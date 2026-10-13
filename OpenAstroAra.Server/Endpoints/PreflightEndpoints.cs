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

/// <summary>§58.14 — the pre-sleep checklist. Read-only: a GET never counts as user activity for the
/// §58.12 unattended-shutdown countdown (the equipment group's filter only watches writes).</summary>
public static class PreflightEndpoints {

    public static IEndpointRouteBuilder MapPreflightEndpoints(this IEndpointRouteBuilder app) {
        var preflight = app.MapGroup("/api/v1/preflight").WithTags("Preflight");

        preflight.MapGet("/today", async (PreflightChecklistService svc, CancellationToken ct) =>
                Results.Ok(await svc.BuildAsync(ct)))
            .Produces<PreflightChecklistDto>(StatusCodes.Status200OK)
            .WithName("GetPreflightToday")
            .WithSummary("§58.14 pre-sleep checklist: equipment, storage, solver, cooler, mount, the running sequence's meridian crossings, the unattended window and the alarm device.");

        return app;
    }
}
