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
/// The by-hand rotation readout. The Plan screen's rotation panel starts it against the framing dial's angle
/// and polls it here; these endpoints start, stop, confirm and read it. Nothing in a run depends on it.
/// start: 202; 400 non-finite angle / bad exposure, mode or binning; 409 already running or confirming.
/// confirm: 202; 409 nothing to confirm or already confirming. stop: 204 once the in-flight solve has drained.
/// </summary>
public static class RotationAssistEndpoints {
    public static IEndpointRouteBuilder MapRotationAssistEndpoints(this IEndpointRouteBuilder app) {
        var assist = app.MapGroup("/api/v1/rotation-assist").WithTags("Rotation assist");

        assist.MapPost("/start", async ([FromBody] RotationAssistStartRequestDto request, IRotationAssistService svc, CancellationToken ct) => {
            try {
                await svc.StartAsync(request, ct);
                return Results.Accepted();
            } catch (System.ArgumentException ex) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            } catch (System.InvalidOperationException ex) when (ex is not System.ObjectDisposedException) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
            .Accepts<RotationAssistStartRequestDto>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartRotationAssist")
            .WithSummary("Start the by-hand rotation readout toward a position angle.");

        assist.MapPost("/stop", async (IRotationAssistService svc) => {
            await svc.StopAsync();
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("StopRotationAssist")
            .WithSummary("Stop the by-hand rotation readout.");

        assist.MapPost("/confirm", async (IRotationAssistService svc, CancellationToken ct) => {
            try {
                await svc.ConfirmAsync(ct);
                return Results.Accepted();
            } catch (System.InvalidOperationException ex) when (ex is not System.ObjectDisposedException) {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        })
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ConfirmRotationAssist")
            .WithSummary("Done: stop the readout and check the framing with one 1×1 frame at the full plate-solve exposure.");

        assist.MapGet("/state", (IRotationAssistService svc) => Results.Ok(svc.GetStatus()))
            .Produces<RotationAssistStatusDto>(StatusCodes.Status200OK)
            .WithName("GetRotationAssist")
            .WithSummary("The by-hand rotation readout: target, latest solved angle, delta, history.");

        assist.MapGet("/frame", (IRotationAssistService svc, HttpContext http) => {
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
            .WithName("GetRotationAssistFrame")
            .WithSummary("The latest solved frame of the by-hand rotation readout, rendered (204 until one).");

        return app;
    }
}
