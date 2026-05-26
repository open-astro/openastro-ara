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

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 7 mosaic endpoints per PORT_PLAYBOOK.md §10.7 + §47.
/// Each route declares its intended request + response DTOs so the
/// generated OpenAPI surface lists the real schemas for WILMA codegen.
/// </summary>
public static class MosaicEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 7's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapMosaicEndpoints(this IEndpointRouteBuilder app) {
        var mosaic = app.MapGroup("/api/v1/mosaics").WithTags("Mosaics");

        mosaic.MapGet("", () => NotImplementedStub("GET /api/v1/mosaics", "§47"))
              .Produces<CursorPage<MosaicDto>>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("ListMosaics");

        mosaic.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/mosaics/{id}", "§47"))
              .Produces<MosaicDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("GetMosaic");

        mosaic.MapPost("", ([FromBody] MosaicCreateRequestDto request) => NotImplementedStub("POST /api/v1/mosaics", "§47"))
              .Accepts<MosaicCreateRequestDto>("application/json")
              .Produces<MosaicDto>(StatusCodes.Status201Created)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("CreateMosaic");

        mosaic.MapDelete("/{id:guid}", (Guid id) => NotImplementedStub("DELETE /api/v1/mosaics/{id}", "§47"))
              .Produces(StatusCodes.Status204NoContent)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("DeleteMosaic");

        mosaic.MapGet("/{id:guid}/panels", (Guid id) =>
                NotImplementedStub("GET /api/v1/mosaics/{id}/panels", "§47"))
            .Produces<IReadOnlyList<MosaicPanelDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetMosaicPanels");

        mosaic.MapGet("/{id:guid}/progress", (Guid id) =>
                NotImplementedStub("GET /api/v1/mosaics/{id}/progress", "§47"))
            .Produces<MosaicProgressDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetMosaicProgress");

        return app;
    }
}
