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
/// §65.5 / §60.5 background-job introspection endpoints. The actual
/// enqueue happens via domain-specific endpoints (e.g.,
/// /sessions/{id}/restretch); these endpoints let WILMA poll status
/// + cancel.
/// </summary>
public static class JobsEndpoints {
    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app) {
        var jobs = app.MapGroup("/api/v1/jobs").WithTags("Jobs");

        jobs.MapGet("/{id:guid}",
                (Guid id, IBatchJobService svc) => {
                    var state = svc.Get(id);
                    return state is null ? Results.NotFound() : Results.Ok(state);
                })
            .Produces<BatchJobDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetJobStatus");

        jobs.MapDelete("/{id:guid}",
                (Guid id, IBatchJobService svc) => {
                    if (svc.Get(id) is null) return Results.NotFound();
                    svc.TryCancel(id);
                    return Results.NoContent();
                })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CancelJob");

        return app;
    }
}