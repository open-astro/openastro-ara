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
/// Phase 7 calibration endpoints per PORT_PLAYBOOK.md §10.7 + §39.
/// Phase 13.14 wires every route to <see cref="ICalibrationService"/>
/// + <see cref="IDarkLibraryService"/> (placeholders today).
/// </summary>
public static class CalibrationEndpoints {

    public static IEndpointRouteBuilder MapCalibrationEndpoints(this IEndpointRouteBuilder app) {
        var calibration = app.MapGroup("/api/v1/calibration").WithTags("Calibration");

        // Session lookup (§39)
        calibration.MapGet("/sessions",
                async (int? limit, string? cursor, ICalibrationService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListSessionsAsync(limit ?? 50, cursor, ct)))
                   .Produces<CursorPage<CalibrationSessionDto>>(StatusCodes.Status200OK)
                   .WithName("ListCalibrationSessions");

        calibration.MapGet("/sessions/{id:guid}",
                async (Guid id, ICalibrationService svc, CancellationToken ct) => {
                    var dto = await svc.GetSessionAsync(id, ct);
                    return dto is null ? Results.NotFound() : Results.Ok(dto);
                })
            .Produces<CalibrationSessionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCalibrationSession");

        calibration.MapPost("/sessions/{id:guid}/matching-flats",
                async (Guid id, [FromBody] MatchingFlatsRequestDto request, ICalibrationService svc, CancellationToken ct) => {
                    // Existence-check first — §39 requires 404 on unknown
                    // sessions, but GenerateMatchingFlatsAsync is a pure
                    // factory that never validates the id. Mirror the
                    // /mosaics/{id}/panels pattern.
                    var session = await svc.GetSessionAsync(id, ct);
                    if (session is null) return Results.NotFound();
                    var generated = await svc.GenerateMatchingFlatsAsync(id, request, ct);
                    return Results.Created($"/api/v1/sequences/{generated.GeneratedSequenceId}", generated);
                })
            .Accepts<MatchingFlatsRequestDto>("application/json")
            .Produces<GeneratedFlatSequenceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GenerateMatchingFlats");

        // Dark library (§39, §63)
        calibration.MapPost("/dark-library/build",
                async ([FromBody] DarkLibraryBuildRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IDarkLibraryService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.StartBuildAsync(request, key, ct)))
            .Accepts<DarkLibraryBuildRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithName("BuildDarkLibrary");

        calibration.MapGet("/dark-library/status",
                async (IDarkLibraryService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetStatusAsync(ct)))
            .Produces<DarkLibraryStateDto>(StatusCodes.Status200OK)
            .WithName("GetDarkLibraryStatus");

        calibration.MapGet("/dark-library/list",
                async (IDarkLibraryService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListEntriesAsync(ct)))
            .Produces<IReadOnlyList<DarkLibraryEntryDto>>(StatusCodes.Status200OK)
            .WithName("ListDarkLibraryEntries");

        return app;
    }
}