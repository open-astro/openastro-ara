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

        stats.MapGet("/overview", () => NotImplementedStub("GET /api/v1/stats/overview", "§50"))
             .Produces<StatsOverviewDto>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status501NotImplemented)
             .WithName("GetStatsOverview");

        stats.MapGet("/targets", () => NotImplementedStub("GET /api/v1/stats/targets", "§50"))
             .Produces<StatsTargetsDto>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status501NotImplemented)
             .WithName("GetStatsTargets");

        stats.MapGet("/focus-temp", (DateTimeOffset? since) =>
                NotImplementedStub("GET /api/v1/stats/focus-temp", "§50"))
            .Produces<StatsFocusTempDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetStatsFocusTemp");

        stats.MapGet("/guiding", (DateTimeOffset? since) =>
                NotImplementedStub("GET /api/v1/stats/guiding", "§50"))
            .Produces<StatsGuidingDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetStatsGuiding");

        stats.MapGet("/frame-quality", (string? filter) =>
                NotImplementedStub("GET /api/v1/stats/frame-quality", "§50"))
            .Produces<StatsFrameQualityDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetStatsFrameQuality");

        stats.MapGet("/best-frames", (int? limit) =>
                NotImplementedStub("GET /api/v1/stats/best-frames", "§50"))
            .Produces<StatsBestFramesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetStatsBestFrames");

        stats.MapGet("/calendar", (DateOnly? fromDate, DateOnly? toDate) =>
                NotImplementedStub("GET /api/v1/stats/calendar", "§50"))
            .Produces<StatsCalendarDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetStatsCalendar");

        stats.MapGet("/export/csv", (string? scope) =>
                NotImplementedStub("GET /api/v1/stats/export/csv", "§50.16"))
            .Produces<byte[]>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ExportStatsCsv");

        return app;
    }
}
