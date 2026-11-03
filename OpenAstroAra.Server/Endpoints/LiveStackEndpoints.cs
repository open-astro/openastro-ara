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
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// ↑ara#1169 live stacking routes (PORT_PLAYBOOK §64.17) under the camera group, beside Live View.
/// start: 202; 409 Live View not active, OSC at 1x1, or the known frame size is over the cap. reset and stop: 204,
/// idempotent. status: 200. frame: 200 image/jpeg, 204 when nothing is stacked yet. A camera service that is not a
/// <see cref="CameraService"/> (tests, the placeholder) answers 501.
/// </summary>
public static class LiveStackEndpoints {

    public static IEndpointRouteBuilder MapLiveStackEndpoints(this IEndpointRouteBuilder camera) {
        camera.MapPost("/liveview/stack/start", (ICameraService svc) => Start(svc));
        camera.MapPost("/liveview/stack/reset", (ICameraService svc) => Reset(svc));
        camera.MapPost("/liveview/stack/stop", (ICameraService svc) => Stop(svc));
        camera.MapGet("/liveview/stack", (ICameraService svc) => Status(svc));
        camera.MapGet("/liveview/stack/frame", (ICameraService svc, HttpContext http) => Frame(svc, http));
        return camera;
    }

    internal static IResult Start(ICameraService svc) {
        if (svc is not ILiveStackControl c) { return NotImplemented(); }
        try {
            c.StartLiveStack();
            return Results.Accepted();
        } catch (InvalidOperationException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    internal static IResult Reset(ICameraService svc) {
        if (svc is not ILiveStackControl c) { return NotImplemented(); }
        c.ResetLiveStack();
        return Results.NoContent();
    }

    internal static IResult Stop(ICameraService svc) {
        if (svc is not ILiveStackControl c) { return NotImplemented(); }
        c.StopLiveStack();
        return Results.NoContent();
    }

    internal static IResult Status(ICameraService svc) =>
        svc is ILiveStackControl c ? Results.Ok(c.GetLiveStackStatus()) : NotImplemented();

    internal static IResult Frame(ICameraService svc, HttpContext http) {
        if (svc is not ILiveStackControl c) { return NotImplemented(); }
        var frame = c.GetLiveStackFrame();
        if (frame is null) { return Results.NoContent(); }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Stack-Seq"] = frame.Value.Seq.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Live-Session"] = frame.Value.SessionId.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Stack-Frames"] = frame.Value.Frames.ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(frame.Value.Jpeg, "image/jpeg");
    }

    private static IResult NotImplemented() => Results.Problem(
        type: "https://openastro.net/errors/not-implemented",
        title: "Live stacking not available",
        statusCode: StatusCodes.Status501NotImplemented,
        detail: "This camera service does not support live stacking.");
}
