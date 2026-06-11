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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§28 centering — see <see cref="ICenteringService"/>. Builds the capture sequence + center-solve
/// parameters from the active profile and drives the ported centering loop over the live equipment.</summary>
public sealed class CenteringService : ICenteringService, IDisposable {
    // Serialize centering ops: each one slews/syncs the mount, so two concurrent calls (e.g. the §58.4 flip
    // recenter racing a REST caller) must not run at once. A second caller waits for the first to finish.
    private readonly SemaphoreSlim _centeringGate = new(1, 1);
    private readonly IProfileService profileService;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IImagingMediator imagingMediator;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IFilterWheelMediator filterWheelMediator;
    private readonly IDomeMediator domeMediator;
    private readonly IDomeFollower domeFollower;

    public CenteringService(
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower) {
        this.profileService = profileService;
        this.plateSolverFactory = plateSolverFactory;
        this.imagingMediator = imagingMediator;
        this.telescopeMediator = telescopeMediator;
        this.filterWheelMediator = filterWheelMediator;
        this.domeMediator = domeMediator;
        this.domeFollower = domeFollower;
    }

    public async Task<PlateSolveResult> CenterOnTarget(Coordinates target, IProgress<PlateSolveProgress>? solveProgress,
            IProgress<ApplicationStatus>? progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(target);

        await _centeringGate.WaitAsync(token);
        try {
            return await CenterCoreAsync(target, solveProgress, progress, token);
        } finally {
            _centeringGate.Release();
        }
    }

    private Task<PlateSolveResult> CenterCoreAsync(Coordinates target, IProgress<PlateSolveProgress>? solveProgress,
            IProgress<ApplicationStatus>? progress, CancellationToken token) {
        var profile = profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot centre: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;

        double focalLength = profile.TelescopeSettings.FocalLength;
        double pixelSize = profile.CameraSettings.PixelSize;
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot centre: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) in the profile.");
        }

        var plateSolver = plateSolverFactory.GetPlateSolver(settings);
        var blindSolver = plateSolverFactory.GetBlindSolver(settings);
        var centeringSolver = plateSolverFactory.GetCenteringSolver(
            plateSolver, blindSolver, imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);

        // The solve exposure (SNAPSHOT so it isn't catalogued); the loop re-uses it each attempt.
        var sequence = new CaptureSequence(
            settings.ExposureTime, ImageTypes.SNAPSHOT, filterType: null,
            binning: new BinningMode(settings.Binning, settings.Binning), exposureCount: 1) {
            Gain = settings.Gain,
        };

        var parameter = new CenterSolveParameter {
            Coordinates = target,
            Threshold = settings.Threshold, // centering tolerance (arcmin); loop converges below this
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Binning = settings.Binning,
            Attempts = settings.NumberOfAttempts,
            // NINA's PlateSolveSettings.ReattemptDelay is in MINUTES (options "Reattempt delay [min]", default 2).
            ReattemptDelay = TimeSpan.FromMinutes(settings.ReattemptDelay),
        };

        return centeringSolver.Center(sequence, parameter, solveProgress, progress, ct: token);
    }

    public void Dispose() {
        _centeringGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
