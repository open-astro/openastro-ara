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
/// Phase 8 diagnostics endpoints per PORT_PLAYBOOK.md §10.8 + §51.
/// </summary>
public static class DiagnosticsEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 8's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app) {
        var diagnostics = app.MapGroup("/api/v1/diagnostics").WithTags("Diagnostics");

        diagnostics.MapGet("/state", () => NotImplementedStub("GET /api/v1/diagnostics/state", "§51"));
        diagnostics.MapPost("/mode", () => NotImplementedStub("POST /api/v1/diagnostics/mode", "§51.5"));
        diagnostics.MapGet("/history", () => NotImplementedStub("GET /api/v1/diagnostics/history", "§51"));

        return app;
    }
}
