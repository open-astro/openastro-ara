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

    // Extracted (not an inline lambda) so the wiring is unit-testable with mocked services.
    public static async Task<IResult> SolveFrameAsync(Guid id, IFrameRepository frames, IPlateSolveService solver,
            OpenAstroAra.Profile.Interfaces.IProfileService profileService, CancellationToken ct) {
        var image = await frames.LoadImageDataAsync(id, profileService, ct);
        if (image is null) {
            return Results.NotFound();
        }
        try {
            // Blind solve for now (no hint); a body-supplied approximate position can seed a faster near-solve.
            var result = await solver.SolveImage(image, approxCoordinates: null, progress: null, ct);
            return Results.Ok(ToDto(result));
        } catch (System.IO.FileNotFoundException) {
            // Framework message embeds the full server-side path; return a fixed, helpful string instead of
            // leaking it (the solver's own validation messages below are intentionally user-facing and kept).
            return Results.Problem("Plate-solver executable not found — check the solver path (e.g. ASTAPLocation) in the profile.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        } catch (Exception ex) when (ex is InvalidOperationException
                or OpenAstroAra.PlateSolving.PlateSolverConfigurationException) {
            // User-fixable setup problems with messages written to be shown to the user (no active profile;
            // focal length / pixel size unconfigured; ASTAP path missing/wrong/too-old). The public
            // PlateSolverConfigurationException base lets us catch the latter without the internal solver
            // types being reachable. Surface as 422, not an opaque 500.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
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
