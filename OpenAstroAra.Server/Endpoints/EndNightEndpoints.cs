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

/// <summary>#1901 — End Night: start (returns at once, the work runs on the daemon), read the status, cancel.
/// One action for both shells (ADR 0009 rule 1); it is not the Emergency Stop route (rule 3).</summary>
public static class EndNightEndpoints {

    public static IEndpointRouteBuilder MapEndNightEndpoints(this IEndpointRouteBuilder app) {
        var endNight = app.MapGroup("/api/v1/end-night").WithTags("EndNight");

        // The body is optional; a bare POST also shuts the host down (the client's "Shut down the Pi" choice is on by default).
        endNight.MapPost("", (EndNightStartRequestDto? request, EndNightService svc) => Start(request, svc))
            .Produces<EndNightStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartEndNight")
            .WithSummary("#1901 End Night: abort the run, stop guiding, Find Home, park, disconnect, warm the cooler, then (when chosen) shut the host down. Returns at once; progress is the end_night.progress event and the status read. A second start while one runs is the end-night-in-progress refusal.");

        endNight.MapGet("/status", (EndNightService svc) => Results.Ok(svc.GetStatus()))
            .Produces<EndNightStatusDto>(StatusCodes.Status200OK)
            .WithName("GetEndNightStatus")
            .WithSummary("The state of the End Night action and each of its steps; the last run stays readable until the next start.");

        endNight.MapPost("/cancel", (EndNightService svc) => Results.Ok(svc.Cancel()))
            .Produces<EndNightStatusDto>(StatusCodes.Status200OK)
            .WithName("CancelEndNight")
            .WithSummary("Stop End Night at the next step boundary and leave the rig as it is. Nothing cancels it once the host poweroff has started (cancellable is false in the answer).");

        return app;
    }

    // Extracted (not an inline lambda) so the wiring is unit-testable.
    public static IResult Start(EndNightStartRequestDto? request, EndNightService svc) {
        var status = svc.Start(request?.ShutDownHost ?? true);
        return Results.Accepted("/api/v1/end-night/status", status);
    }
}