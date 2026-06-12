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
        camera.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        camera.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        camera.MapPost("/exposure", async ([FromBody] ExposureRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ICameraService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StartExposureAsync(request, key, ct)));
        camera.MapPost("/exposure/abort", async (ICameraService svc, CancellationToken ct) => {
            await svc.AbortExposureAsync(ct); return Results.Accepted();
        });

        // ─── Telescope ───
        var telescope = equipment.MapGroup("/telescope");
        telescope.MapGet("", async (ITelescopeService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        telescope.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        telescope.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        telescope.MapPost("/slew", async ([FromBody] SlewRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.SlewAsync(request, key, ct)));
        telescope.MapPost("/park", async ([FromBody] ParkRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ParkAsync(request, key, ct)));
        telescope.MapPost("/unpark", async ([FromHeader(Name = "Idempotency-Key")] string? key, ITelescopeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.UnparkAsync(key, ct)));
        telescope.MapPost("/abort", async (ITelescopeService svc, CancellationToken ct) => {
            await svc.AbortSlewAsync(ct); return Results.Accepted();
        });

        // ─── Focuser ───
        var focuser = equipment.MapGroup("/focuser");
        focuser.MapGet("", async (IFocuserService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        focuser.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        focuser.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        focuser.MapPost("/move", async ([FromBody] FocuserMoveRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFocuserService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.MoveAsync(request, key, ct)));

        // ─── FilterWheel ───
        var filterwheel = equipment.MapGroup("/filterwheel");
        filterwheel.MapGet("", async (IFilterWheelService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        filterwheel.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        filterwheel.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        filterwheel.MapPost("/change", async ([FromBody] FilterChangeRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFilterWheelService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ChangeFilterAsync(request, key, ct)));

        // ─── Rotator ───
        var rotator = equipment.MapGroup("/rotator");
        rotator.MapGet("", async (IRotatorService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        rotator.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        rotator.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        rotator.MapPost("/move", async ([FromBody] RotatorMoveRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IRotatorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.MoveAsync(request, key, ct)));

        // ─── Dome ───
        var dome = equipment.MapGroup("/dome");
        dome.MapGet("", async (IDomeService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        dome.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IDomeService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
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

        // ─── Switch ───
        var sw = equipment.MapGroup("/switch");
        sw.MapGet("", async (ISwitchService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        sw.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISwitchService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        sw.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ISwitchService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));
        sw.MapPost("/value", async ([FromBody] SwitchValueRequestDto request, ISwitchService svc, CancellationToken ct) => {
            await svc.SetValueAsync(request, ct); return Results.Accepted();
        });

        // ─── ObservingConditions ───
        var oc = equipment.MapGroup("/observingconditions");
        oc.MapGet("", async (IObservingConditionsService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        oc.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IObservingConditionsService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        oc.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, IObservingConditionsService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));

        // ─── SafetyMonitor ───
        var safety = equipment.MapGroup("/safetymonitor");
        safety.MapGet("", async (ISafetyMonitorService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        safety.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISafetyMonitorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
        safety.MapPost("/disconnect", async ([FromHeader(Name = "Idempotency-Key")] string? key, ISafetyMonitorService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.DisconnectAsync(key, ct)));

        // ─── FlatDevice ───
        var flat = equipment.MapGroup("/flatdevice");
        flat.MapGet("", async (IFlatDeviceService svc, CancellationToken ct) => {
            var dto = await svc.GetAsync(ct); return dto is null ? Results.NotFound() : Results.Ok(dto);
        });
        flat.MapPost("/connect", async ([FromBody] ConnectRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IFlatDeviceService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.ConnectAsync(request, key, ct)));
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

        // ─── Polar Alignment ───
        var polar = equipment.MapGroup("/polaralign");
        polar.MapGet("/status", async (IPolarAlignService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetStatusAsync(ct)));
        polar.MapPost("/start", async ([FromHeader(Name = "Idempotency-Key")] string? key, IPolarAlignService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StartAsync(key, ct)));
        polar.MapPost("/stop", async ([FromHeader(Name = "Idempotency-Key")] string? key, IPolarAlignService svc, CancellationToken ct) =>
            Results.Accepted(value: await svc.StopAsync(key, ct)));

        return app;
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
}
