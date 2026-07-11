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
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// §18.I plate-solve endpoints. For v0.0.1 this exposes solving a catalogued frame; a live solver backend
/// (ASTAP) must be installed + its path set in the profile. The §28 centering loop (capture → solve → sync →
/// re-slew) builds on the same <see cref="IPlateSolveService"/>.
/// </summary>
public static class PlateSolveEndpoints {
    public static IEndpointRouteBuilder MapPlateSolveEndpoints(this IEndpointRouteBuilder app) {
        var solve = app.MapGroup("/api/v1/platesolve").WithTags("PlateSolve");

        solve.MapPost("/frames/{id:guid}/solve", SolveFrameAsync)
            .Produces<PlateSolveResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity) // solver/profile not configured
            .WithName("SolveFrame")
            .WithSummary("Plate-solve a catalogued frame and return its astrometric solution.");

        // §18.I — trigger the §28 slew-and-center loop as a §65.5 background job (minutes of
        // capture → solve → sync → re-slew iterations — far too long for a request). Returns
        // 202 + the job; poll GET /api/v1/jobs/{id} (a non-converged, mis-configured, or
        // equipment-faulted run surfaces as a Failed job with the reason in error_message) and
        // cancel via DELETE /api/v1/jobs/{id}. Single-flight is layered: the job service runs
        // one "center" job at a time (a second POST joins the running job), and
        // CenteringService's internal gate additionally serializes against the §58.4 flip
        // recenter and the §35 safety recenter.
        solve.MapPost("/center", CenterAsync)
            .Produces<BatchJobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("CenterOnCoordinates")
            .WithSummary("Slew the mount to coordinates and center by iterative plate solves, as a background job.");

