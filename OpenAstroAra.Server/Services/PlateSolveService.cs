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
/// §18.I — headless image plate-solving. The solver backends (ASTAP, Platesolve2/3) and the solve algorithms
/// (<see cref="IImageSolver"/>) were ported in the §0.5 conversion but never wired into a callable service.
/// This is that wiring: given a captured frame it builds the configured solver via
/// <see cref="IPlateSolverFactory"/>, assembles a <see cref="PlateSolveParameter"/> from the active profile
/// (focal length, pixel size, search/downsample tuning), and returns the astrometric solution.
///
/// This image-in → solution-out core is the foundation; the plate-solve centering loop (capture → solve → sync →
/// re-slew) and the §58.4 meridian-flip recenter build on it. A solver backend (e.g. ASTAP) must be
/// installed + its path set in the profile for a live solve to succeed.
/// </summary>
public interface IPlateSolveService {

    /// <summary>
    /// Solve <paramref name="image"/> astrometrically. <paramref name="approxCoordinates"/>, when supplied,
    /// seeds a near (non-blind) solve around the telescope's reported position; null requests a blind solve.
    /// </summary>
    Task<PlateSolveResult> SolveImage(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token);
}

public class PlateSolveService : IPlateSolveService {
    private readonly IProfileService profileService;
    private readonly IPlateSolverFactory plateSolverFactory;

    public PlateSolveService(IProfileService profileService, IPlateSolverFactory plateSolverFactory) {
        this.profileService = profileService;
        this.plateSolverFactory = plateSolverFactory;
    }

    public Task<PlateSolveResult> SolveImage(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(image);

        var profile = profileService.ActiveProfile;
        var settings = profile.PlateSolveSettings;

        var plateSolver = plateSolverFactory.GetPlateSolver(settings);
        var blindSolver = plateSolverFactory.GetBlindSolver(settings);
        var imageSolver = plateSolverFactory.GetImageSolver(plateSolver, blindSolver);

        var parameter = new PlateSolveParameter {
            FocalLength = profile.TelescopeSettings.FocalLength,
            PixelSize = profile.CameraSettings.PixelSize,
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
