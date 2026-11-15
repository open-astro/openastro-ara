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
using System.Globalization;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// ↑ara#1787 Session Live Stack routes under the camera group. start: 202; stop and reset: 204, idempotent; status: 200;
/// frame: 200 image/jpeg, 204 until a light is stacked. A camera service without the capability answers 501.
/// </summary>
public static class SessionLiveStackEndpoints {

    public static IEndpointRouteBuilder MapSessionLiveStackEndpoints(this IEndpointRouteBuilder camera) {
        camera.MapPost("/live-stack/start", (ICameraService svc) => Start(svc));
        camera.MapPost("/live-stack/reset", (ICameraService svc) => Reset(svc));
        camera.MapPost("/live-stack/stop", (ICameraService svc) => Stop(svc));
        camera.MapGet("/live-stack", (ICameraService svc) => Status(svc));
        camera.MapGet("/live-stack/frame", (ICameraService svc, HttpContext http) => Frame(svc, http));
        return camera;
    }

    internal static IResult Start(ICameraService svc) {
        if (svc is not ISessionLiveStackControl c) { return NotImplemented(); }
        c.StartSessionLiveStack();
        return Results.Accepted();
    }

    internal static IResult Reset(ICameraService svc) {
        if (svc is not ISessionLiveStackControl c) { return NotImplemented(); }
        c.ResetSessionLiveStack();
        return Results.NoContent();
    }

    internal static IResult Stop(ICameraService svc) {
        if (svc is not ISessionLiveStackControl c) { return NotImplemented(); }
        c.StopSessionLiveStack();
        return Results.NoContent();
    }

    internal static IResult Status(ICameraService svc) =>
        svc is ISessionLiveStackControl c ? Results.Ok(c.GetSessionLiveStackStatus()) : NotImplemented();

    internal static IResult Frame(ICameraService svc, HttpContext http) {
        if (svc is not ISessionLiveStackControl c) { return NotImplemented(); }
        var frame = c.GetSessionLiveStackFrame();
        if (frame is null) { return Results.NoContent(); }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Stack-Seq"] = frame.Value.Seq.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Stack-Lights"] = frame.Value.Lights.ToString(CultureInfo.InvariantCulture);
        return Results.Bytes(frame.Value.Jpeg, "image/jpeg");
    }

    private static IResult NotImplemented() => Results.Problem(
        type: "https://openastro.net/errors/not-implemented",
        title: "Session live stacking not available",
        statusCode: StatusCodes.Status501NotImplemented,
        detail: "This camera service does not support the session live stack.");
}
