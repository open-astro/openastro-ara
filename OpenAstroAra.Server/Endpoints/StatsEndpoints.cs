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
/// Phase 9 stats endpoints per PORT_PLAYBOOK.md §10.9 + §50.
/// Phase 13.6 wires every chart view to <see cref="IStatsService"/>
/// (placeholder today; real DB-backed aggregations land alongside the
/// §28 frame catalog DB).
/// </summary>
public static class StatsEndpoints {

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app) {
        var stats = app.MapGroup("/api/v1/stats").WithTags("Stats");

        stats.MapGet("/overview",
                async (IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetOverviewAsync(ct)))
            .Produces<StatsOverviewDto>(StatusCodes.Status200OK)
            .WithName("GetStatsOverview");

        stats.MapGet("/targets",
                async (IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetTargetsAsync(ct)))
            .Produces<StatsTargetsDto>(StatusCodes.Status200OK)
            .WithName("GetStatsTargets");

        stats.MapGet("/focus-temp",
                async (DateTimeOffset? since, IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetFocusTempAsync(since, ct)))
            .Produces<StatsFocusTempDto>(StatusCodes.Status200OK)
            .WithName("GetStatsFocusTemp");

        stats.MapGet("/guiding",
                async (DateTimeOffset? since, IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetGuidingAsync(since, ct)))
            .Produces<StatsGuidingDto>(StatusCodes.Status200OK)
            .WithName("GetStatsGuiding");

        stats.MapGet("/frame-quality",
                async (string? filter, IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetFrameQualityAsync(filter, ct)))
            .Produces<StatsFrameQualityDto>(StatusCodes.Status200OK)
            .WithName("GetStatsFrameQuality");

        stats.MapGet("/best-frames",
                async (int? limit, IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetBestFramesAsync(limit ?? 10, ct)))
            .Produces<StatsBestFramesDto>(StatusCodes.Status200OK)
            .WithName("GetStatsBestFrames");

        stats.MapGet("/calendar",
                async (DateOnly? fromDate, DateOnly? toDate, IStatsService svc, CancellationToken ct) => {
                    var from = fromDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
                    var to = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                    return Results.Ok(await svc.GetCalendarAsync(from, to, ct));
                })
            .Produces<StatsCalendarDto>(StatusCodes.Status200OK)
            .WithName("GetStatsCalendar");

        stats.MapGet("/achievements",
                async (IStatsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetAchievementsAsync(ct)))
            .Produces<StatsAchievementsDto>(StatusCodes.Status200OK)
            .WithName("GetStatsAchievements");

        // CSV export remains 404 until the §28 frame catalog backs it —
        // null from the service signals "feature unavailable" rather than
        // 501-stubbing the whole route. Real impl streams the result.
        stats.MapGet("/export/csv",
                async (string? scope, IStatsService svc, CancellationToken ct) => {
                    var result = await svc.OpenCsvExportAsync(scope ?? "frames", ct);
                    if (result is null) return Results.NotFound();
                    return Results.Stream(result.Value.Stream, "text/csv", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ExportStatsCsv");

        // AstroBin acquisition CSV for one target: 400 when no target is given,
        // 404 when the target has no light frames to export.
        stats.MapGet("/export/astrobin",
                async (string? target, IStatsService svc, CancellationToken ct) => {
                    if (string.IsNullOrWhiteSpace(target)) {
                        return Results.Problem(
                            detail: "A 'target' query parameter is required.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                    var result = await svc.OpenAstrobinExportAsync(target, ct);
                    if (result is null) return Results.NotFound();
                    return Results.Stream(result.Value.Stream, "text/csv", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ExportStatsAstrobin");

        return app;
    }
}