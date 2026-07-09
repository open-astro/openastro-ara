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
/// §59.15 Smart Focus endpoints. The RUN endpoint stays on the focuser
/// (<c>POST /api/v1/equipment/focuser/autofocus</c> — it drives a physical device through the job
/// queue); these two are pure profile-state reads/writes on the daemon-owned <c>focus_calibration</c>
/// section, so they live under <c>/api/v1/autofocus</c> per the playbook's endpoint table.
/// </summary>
public static class AutofocusEndpoints {

    public static IEndpointRouteBuilder MapAutofocusEndpoints(this IEndpointRouteBuilder app) {
        var autofocus = app.MapGroup("/api/v1/autofocus").WithTags("Autofocus");

        // §59.15 — the stored Smart Focus calibration (the RAW sweep samples + when/temp/filter, for
        // display/debug; the client rebuilds any table it wants to render). 404 = "not calibrated",
        // the §59.2 null state — the next successful Classic sweep creates it.
        autofocus.MapGet("/calibration", (IProfileStore profiles) => {
            var calibration = profiles.GetFocusCalibration();
            return calibration is null ? Results.NotFound() : Results.Ok(calibration);
        })
            .Produces<FocusCalibrationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetAutofocusCalibration");

        // §59.15 — force a full recalibration. Clearing the stored calibration is the whole mechanism:
        // §59.1 mode routing runs Classic when uncalibrated, and every successful Classic sweep records
        // fresh samples (§59.2), so the next AF trigger (or a manual run) IS the recalibration. Nothing
        // to wait on here — 204.
        autofocus.MapPost("/recalibrate", (IProfileStore profiles) => {
            profiles.PutFocusCalibration(null);
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("RecalibrateAutofocus");

        return app;
    }
}
