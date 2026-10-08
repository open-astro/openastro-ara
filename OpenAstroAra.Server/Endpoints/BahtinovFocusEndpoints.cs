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
using System.Threading;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// #1299 — the Bahtinov mask focus readout (Setup → Smart Focus → Main telescope). The client starts it,
/// polls the state and the star crop, and stops it; nothing in a run depends on it.
/// start: 202; 400 bad exposure or binning; 409 already running, or an autofocus run or a sequence has the
/// camera. stop: 204 once the in-flight frame has drained.
/// </summary>
public static class BahtinovFocusEndpoints {
    public static IEndpointRouteBuilder MapBahtinovFocusEndpoints(this IEndpointRouteBuilder app) {
        var bahtinov = app.MapGroup("/api/v1/bahtinov-focus").WithTags("Bahtinov focus");

        bahtinov.MapPost("/start", async ([FromBody] BahtinovFocusStartRequestDto request, IBahtinovFocusService svc, CancellationToken ct) => {
            try {
                await svc.StartAsync(request, ct);
                return Results.Accepted();
            } catch (System.ArgumentException ex) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            } catch (System.InvalidOperationException ex) when (ex is not System.ObjectDisposedException) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
            .Accepts<BahtinovFocusStartRequestDto>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartBahtinovFocus")
            .WithSummary("Start the Bahtinov mask focus readout on the main camera.");

        bahtinov.MapPost("/stop", async (IBahtinovFocusService svc) => {
            await svc.StopAsync();
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("StopBahtinovFocus")
            .WithSummary("Stop the Bahtinov mask focus readout.");

        bahtinov.MapGet("/state", (IBahtinovFocusService svc) => Results.Ok(svc.GetStatus()))
            .Produces<BahtinovFocusStatusDto>(StatusCodes.Status200OK)
            .WithName("GetBahtinovFocus")
            .WithSummary("The Bahtinov readout: latest spike offset, focus zone, trend.");

        bahtinov.MapGet("/frame", (IBahtinovFocusService svc, HttpContext http) => {
            var frame = svc.GetFrame();
            if (frame is null) {
                return Results.NoContent();
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Frame-Seq"] = frame.Value.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Bytes(frame.Value.Jpeg, "image/jpeg");
        })
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status204NoContent)
            .WithName("GetBahtinovFocusFrame")
            .WithSummary("The latest Bahtinov frame: the star crop the overlay draws on, or the whole frame (204 until one).");

        return app;
    }
}
