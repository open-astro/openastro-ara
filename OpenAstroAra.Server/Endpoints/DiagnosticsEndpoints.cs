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
/// Phase 8 diagnostics endpoints per PORT_PLAYBOOK.md §10.8 + §51.
/// Phase 13.5 wires every route to <see cref="IDiagnosticsService"/>
/// (placeholder today; real monitor worker + WS event emission lands
/// in the real-infra phase that follows §60.9).
/// </summary>
public static class DiagnosticsEndpoints {

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app) {
        var diagnostics = app.MapGroup("/api/v1/diagnostics").WithTags("Diagnostics");

        diagnostics.MapGet("/state",
                async (IDiagnosticsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetStateAsync(ct)))
            .Produces<DiagnosticsStateDto>(StatusCodes.Status200OK)
            .WithName("GetDiagnosticsState");

        diagnostics.MapPost("/mode",
                async ([FromBody] DiagnosticsModeRequestDto request, IDiagnosticsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.SetModeAsync(request, ct)))
            .Accepts<DiagnosticsModeRequestDto>("application/json")
            .Produces<DiagnosticsStateDto>(StatusCodes.Status200OK)
            .WithName("SetDiagnosticsMode");

        diagnostics.MapGet("/history",
                async (int? limit, string? cursor, IDiagnosticsService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetHistoryAsync(limit ?? 50, cursor, ct)))
            .Produces<CursorPage<DiagnosticEventDto>>(StatusCodes.Status200OK)
            .WithName("GetDiagnosticsHistory");

        return app;
    }
}