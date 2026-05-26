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
/// Phase 7 calibration endpoints per PORT_PLAYBOOK.md §10.7 + §39.
/// Each route declares its intended request + response DTOs so the
/// generated OpenAPI surface lists the real schemas for WILMA codegen.
/// </summary>
public static class CalibrationEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 7's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapCalibrationEndpoints(this IEndpointRouteBuilder app) {
        var calibration = app.MapGroup("/api/v1/calibration").WithTags("Calibration");

        // Session lookup (§39)
        calibration.MapGet("/sessions", () => NotImplementedStub("GET /api/v1/calibration/sessions", "§39"))
                   .Produces<CursorPage<CalibrationSessionDto>>(StatusCodes.Status200OK)
                   .ProducesProblem(StatusCodes.Status501NotImplemented)
                   .WithName("ListCalibrationSessions");

        calibration.MapGet("/sessions/{id:guid}", (Guid id) =>
                NotImplementedStub("GET /api/v1/calibration/sessions/{id}", "§39"))
            .Produces<CalibrationSessionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetCalibrationSession");

        calibration.MapPost("/sessions/{id:guid}/matching-flats",
                (Guid id, [FromBody] MatchingFlatsRequestDto request) =>
                    NotImplementedStub("POST /api/v1/calibration/sessions/{id}/matching-flats", "§39"))
            .Accepts<MatchingFlatsRequestDto>("application/json")
            .Produces<GeneratedFlatSequenceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GenerateMatchingFlats");

        // Dark library (§39, §63)
        calibration.MapPost("/dark-library/build", ([FromBody] DarkLibraryBuildRequestDto request) =>
                NotImplementedStub("POST /api/v1/calibration/dark-library/build", "§39"))
            .Accepts<DarkLibraryBuildRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("BuildDarkLibrary");

        calibration.MapGet("/dark-library/status", () =>
                NotImplementedStub("GET /api/v1/calibration/dark-library/status", "§39"))
            .Produces<DarkLibraryStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetDarkLibraryStatus");

        calibration.MapGet("/dark-library/list", () =>
                NotImplementedStub("GET /api/v1/calibration/dark-library/list", "§39"))
            .Produces<IReadOnlyList<DarkLibraryEntryDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ListDarkLibraryEntries");

        return app;
    }
}
