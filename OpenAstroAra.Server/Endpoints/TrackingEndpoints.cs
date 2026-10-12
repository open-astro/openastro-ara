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
/// #1264 — the stored unguided tracking calibration (profile section <c>tracking_calibration</c>), the
/// sibling of <see cref="AutofocusEndpoints"/>' calibration pair: a read for the client's readiness fact
/// and advisor line, and a clear so the next run's probe starts from the spec sheet again.
/// </summary>
public static class TrackingEndpoints {

    public static IEndpointRouteBuilder MapTrackingEndpoints(this IEndpointRouteBuilder app) {
        var tracking = app.MapGroup("/api/v1/tracking").WithTags("Tracking");

        // 404 = never measured (no probe has run on this profile, or it was cleared).
        tracking.MapGet("/calibration", (IProfileStore profiles) => {
            var calibration = profiles.GetTrackingCalibration();
            return calibration is null ? Results.NotFound() : Results.Ok(calibration);
        })
            .Produces<TrackingCalibrationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetTrackingCalibration");

        tracking.MapPost("/recalibrate", (IProfileStore profiles) => {
            profiles.PutTrackingCalibration(null);
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("RecalibrateTracking");

        return app;
    }
}
