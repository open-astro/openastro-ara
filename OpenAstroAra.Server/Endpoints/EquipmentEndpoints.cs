#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 6 equipment-endpoint registration per PORT_PLAYBOOK.md §10.6.
/// Phase 13.12 wires every device group through its corresponding
/// placeholder service. Each Get returns 404 (no hardware connected);
/// Connects/Disconnects/Actions return 202 <see cref="OperationAcceptedDto"/>.
/// Real ASCOM Alpaca drivers land per-device in the real-infra phase + Phase 14.
///
/// Idempotency-Key header is read where §60.5 requires it. Long-running
/// ops return 202 with operation accepted; progress arrives via WS.
/// </summary>
public static class EquipmentEndpoints {

    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app) {
        var equipment = app.MapGroup("/api/v1/equipment").WithTags("Equipment");

        // Discovery (shared across device types) — already functional via
        // AlpacaEquipmentDiscoveryService since Phase 6.
        equipment.MapGet("/discover/{type}", async (string type, bool? forceRefresh, IEquipmentDiscoveryService svc, CancellationToken ct) => {
            if (!Enum.TryParse<DeviceType>(type, ignoreCase: true, out var deviceType)) {
                return Results.Problem(
                    type: "https://openastro.net/errors/unknown-device-type",
                    title: "Unknown device type",
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: $"'{type}' is not a recognized device type. See §10.6 for the list.");
            }
            var devices = await svc.DiscoverAsync(deviceType, forceRefresh ?? false, ct);
            return Results.Ok(devices);
        });

        // ─── Camera ───
        var camera = equipment.MapGroup("/camera");
        camera.MapGet("", async (ICameraService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        camera.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        camera.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        camera.MapPost("/exposure", async ([FromBody] ExposureRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StartExposureAsync(request, key, ct)));
        camera.MapPost("/exposure/abort", async (ICameraService svc, CancellationToken ct) => {
            await svc.AbortExposureAsync(ct); return Results.Accepted();
        });
        camera.MapPost("/cooler", async ([FromBody] CameraCoolerRequestDto request, ICameraService svc, CancellationToken ct) => {
            await svc.SetCoolerAsync(request.Enabled, request.TargetTemperatureC, ct);
            return Results.Accepted();
        });
        // §64 Live View — short-exposure framing/focus loop (no catalog write).
        camera.MapPost("/liveview/start", async ([FromBody] LiveViewStartRequestDto request, ICameraService svc, CancellationToken ct) => {
            try {
                await svc.StartLiveViewAsync(request, ct);
                return Results.Accepted();
            } catch (ArgumentException ex) {
                // Invalid exposure/binning request.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            } catch (InvalidOperationException ex) {
                // Not connected, already running, or an incompatible (tri-colour) sensor.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        camera.MapPost("/liveview/stop", async (ICameraService svc) => {
            // 204, not 202: StopLiveViewAsync awaits the loop fully draining before returning, so the
            // stop is confirmed complete by the time this responds (unlike /start, which is async).
            // No ct: a stop is unconditional and never honors cancellation. It can take up to the
            // exposure cap (LiveViewMaxExposureSec, 15 s) + readout while an in-flight, non-cancellable
            // ImageArray download finishes — clients should allow a ~30 s timeout on this call.
            await svc.StopLiveViewAsync();
            return Results.NoContent();
        });
        camera.MapGet("/liveview", (ICameraService svc) => Results.Ok(svc.GetLiveViewStatus()));
        camera.MapGet("/liveview/frame", (ICameraService svc, HttpContext http) => {
            var frame = svc.GetLiveViewFrame();
            if (frame is null) {
                return Results.NoContent();
            }
            // Live frames are ephemeral — never let a proxy/client cache one and serve it stale.
            http.Response.Headers.CacheControl = "no-store";
            // Expose the frame's sequence + session so a poller can change-detect in a single request
            // (no separate GET /liveview round-trip). A changed X-Live-Session means a new session
            // (FrameSeq restarts at 1), so the poller treats it as new rather than a seq regression.
            http.Response.Headers["X-Frame-Seq"] =
                frame.Value.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Live-Session"] =
                frame.Value.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Bytes(frame.Value.Jpeg, "image/jpeg");
        });

        // ─── Telescope ───
        var telescope = equipment.MapGroup("/telescope");
        telescope.MapGet("", async (ITelescopeService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        telescope.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        telescope.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        telescope.MapPost("/slew", async ([FromBody] SlewRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.SlewAsync(request, key, ct)));
        // Park's body (an optional Reason) is nullable so a bodyless POST works — the panel's
        // Park button sends no body, and a required [FromBody] would 400/415 it (the bug where
        // "Park does nothing"). Reason defaults to null when absent.
        telescope.MapPost("/park", async ([FromBody] ParkRequestDto? request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ParkAsync(request ?? new ParkRequestDto(), key, ct)));
        telescope.MapPost("/unpark", async ([FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.UnparkAsync(key, ct)));
        telescope.MapPost("/home", async ([FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.FindHomeAsync(key, ct)));
        telescope.MapPost("/abort", async (ITelescopeService svc, CancellationToken ct) => {
            await svc.AbortSlewAsync(ct); return Results.Accepted();
        });
        // Manual nudge (direction pad): start (rate != 0) / stop (rate 0) one axis. /abort halts all axes.
        telescope.MapPost("/moveaxis", async ([FromBody] MoveAxisRequestDto request, ITelescopeService svc, CancellationToken ct) => {
            // ASCOM axes: 0 = Primary, 1 = Secondary, 2 = Tertiary. Reject out-of-range up front (400)
            // so an invalid enum cast can't surface as a driver 500. Rate is intentionally NOT clamped
            // here: the UI speed picker constrains it to the mount's reported AxisRates, a direct API
            // caller owns its choice of rate, and the driver clamps/rejects anything it can't honour.
            if (request.Axis is < 0 or > 2) {
                return Results.Problem($"axis must be 0, 1, or 2 (got {request.Axis}).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            await svc.MoveAxisAsync(request.Axis, request.Rate, ct);
            return Results.Accepted();
        });
        telescope.MapPost("/tracking", async ([FromBody] TelescopeTrackingRequestDto request, ITelescopeService svc, CancellationToken ct) => {
            await svc.SetTrackingAsync(request.Enabled, ct); return Results.Accepted();
        });

        // ─── Focuser ───
        var focuser = equipment.MapGroup("/focuser");
        focuser.MapGet("", async (IFocuserService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        focuser.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        focuser.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        focuser.MapPost("/move", async ([FromBody] FocuserMoveRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.MoveAsync(request, key, ct)));
        // §59 — run one autofocus V-curve sweep with the profile's §37.11 settings, as a
        // §65.5 background job (a sweep is minutes of focuser moves + probe exposures — far too
        // long for a request). Returns 202 + the job; poll GET /api/v1/jobs/{id} for state, and
        // a failed sweep (unusable fit, starless probes, focuser fault) surfaces as a Failed job
        // with the reason in error_message. One sweep runs at a time (the service serializes;
        // a queued second job simply waits its turn).
        focuser.MapPost("/autofocus", (
                OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor autofocus,
                IBatchJobService jobs,
                IProfileStore profiles) => {
            // Real progress: the job's total is the sweep's probe count (from the
            // profile's §37.11 settings), and the sweep reports structured
            // Progress/MaxProgress per probe — a polling client sees 3/9, not 0→1.
            var af = profiles.GetAutofocusSettings();
            var totalProbes = af.Steps >= 1 ? af.Steps * 2 + 1 : 1;
            var job = jobs.Enqueue("autofocus", totalSteps: totalProbes, async (tick, ct) => {
                var progress = new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(s => {
                    if (s.MaxProgress > 0 && s.Progress > 0) {
                        tick((int)s.Progress);
                    }
                });
                var ok = await autofocus.RunAutofocusAsync(progress, ct);
                if (!ok) {
                    throw new InvalidOperationException(
                        "Autofocus sweep failed — see the daemon log (probe quality, curve fit, or focuser fault).");
                }
                tick(totalProbes); // settle at total even if the last report raced
            });
            return Results.Accepted($"/api/v1/jobs/{job.JobId}", job);
        })
            .Produces<BatchJobDto>(StatusCodes.Status202Accepted)
            .WithName("RunAutofocus");

        // ─── FilterWheel ───
        var filterwheel = equipment.MapGroup("/filterwheel");
        filterwheel.MapGet("", async (IFilterWheelService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        filterwheel.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        filterwheel.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        filterwheel.MapPost("/change", async ([FromBody] FilterChangeRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ChangeFilterAsync(request, key, ct)));

        // ─── Rotator ───
        var rotator = equipment.MapGroup("/rotator");
        rotator.MapGet("", async (IRotatorService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        rotator.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        rotator.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        rotator.MapPost("/move", async ([FromBody] RotatorMoveRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.MoveAsync(request, key, ct)));
        rotator.MapPost("/reverse", async ([FromBody] RotatorReverseRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.SetReverseAsync(request, key, ct)));
        rotator.MapPost("/sync", async ([FromBody] RotatorSyncRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.SyncAsync(request, key, ct)));

        // ─── Dome ───
        var dome = equipment.MapGroup("/dome");
        dome.MapGet("", async (IDomeService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        dome.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        dome.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        dome.MapPost("/slew", async ([FromBody] DomeSlewRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.SlewAsync(request, key, ct)));
        dome.MapPost("/park", async ([FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ParkAsync(key, ct)));
        dome.MapPost("/shutter/open", async ([FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.OpenShutterAsync(key, ct)));
        dome.MapPost("/shutter/close", async ([FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.CloseShutterAsync(key, ct)));

        // ─── Switch (multi-instance, addressed by Alpaca device number — the {n}) ───
        var sw = equipment.MapGroup("/switch");
        // List every connected/known switch ([] when none).
        sw.MapGet("", async (ISwitchService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)));
        // One switch by device number (404 when not present).
        sw.MapGet("/{n:int}", async (int n, ISwitchService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(n, ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        // Connect an ADDITIONAL switch (no longer evicts the others). The device's AlpacaDeviceNumber
        // (in the body's DiscoveredDeviceDto) becomes its address.
        sw.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISwitchService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        // Disconnect is idempotent (like every device's disconnect): an unknown/already-disconnected
        // device number is a no-op 202, not a 404.
        sw.MapPost("/{n:int}/disconnect", async (int n, [FromHeader(Name = "Idempotency-Key")] string? key, ISwitchService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(n, key, ct)));
        // A write needs a live switch, so the errors map (per the file's convention): an out-of-range
        // PortId → 400; no connected switch at {n} (unknown number or not Connected) → 409 Conflict —
        // instead of an opaque 500.
        sw.MapPost("/{n:int}/value", async (int n, [FromBody] SwitchValueRequestDto request, ISwitchService svc, CancellationToken ct) => {
            try {
                await svc.SetValueAsync(n, request, ct);
                return Results.Accepted();
            } catch (System.ArgumentException ex) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            } catch (System.InvalidOperationException ex) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // ─── ObservingConditions ───
        var oc = equipment.MapGroup("/observingconditions");
        oc.MapGet("", async (IObservingConditionsService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        oc.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IObservingConditionsService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        oc.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IObservingConditionsService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));

        // ─── SafetyMonitor ───
        var safety = equipment.MapGroup("/safetymonitor");
        safety.MapGet("", async (ISafetyMonitorService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        safety.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISafetyMonitorService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        safety.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ISafetyMonitorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));

        // ─── FlatDevice ───
        var flat = equipment.MapGroup("/flatdevice");
        flat.MapGet("", async (IFlatDeviceService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        flat.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFlatDeviceService svc, IEquipmentSelectionStore selectionStore, CancellationToken ct) =>
            await ConnectAndRememberAsync(request, selectionStore, () => svc.ConnectAsync(request, key, ct), ct));
        flat.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IFlatDeviceService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        flat.MapPost("/apply", async ([FromBody] FlatPanelRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFlatDeviceService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ApplyFlatPanelAsync(request, key, ct)));

        // ─── Guider (PHD2) ───
        var guider = equipment.MapGroup("/guider");
        guider.MapGet("", async (IGuiderService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        guider.MapPost("/connect", async ([FromBody] GuiderConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        guider.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        guider.MapPost("/start", async ([FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StartGuidingAsync(key, ct)));
        guider.MapPost("/stop", async ([FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StopGuidingAsync(key, ct)));
        guider.MapPost("/dither", async (double pixels, [FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DitherAsync(pixels, key, ct)));

        // §63.6 dark library: build (202, long-running ~minutes; WS reports guider.dark_library.started/complete)
        // and a status read for the wizard's "Build dark library" affordance.
        guider.MapPost("/darklibrary/build", async ([FromBody] BuildDarkLibraryRequestDto request,
                [FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            await BuildDarkLibraryAsync(request, key, svc, ct));
        // Always 200 with a {connected, status} envelope so a client polling to drive the build affordance can
        // tell "guider not connected" (connected:false) apart from a missing route (404).
        guider.MapGet("/darklibrary/status", async (IGuiderService svc, CancellationToken ct) => {
            var dto = await svc.GetCalibrationFilesStatusAsync(ct);
            return Results.Ok(new CalibrationFilesStatusResponseDto(Connected: dto is not null, Status: dto));
        });
        // §63.6 defect map: build (202, long-running; WS guider.defect_map.started/complete/failed). Shares the
        // single calibration-build gate with the dark-library build; the status read above already covers the
        // defect-map fields, so there's no separate defect-map status endpoint.
        guider.MapPost("/defectmap/build", async ([FromBody] BuildDefectMapDarksRequestDto request,
                [FromHeader(Name = "Idempotency-Key")] string? key, IGuiderService svc, CancellationToken ct) =>
            await BuildDefectMapDarksAsync(request, key, svc, ct));
        // §63.6 enable/disable the loaded calibration artifacts — synchronous (returns the updated status), not a
        // 202 build. Disconnected → 409; the daemon rejecting the toggle (e.g. enable with no camera) → 422.
        guider.MapPost("/darklibrary/enabled", async ([FromBody] SetCalibrationEnabledRequestDto request, IGuiderService svc, CancellationToken ct) =>
            await SetDarkLibraryEnabledAsync(request, svc, ct));
        guider.MapPost("/defectmap/enabled", async ([FromBody] SetCalibrationEnabledRequestDto request, IGuiderService svc, CancellationToken ct) =>
            await SetDefectMapEnabledAsync(request, svc, ct));

        // ─── Polar Alignment ───
        var polar = equipment.MapGroup("/polaralign");
        polar.MapGet("/status", async (IPolarAlignService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetStatusAsync(ct)));
        polar.MapPost("/start", async ([FromHeader(Name = "Idempotency-Key")] string? key, IPolarAlignService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StartAsync(key, ct)));
        polar.MapPost("/stop", async ([FromHeader(Name = "Idempotency-Key")] string? key, IPolarAlignService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StopAsync(key, ct)));

        // ─── Manual reconnect (§52.1) ───
        // Reconnect the remembered device(s) for the type without re-running discovery (the same
        // path auto-connect-on-boot uses). 202 when at least one connect was dispatched; 404 when
        // nothing has ever been connected for the type (so there's nothing to reconnect). Switch
        // reconnects every remembered switch. Guider has no entry — it connects via PHD2, not this flow.
        camera.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Camera, ct));
        telescope.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Telescope, ct));
        focuser.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Focuser, ct));
        filterwheel.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.FilterWheel, ct));
        rotator.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Rotator, ct));
        dome.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Dome, ct));
        sw.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.Switch, ct));
        oc.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.ObservingConditions, ct));
        safety.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.SafetyMonitor, ct));
        flat.MapPost("/reconnect", (IEquipmentReconnector r, CancellationToken ct) => ReconnectAsync(r, DeviceType.CoverCalibrator, ct));

        return app;
    }

    // Manual reconnect helper: dispatch a connect to the remembered device(s) for the type.
    //   404 Not Found  — nothing remembered for the type yet (connect it once via /connect first).
    //   202 Accepted   — at least one connect was dispatched; each connects in the background.
    //   502 Bad Gateway — devices were remembered but every dispatch threw synchronously (e.g. all
    //                     their Alpaca servers are down on a rig restart), so don't claim "reconnecting".
    private static async Task<IResult> ReconnectAsync(IEquipmentReconnector reconnector, DeviceType type, CancellationToken ct) {
        var outcome = await reconnector.ReconnectAsync(type, ct);
        if (outcome.Attempted == 0) {
            return Results.NotFound();
        }
        return outcome.Dispatched > 0
            ? Results.Accepted()
            : Results.Problem(
                "Every remembered device failed to reconnect — check the devices are powered on and reachable.",
                statusCode: StatusCodes.Status502BadGateway);
    }

    // Extracted (not an inline lambda) so the validation/connection error mapping is unit-testable with a mocked
    // service. §63.6: a bad request (out-of-range frame count / exposure bound, or min > max) throws
    // ArgumentException → 400; a disconnected guider throws InvalidOperationException → 409 Conflict.
    public static async Task<IResult> BuildDarkLibraryAsync(
            BuildDarkLibraryRequestDto request, string? idempotencyKey, IGuiderService svc, CancellationToken ct) {
        try {
            return Results.Accepted(value: await svc.BuildDarkLibraryAsync(request, idempotencyKey, ct));
        } catch (System.ArgumentException ex) {
            // Covers ArgumentOutOfRangeException (frame count / exposure bounds) and the min > max ArgumentException.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        } catch (System.InvalidOperationException ex) {
            // Guider not connected — can't accept a build it can't run.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // §63.6 defect-map twin of BuildDarkLibraryAsync — same validation/connection error mapping (400 / 409).
    // The 409 also covers "a calibration build is already in progress" (the shared single-build gate).
    public static async Task<IResult> BuildDefectMapDarksAsync(
            BuildDefectMapDarksRequestDto request, string? idempotencyKey, IGuiderService svc, CancellationToken ct) {
        try {
            return Results.Accepted(value: await svc.BuildDefectMapDarksAsync(request, idempotencyKey, ct));
        } catch (System.ArgumentException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        } catch (System.InvalidOperationException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // §63.6 calibration enable/disable handlers (extracted for the error-mapping tests). 200 with the updated
    // status; disconnected guider → 409; daemon rejected the toggle (e.g. enable with no camera) → 422.
    public static Task<IResult> SetDarkLibraryEnabledAsync(SetCalibrationEnabledRequestDto request, IGuiderService svc, CancellationToken ct) =>
        ToggleCalibrationAsync(() => svc.SetDarkLibraryEnabledAsync(request.Enabled, ct));

    public static Task<IResult> SetDefectMapEnabledAsync(SetCalibrationEnabledRequestDto request, IGuiderService svc, CancellationToken ct) =>
        ToggleCalibrationAsync(() => svc.SetDefectMapEnabledAsync(request.Enabled, ct));

    private static async Task<IResult> ToggleCalibrationAsync(System.Func<Task<CalibrationFilesStatusDto>> toggle) {
        try {
            return Results.Ok(await toggle());
        } catch (System.InvalidOperationException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        } catch (OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderRpcException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    // Connect an Alpaca device, then remember it for auto-connect-on-boot. Alpaca is open by design
    // (§52/§67 trusted LAN) — ARA connects any standard Alpaca device without a version/protection
    // handshake. Extracted so the connect+remember flow is shared across every device type and unit-
    // testable. NOT applied to the guider — it's the PHD2 daemon, not an Alpaca device.
    public static async Task<IResult> ConnectAndRememberAsync(
            ConnectRequestDto request, IEquipmentSelectionStore selectionStore,
            System.Func<Task<OperationAcceptedDto>> connect, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var accepted = await connect();
        // §52.1 — remember the device so EquipmentAutoConnectService can re-establish it after a
        // daemon restart. Persist with None, not the request token: the connect is already accepted,
        // so a request cancellation racing in here must not surface as a 5xx for a succeeded op.
        await selectionStore.RememberAsync(request.Device, CancellationToken.None);
        return Results.Accepted(value: accepted);
    }
}
