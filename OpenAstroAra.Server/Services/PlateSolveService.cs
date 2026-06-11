#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §18.I — headless image plate-solving (see <see cref="IPlateSolveService"/>). Builds the configured solver
/// via <see cref="IPlateSolverFactory"/>, assembles a <see cref="PlateSolveParameter"/> from the active profile
/// (focal length, pixel size, search/downsample tuning), and returns the astrometric solution.
/// </summary>
public class PlateSolveService : IPlateSolveService {
    private readonly IProfileService profileService;
    private readonly IPlateSolverFactory plateSolverFactory;

    public PlateSolveService(IProfileService profileService, IPlateSolverFactory plateSolverFactory) {
        this.profileService = profileService;
        this.plateSolverFactory = plateSolverFactory;
    }

    public Task<PlateSolveResult> SolveImage(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(image);

        var profile = profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot plate-solve: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;

        // Focal length + pixel size set the pixel scale / FOV the solver searches; a fresh profile leaves them
        // unset (focal length defaults to NaN), which yields a degenerate FOV and an opaque solver failure.
        // Fail fast with an actionable message instead. PlateSolverConfigurationException (a setup error the
        // user fixes) lets the API layer return a narrow 422 without catching unrelated InvalidOperationExceptions.
        double focalLength = profile.TelescopeSettings.FocalLength;
        double pixelSize = profile.CameraSettings.PixelSize;
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot plate-solve: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) in the profile.");
        }

        var plateSolver = plateSolverFactory.GetPlateSolver(settings);
        var blindSolver = plateSolverFactory.GetBlindSolver(settings);
        var imageSolver = plateSolverFactory.GetImageSolver(plateSolver, blindSolver);

        var parameter = new PlateSolveParameter {
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Binning = 1, // solve at native scale; the plate-solve capture path will set this once it owns the capture
            Coordinates = approxCoordinates,
        };

        return imageSolver.Solve(image, parameter, progress, token);
    }
}
