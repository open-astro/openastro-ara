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
/// Phase 9 stats endpoints per PORT_PLAYBOOK.md §10.9 + §50.
/// </summary>
public static class StatsEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 9's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app) {
        var stats = app.MapGroup("/api/v1/stats").WithTags("Stats");

        stats.MapGet("/overview", () => NotImplementedStub("GET /api/v1/stats/overview", "§50"));
        stats.MapGet("/targets", () => NotImplementedStub("GET /api/v1/stats/targets", "§50"));
        stats.MapGet("/focus-temp", () => NotImplementedStub("GET /api/v1/stats/focus-temp", "§50"));
        stats.MapGet("/guiding", () => NotImplementedStub("GET /api/v1/stats/guiding", "§50"));
        stats.MapGet("/frame-quality", () => NotImplementedStub("GET /api/v1/stats/frame-quality", "§50"));
        stats.MapGet("/best-frames", () => NotImplementedStub("GET /api/v1/stats/best-frames", "§50"));
        stats.MapGet("/calendar", () => NotImplementedStub("GET /api/v1/stats/calendar", "§50"));
        stats.MapGet("/export/csv", () => NotImplementedStub("GET /api/v1/stats/export/csv", "§50.16"));

        return app;
    }
}
