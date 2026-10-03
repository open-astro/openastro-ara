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

        // §59.12 — the current / most recent run as one snapshot (probes for the V-curve, the sampled fit
        // curve, the final measured focus, frame availability). `idle` until the first run since boot. The
        // Smart Focus pane hydrates from this on open and after a WS reconnect, then follows autofocus.* events.
        autofocus.MapGet("/state", (AutofocusRunTracker tracker) => Results.Ok(tracker.Snapshot()))
            .Produces<AutofocusRunDto>(StatusCodes.Status200OK)
            .WithName("GetAutofocusState");

        // §59.12 — the rendered picture of the run: the latest kept probe while the sweep runs, the
        // confirmation frame at best focus once it completes (auto-stretched, star rings, ≤1024 px). 204 until
        // a frame exists. X-Frame-Seq lets a poller change-detect without a separate state read.
        autofocus.MapGet("/frame", (AutofocusRunTracker tracker, HttpContext http) => {
            var frame = tracker.GetFrame();
            if (frame is null) {
                return Results.NoContent();
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Frame-Seq"] = frame.Value.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Bytes(frame.Value.Jpeg, "image/jpeg");
        })
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status204NoContent)
            .WithName("GetAutofocusFrame");

        // §59.12 — cancel the run in progress, whoever started it (the focuser endpoint's job or a sequence
        // instruction). The sweep restores the start position per the profile's restore_position_on_failure
        // and reports autofocus.failed { reason: "cancelled" }. 409 when nothing is running.
        autofocus.MapPost("/cancel", (AutofocusRunTracker tracker) =>
            tracker.TryCancel()
                ? Results.Accepted()
                : Results.Problem(title: "not_running", detail: "No autofocus run is in progress.", statusCode: StatusCodes.Status409Conflict))
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("CancelAutofocus");

        return app;
    }
}