        return app;
    }

    // Extracted (not an inline lambda) so the wiring is unit-testable with mocked services.
    public static IResult CenterAsync(CenterRequestDto? request, ICenteringService centering, IBatchJobService jobs,
            OpenAstroAra.Profile.Interfaces.IProfileService profileService) {
        // Doubles reject NaN too: NaN fails every range comparison, so `is not` catches it.
        if (request is null
                || request.RaHours is not (>= 0 and < 24)
                || request.DecDegrees is not (>= -90 and <= 90)) {
            return Results.Problem("Centering needs ra_hours in [0, 24) and dec_degrees in [-90, 90].",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var target = new OpenAstroAra.Astrometry.Coordinates(request.RaHours, request.DecDegrees,
            OpenAstroAra.Astrometry.Epoch.J2000, OpenAstroAra.Astrometry.Coordinates.RAType.Hours);
        // Job total = the profile's solve-attempt budget, read at enqueue time (the autofocus
        // pattern). Clamped to ≥1 (no active profile / zero attempts still yields a sane 0→1
        // progress bar); the job service's monotone/clamped tick keeps done in range if the
        // live loop runs a different attempt count.
        var attempts = Math.Max(1, profileService.ActiveProfile?.PlateSolveSettings?.NumberOfAttempts ?? 1);
        var job = jobs.Enqueue("center", totalSteps: attempts, CenterWork(centering, target, attempts));
        return Results.Accepted($"/api/v1/jobs/{job.JobId}", job);
    }

    /// <summary>The job body <see cref="CenterAsync"/> enqueues: one tick per completed solve
    /// attempt, then converge-or-fail. Public so tests run the real body against a mocked
    /// centering service instead of a copy kept in lockstep.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The job runner only records failures from a narrow exception allow-list; anything else (solver configuration, equipment/mediator faults) would fault the worker task and strand the job in 'running', so foreign exceptions are re-thrown as InvalidOperationException with the original preserved as the inner exception.")]
    public static Func<Action<int>, CancellationToken, Task> CenterWork(ICenteringService centering,
            OpenAstroAra.Astrometry.Coordinates target, int attempts) =>
        async (tick, ct) => {
            var solved = 0;
            var solveProgress = new Progress<OpenAstroAra.PlateSolving.PlateSolveProgress>(p => {
                if (p.PlateSolveResult is not null) {
                    tick(Math.Min(Interlocked.Increment(ref solved), attempts));
                }
            });
            OpenAstroAra.PlateSolving.PlateSolveResult result;
            try {
                result = await centering.CenterOnTarget(target, solveProgress, progress: null, ct);
            } catch (OperationCanceledException) {
                throw; // a cancelled job must record "cancelled", not "failed"
            } catch (System.IO.FileNotFoundException ex) {
                // Same sanitization as SolveFrameAsync: the framework message embeds the full
                // server-side path — replace it rather than leak it into error_message.
                throw new InvalidOperationException(
                    "Plate-solver executable not found — check the solver path (e.g. ASTAPLocation) in the profile.", ex);
            } catch (Exception ex) when (ex is not InvalidOperationException) {
                throw new InvalidOperationException(ex.Message, ex);
            }
            if (result?.Success != true) {
                throw new InvalidOperationException(
                    "Centering did not converge within the profile's attempt budget — the mount may still be off target; see the daemon log.");
            }
            tick(attempts); // settle at total; the job service's tick guard makes this final
        };

    // Extracted (not an inline lambda) so the wiring is unit-testable with mocked services. The body is
    // optional (a bare POST blind-solves); a nullable complex parameter makes Minimal API tolerate an empty body.
    public static async Task<IResult> SolveFrameAsync(Guid id, PlateSolveRequestDto? request, IFrameRepository frames,
            IPlateSolveService solver, OpenAstroAra.Profile.Interfaces.IProfileService profileService, CancellationToken ct) {
        var image = await frames.LoadImageDataAsync(id, profileService, ct);
        if (image is null) {
            return Results.NotFound();
        }
        var approx = await ResolveApproxCoordinatesAsync(id, request, frames, ct);
        try {
            var result = await solver.SolveImage(image, approx, progress: null, ct);
            return Results.Ok(ToDto(result));
        } catch (System.IO.FileNotFoundException) {
            // Framework message embeds the full server-side path; return a fixed, helpful string instead of
            // leaking it (the solver's own validation messages below are intentionally user-facing and kept).
            return Results.Problem("Plate-solver executable not found — check the solver path (e.g. ASTAPLocation) in the profile.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException ex) {
            // Every user-fixable setup problem now throws this (no active profile; focal length / pixel size
            // unconfigured; ASTAP path missing/wrong/too-old) — the messages are written to be shown to the
            // user. Catching the narrow public base (not InvalidOperationException) keeps the 422 scoped to
            // known setup errors and avoids mis-mapping unrelated InvalidOperationExceptions. Surface as 422.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>Resolve the approximate sky position to seed a near-solve: an explicit, in-range body hint (both
    /// RA and Dec supplied) wins; otherwise the frame's own OBJCTRA/OBJCTDEC FITS headers; otherwise null → a
    /// blind solve. A lone RA or Dec, or an out-of-range pair, is treated as no hint (an incomplete or nonsensical
    /// pair can't center a search) and falls through — mirroring the header path's range guard for consistency.</summary>
    public static async Task<OpenAstroAra.Astrometry.Coordinates?> ResolveApproxCoordinatesAsync(
            Guid id, PlateSolveRequestDto? request, IFrameRepository frames, CancellationToken ct) {
        if (request?.ApproxRaHours is double raHours && request.ApproxDecDegrees is double decDeg
                && raHours is >= 0 and < 24 && decDeg is >= -90 and <= 90) {
            return new OpenAstroAra.Astrometry.Coordinates(
                raHours, decDeg, OpenAstroAra.Astrometry.Epoch.J2000, OpenAstroAra.Astrometry.Coordinates.RAType.Hours);
        }
        if (await frames.TryReadTargetCoordinatesAsync(id, ct) is (double raDegrees, double headerDec)) {
            return new OpenAstroAra.Astrometry.Coordinates(
                raDegrees, headerDec, OpenAstroAra.Astrometry.Epoch.J2000, OpenAstroAra.Astrometry.Coordinates.RAType.Degrees);
        }
        return null;
    }

    /// <summary>Map a solver result to the wire DTO. On an unsuccessful solve every field except Success is
    /// null — RA/Dec have no coordinate, and orientation/scale/radius are unsolved (would otherwise read 0). A
    /// "success" with no coordinates is a solver contract violation and is reported as a failure, not as a
    /// contradictory {success:true, ra:null}.</summary>
    public static PlateSolveResultDto ToDto(OpenAstroAra.PlateSolving.PlateSolveResult result) =>
        result.Success && result.Coordinates != null
            ? new PlateSolveResultDto(true, result.Coordinates.RA, result.Coordinates.Dec,
                result.PositionAngle, result.Pixscale, result.Radius)
            : new PlateSolveResultDto(false, null, null, null, null, null);
}
