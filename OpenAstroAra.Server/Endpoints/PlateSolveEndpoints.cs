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

        return app;
    }

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

    /// <summary>Resolve the approximate sky position to seed a near-solve: an explicit body hint (both RA and
    /// Dec supplied) wins; otherwise the frame's own OBJCTRA/OBJCTDEC FITS headers; otherwise null → a blind
    /// solve. A lone RA or Dec in the body is treated as no hint (an incomplete pair can't center a search).</summary>
    public static async Task<OpenAstroAra.Astrometry.Coordinates?> ResolveApproxCoordinatesAsync(
            Guid id, PlateSolveRequestDto? request, IFrameRepository frames, CancellationToken ct) {
        if (request?.ApproxRaHours is double raHours && request.ApproxDecDegrees is double decDeg) {
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
