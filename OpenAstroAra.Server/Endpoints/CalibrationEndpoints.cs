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
/// Phase 7 calibration endpoints per PORT_PLAYBOOK.md §10.7 + §39.
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
        calibration.MapGet("/sessions", () => NotImplementedStub("GET /api/v1/calibration/sessions", "§39"));
        calibration.MapGet("/sessions/{id:guid}", (Guid id) =>
            NotImplementedStub("GET /api/v1/calibration/sessions/{id}", "§39"));
        calibration.MapPost("/sessions/{id:guid}/matching-flats", (Guid id) =>
            NotImplementedStub("POST /api/v1/calibration/sessions/{id}/matching-flats", "§39"));

        // Dark library (§39, §63)
        calibration.MapPost("/dark-library/build", () =>
            NotImplementedStub("POST /api/v1/calibration/dark-library/build", "§39"));
        calibration.MapGet("/dark-library/status", () =>
            NotImplementedStub("GET /api/v1/calibration/dark-library/status", "§39"));
        calibration.MapGet("/dark-library/list", () =>
            NotImplementedStub("GET /api/v1/calibration/dark-library/list", "§39"));

        return app;
    }
}
