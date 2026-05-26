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
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 6 equipment-endpoint registration per PORT_PLAYBOOK.md §10.6.
///
/// Each device type follows the same shape (Get + Connect + Disconnect + device-specific operations).
/// Endpoint bodies are stubs that return 501 NotImplemented until the corresponding
/// service implementations land. The endpoint surface itself is defined so the
/// OpenAPI document + WILMA client codegen can target it today; service impls
/// fill in incrementally.
///
/// Idempotency-Key header is read where §60.5 requires it. Long-running ops
/// return 202 with <see cref="OperationAcceptedDto"/>; progress arrives via WS.
/// </summary>
public static class EquipmentEndpoints {

    private static IResult NotImplementedStub(string endpoint) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 6's incremental implementation. Stub registered so the OpenAPI surface is stable; service wiring lands per device type.");

    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app) {
        var equipment = app.MapGroup("/api/v1/equipment").WithTags("Equipment");

        // Discovery (shared across device types).
        //
        // Route is /discover/{type} — NOT /{type} — to avoid colliding with the
        // per-device literal-route groups below (e.g. GET /api/v1/equipment/camera
        // maps to the per-device current-state stub, not discovery). The §10.6
        // table reads "GET /api/v1/equipment/{type}" in shorthand; the actual
        // wire path is disambiguated via /discover/ so all DeviceType values
        // (incl. camera/telescope/etc. that have dedicated groups) are reachable.
        // openapi.yaml + design/PORT_PLAYBOOK.md §10.6 row 1 both updated to match.
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
        camera.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/camera"));
        camera.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/camera/connect"));
        camera.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/camera/disconnect"));
        camera.MapPost("/exposure", () => NotImplementedStub("POST /api/v1/equipment/camera/exposure"));
        camera.MapPost("/exposure/abort", () => NotImplementedStub("POST /api/v1/equipment/camera/exposure/abort"));

        // ─── Telescope ───
        var telescope = equipment.MapGroup("/telescope");
        telescope.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/telescope"));
        telescope.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/telescope/connect"));
        telescope.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/telescope/disconnect"));
        telescope.MapPost("/slew", () => NotImplementedStub("POST /api/v1/equipment/telescope/slew"));
        telescope.MapPost("/park", () => NotImplementedStub("POST /api/v1/equipment/telescope/park"));
        telescope.MapPost("/unpark", () => NotImplementedStub("POST /api/v1/equipment/telescope/unpark"));
        telescope.MapPost("/abort", () => NotImplementedStub("POST /api/v1/equipment/telescope/abort"));

        // ─── Focuser ───
        var focuser = equipment.MapGroup("/focuser");
        focuser.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/focuser"));
        focuser.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/focuser/connect"));
        focuser.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/focuser/disconnect"));
        focuser.MapPost("/move", () => NotImplementedStub("POST /api/v1/equipment/focuser/move"));

        // ─── FilterWheel ───
        var filterwheel = equipment.MapGroup("/filterwheel");
        filterwheel.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/filterwheel"));
        filterwheel.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/filterwheel/connect"));
        filterwheel.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/filterwheel/disconnect"));
        filterwheel.MapPost("/change", () => NotImplementedStub("POST /api/v1/equipment/filterwheel/change"));

        // ─── Rotator ───
        var rotator = equipment.MapGroup("/rotator");
        rotator.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/rotator"));
        rotator.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/rotator/connect"));
        rotator.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/rotator/disconnect"));
        rotator.MapPost("/move", () => NotImplementedStub("POST /api/v1/equipment/rotator/move"));

        // ─── Dome ───
        var dome = equipment.MapGroup("/dome");
        dome.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/dome"));
        dome.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/dome/connect"));
        dome.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/dome/disconnect"));
        dome.MapPost("/slew", () => NotImplementedStub("POST /api/v1/equipment/dome/slew"));
        dome.MapPost("/park", () => NotImplementedStub("POST /api/v1/equipment/dome/park"));
        dome.MapPost("/shutter/open", () => NotImplementedStub("POST /api/v1/equipment/dome/shutter/open"));
        dome.MapPost("/shutter/close", () => NotImplementedStub("POST /api/v1/equipment/dome/shutter/close"));

        // ─── Switch ───
        var sw = equipment.MapGroup("/switch");
        sw.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/switch"));
        sw.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/switch/connect"));
        sw.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/switch/disconnect"));
        sw.MapPost("/value", () => NotImplementedStub("POST /api/v1/equipment/switch/value"));

        // ─── ObservingConditions ───
        var oc = equipment.MapGroup("/observingconditions");
        oc.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/observingconditions"));
        oc.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/observingconditions/connect"));
        oc.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/observingconditions/disconnect"));

        // ─── SafetyMonitor ───
        var safety = equipment.MapGroup("/safetymonitor");
        safety.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/safetymonitor"));
        safety.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/safetymonitor/connect"));
        safety.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/safetymonitor/disconnect"));

        // ─── FlatDevice ───
        var flat = equipment.MapGroup("/flatdevice");
        flat.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/flatdevice"));
        flat.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/flatdevice/connect"));
        flat.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/flatdevice/disconnect"));
        flat.MapPost("/apply", () => NotImplementedStub("POST /api/v1/equipment/flatdevice/apply"));

        // ─── Guider (PHD2) ───
        var guider = equipment.MapGroup("/guider");
        guider.MapGet("", () => NotImplementedStub("GET /api/v1/equipment/guider"));
        guider.MapPost("/connect", () => NotImplementedStub("POST /api/v1/equipment/guider/connect"));
        guider.MapPost("/disconnect", () => NotImplementedStub("POST /api/v1/equipment/guider/disconnect"));
        guider.MapPost("/start", () => NotImplementedStub("POST /api/v1/equipment/guider/start"));
        guider.MapPost("/stop", () => NotImplementedStub("POST /api/v1/equipment/guider/stop"));
        guider.MapPost("/dither", () => NotImplementedStub("POST /api/v1/equipment/guider/dither"));

        // ─── Polar Alignment ───
        var polar = equipment.MapGroup("/polaralign");
        polar.MapGet("/status", () => NotImplementedStub("GET /api/v1/equipment/polaralign/status"));
        polar.MapPost("/start", () => NotImplementedStub("POST /api/v1/equipment/polaralign/start"));
        polar.MapPost("/stop", () => NotImplementedStub("POST /api/v1/equipment/polaralign/stop"));

        return app;
    }
}
