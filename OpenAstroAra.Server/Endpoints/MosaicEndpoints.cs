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
/// Phase 7 mosaic endpoints per PORT_PLAYBOOK.md §10.7 + §47.
/// Phase 13.14 wires every route to <see cref="IMosaicService"/>
/// (placeholder today; real planner + per-panel progress lands
/// alongside the §38 sequence orchestrator).
/// </summary>
public static class MosaicEndpoints {

    public static IEndpointRouteBuilder MapMosaicEndpoints(this IEndpointRouteBuilder app) {
        var mosaic = app.MapGroup("/api/v1/mosaics").WithTags("Mosaics");

        mosaic.MapGet("",
                async (int? limit, string? cursor, IMosaicService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(limit ?? 50, cursor, ct)))
              .Produces<CursorPage<MosaicDto>>(StatusCodes.Status200OK)
              .WithName("ListMosaics");

        mosaic.MapGet("/{id:guid}",
                async (Guid id, IMosaicService svc, CancellationToken ct) => {
                    var dto = await svc.GetAsync(id, ct);
                    return dto is null ? Results.NotFound() : Results.Ok(dto);
                })
              .Produces<MosaicDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("GetMosaic");

        mosaic.MapPost("",
                async ([FromBody] MosaicCreateRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IMosaicService svc, CancellationToken ct) => {
                    var dto = await svc.CreateAsync(request, key, ct);
                    return Results.Created($"/api/v1/mosaics/{dto.Id}", dto);
                })
              .Accepts<MosaicCreateRequestDto>("application/json")
              .Produces<MosaicDto>(StatusCodes.Status201Created)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("CreateMosaic");

        mosaic.MapDelete("/{id:guid}",
                async (Guid id, IMosaicService svc, CancellationToken ct) => {
                    var ok = await svc.DeleteAsync(id, ct);
                    return ok ? Results.NoContent() : Results.NotFound();
                })
              .Produces(StatusCodes.Status204NoContent)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("DeleteMosaic");

        mosaic.MapGet("/{id:guid}/panels",
                async (Guid id, IMosaicService svc, CancellationToken ct) => {
                    // Mirror the /sessions/{id}/frames 404-on-unknown pattern:
                    // existence-check first, return 404 if the mosaic doesn't
                    // exist rather than 200+[]
                    var m = await svc.GetAsync(id, ct);
                    if (m is null) return Results.NotFound();
                    return Results.Ok(await svc.GetPanelsAsync(id, ct));
                })
            .Produces<IReadOnlyList<MosaicPanelDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetMosaicPanels");

        mosaic.MapGet("/{id:guid}/progress",
                async (Guid id, IMosaicService svc, CancellationToken ct) => {
                    var prog = await svc.GetProgressAsync(id, ct);
                    return prog is null ? Results.NotFound() : Results.Ok(prog);
                })
            .Produces<MosaicProgressDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetMosaicProgress");

        return app;
    }
}
