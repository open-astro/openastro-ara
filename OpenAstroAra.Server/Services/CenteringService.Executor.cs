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
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28/§38 — the service also serves the sequencer's <see cref="ICenteringExecutor"/> seam
/// (one singleton for both surfaces, per the §8.1 mediator-unification pattern), so a
/// sequence's <c>CenterAndRotate</c> drives the SAME capture → plate-solve → sync → re-slew
/// loop the REST surface and the §58.4 meridian-flip recenter use — including its internal
/// serialization (concurrent centre requests queue rather than fight over the mount).
/// </summary>
public sealed partial class CenteringService : ICenteringExecutor {

    /// <inheritdoc/>
    public async Task<bool> CenterAsync(Coordinates target, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var result = await CenterOnTarget(target, solveProgress: null, progress, token);
        return result.Success;
    }

    /// <inheritdoc/>
    public async Task<bool> CenterAndRotateAsync(Coordinates target, double positionAngleDeg,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(target);
        var rotator = rotatorMediator ?? throw new PlateSolverConfigurationException(
            "Cannot rotate: no rotator mediator is wired into the centering service.");
        await _centeringGate.WaitAsync(token);
        try {
            // NINA's rotate-first order (Center&Rotate): settle the position angle before the
            // centre — a rotator move shifts the field centre on off-axis rigs, so rotating
            // after centring would immediately un-centre what the loop just converged.
            if (!await RotateToPositionAngleAsync(rotator, positionAngleDeg, token)) {
                return false;
            }
            var result = await CenterCoreAsync(target, solveProgress: null, progress, token);
            return result.Success;
        } finally {
            _centeringGate.Release();
        }
    }

    // Solve → sync the rotator to the solved sky angle → move by the folded delta, until the
    // solved position angle is within the profile's RotationTolerance (NINA parity, including
    // the once-more-than-attempts loop shape: attempt N's move gets verified by solve N+1).
    private async Task<bool> RotateToPositionAngleAsync(OpenAstroAra.Equipment.Interfaces.Mediator.IRotatorMediator rotator,
            double positionAngleDeg, CancellationToken token) {
        var profile = profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot rotate: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;
        double focalLength = profile.TelescopeSettings.FocalLength;
        double pixelSize = profile.CameraSettings.PixelSize;
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot rotate: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) in the profile.");
        }
        var plateSolver = plateSolverFactory.GetPlateSolver(settings);
        var blindSolver = plateSolverFactory.GetBlindSolver(settings);
        var captureSolver = plateSolverFactory.GetCaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);
        var sequence = new CaptureSequence(
            settings.ExposureTime, ImageTypes.SNAPSHOT, filterType: null,
            binning: new BinningMode(settings.Binning, settings.Binning), exposureCount: 1) {
            Gain = settings.Gain,
        };
        var parameter = new CaptureSolverParameter {
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Binning = settings.Binning,
            Attempts = settings.NumberOfAttempts,
            ReattemptDelay = TimeSpan.FromMinutes(settings.ReattemptDelay),
            Coordinates = telescopeMediator.GetCurrentPosition(),
        };
        var attempts = Math.Max(1, settings.NumberOfAttempts);
        for (var attempt = 0; attempt <= attempts; attempt++) {
            token.ThrowIfCancellationRequested();
            var solve = await captureSolver.Solve(sequence, parameter, solveProgress: null, progress: null, token);
            if (!solve.Success) {
                Logger.Warning("Center-and-rotate: the rotation solve failed — the position angle cannot be verified");
                return false;
            }
            // Feed the solved angle back so the rotator's sky-angle frame tracks reality;
            // MoveRelative then works in that same frame.
            rotator.Sync((float)solve.PositionAngle);
            var delta = FoldRotationDelta(positionAngleDeg, solve.PositionAngle);
            if (Math.Abs(delta) <= settings.RotationTolerance) {
                Logger.Info($"Center-and-rotate: solved position angle {solve.PositionAngle:0.##}°, target {positionAngleDeg:0.##}° — within ±{settings.RotationTolerance:0.##}° after {attempt} move(s) (§38)");
                return true;
            }
            if (attempt == attempts) {
                break; // the last solve only verifies — no un-verified trailing move
            }
            Logger.Info($"Center-and-rotate: solved position angle {solve.PositionAngle:0.##}°, target {positionAngleDeg:0.##}° — rotating by {delta:0.##}° (§38)");
            await rotator.MoveRelative((float)delta, token);
        }
        Logger.Warning($"Center-and-rotate: rotation did not converge within {attempts} attempt(s) to ±{settings.RotationTolerance:0.##}°");
        return false;
    }

    /// <summary>
    /// The signed shortest rotation from the solved to the target position angle, folded into
    /// (-90°, +90°]: a frame rotated by 180° is the SAME framing (the sensor is a rectangle),
    /// so 350° → 172° is an 2° move, not 178°. Extracted for direct unit testing.
    /// </summary>
    internal static double FoldRotationDelta(double targetPositionAngleDeg, double solvedPositionAngleDeg) {
        var delta = AstroUtil.EuclidianModulus(targetPositionAngleDeg - solvedPositionAngleDeg, 180);
        if (delta > 90) {
            delta -= 180;
        }
        return delta;
    }
}
