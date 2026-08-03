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
using OpenAstroAra.Server.Services.Video;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §77.2/§77.4 planetary capture surface: enter/leave planetary mode (the Alpaca
/// disconnected-window arbitration), start/stop SER recording, and live status.
/// </summary>
public static class PlanetaryEndpoints {

    public static IEndpointRouteBuilder MapPlanetaryEndpoints(this IEndpointRouteBuilder app) {
        var planetary = app.MapGroup("/api/v1/planetary").WithTags("Planetary");

        planetary.MapPost("/enter", EnterAsync)
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("EnterPlanetaryMode")
            .WithSummary("Detach the camera from the Alpaca surface and enter SDK video mode (§77.2); refused while a sequence run is active.");

        planetary.MapPost("/leave", LeaveAsync)
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithName("LeavePlanetaryMode")
            .WithSummary("Stop any recording, close the SDK handle, and reconnect the Alpaca camera (best-effort).");

        planetary.MapPost("/record/start", StartRecordingAsync)
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartPlanetaryRecording")
            .WithSummary("Start a SER recording with the §77.1 engine (requires planetary mode).");

        planetary.MapPost("/record/stop", StopRecordingAsync)
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithName("StopPlanetaryRecording")
            .WithSummary("Stop the current SER recording; final honest-accounting stats arrive on planetary.recording_stopped.");

        planetary.MapGet("/status", (PlanetaryCaptureService service) => Results.Ok(service.Status()))
            .Produces<PlanetaryStatusDto>(StatusCodes.Status200OK)
            .WithName("GetPlanetaryStatus")
            .WithSummary("Planetary mode + live recording counters (§77.1 honest accounting).");

        return app;
    }

    private static async Task<IResult> EnterAsync(
        PlanetaryEnterRequestDto request, PlanetaryCaptureService service, HttpContext http, CancellationToken ct) {
        try {
            var accepted = await service.EnterAsync(request, IdempotencyKey(http), ct).ConfigureAwait(false);
            return Results.Accepted(value: accepted);
        } catch (InvalidOperationException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> LeaveAsync(
        PlanetaryCaptureService service, HttpContext http, CancellationToken ct) {
        var accepted = await service.LeaveAsync(IdempotencyKey(http), ct).ConfigureAwait(false);
        return Results.Accepted(value: accepted);
    }

    private static async Task<IResult> StartRecordingAsync(
        PlanetaryRecordRequestDto request, PlanetaryCaptureService service, HttpContext http, CancellationToken ct) {
        try {
            var accepted = await service.StartRecordingAsync(request, IdempotencyKey(http), ct).ConfigureAwait(false);
            return Results.Accepted(value: accepted);
        } catch (ArgumentException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        } catch (InvalidOperationException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        } catch (VideoCaptureException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> StopRecordingAsync(
        PlanetaryCaptureService service, HttpContext http, CancellationToken ct) {
        var accepted = await service.StopRecordingAsync(IdempotencyKey(http), ct).ConfigureAwait(false);
        return Results.Accepted(value: accepted);
    }

    private static string? IdempotencyKey(HttpContext http) =>
        http.Request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;
}
