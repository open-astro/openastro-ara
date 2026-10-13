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

/// <summary>#1140 P2 — the Integration Budget's sky-model calibration: read the stored record, refit it
/// from the library's recent frames, or clear it.</summary>
public static class PlanningEndpoints {

    public static IEndpointRouteBuilder MapPlanningEndpoints(this IEndpointRouteBuilder app) {
        var planning = app.MapGroup("/api/v1/planning").WithTags("Planning");

        // 404 = never fitted (or cleared).
        planning.MapGet("/sky-model", (IProfileStore profiles) => {
            var record = profiles.GetSkyModelCalibration();
            return record is null ? Results.NotFound() : Results.Ok(record);
        })
            .Produces<SkyModelCalibrationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetSkyModelCalibration");

        // Refit from the library now (synchronous: a few thousand catalog rows, no pixels) and store it.
        planning.MapPost("/sky-model/recalibrate", async (SkyModelCalibrationService svc, CancellationToken ct) =>
                Results.Ok(await svc.RecalibrateAsync(ct)))
            .Produces<SkyModelCalibrationDto>(StatusCodes.Status200OK)
            .WithName("RecalibrateSkyModel")
            .WithSummary("Fit the per-filter-kind sky-model calibration k from the library's recent frames and store it on the profile.");

        planning.MapDelete("/sky-model", (IProfileStore profiles) => {
            profiles.PutSkyModelCalibration(null);
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("ClearSkyModelCalibration");

        return app;
    }
}
