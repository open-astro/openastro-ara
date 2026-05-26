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

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 7 mosaic endpoints per PORT_PLAYBOOK.md §10.7 + §47.
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

        mosaic.MapGet("", () => NotImplementedStub("GET /api/v1/mosaics", "§47"));
        mosaic.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/mosaics/{id}", "§47"));
        mosaic.MapPost("", () => NotImplementedStub("POST /api/v1/mosaics", "§47"));
        mosaic.MapDelete("/{id:guid}", (Guid id) => NotImplementedStub("DELETE /api/v1/mosaics/{id}", "§47"));

        mosaic.MapGet("/{id:guid}/panels", (Guid id) =>
            NotImplementedStub("GET /api/v1/mosaics/{id}/panels", "§47"));
        mosaic.MapGet("/{id:guid}/progress", (Guid id) =>
            NotImplementedStub("GET /api/v1/mosaics/{id}/progress", "§47"));

        return app;
    }
}
