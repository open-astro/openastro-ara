#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>One solved reading for the by-hand rotation readout: the sky position angle of the frame's
/// up axis (degrees east of north), where the frame centre points (J2000 degrees), the solved pixel scale,
/// whether the optical train mirrors the sky (a flip reverses the sense of a turn), and the frame itself
/// so the readout can show the user the stars it measured.</summary>
public sealed record RotationSolve(
    double PositionAngleDeg,
    double RaDeg,
    double DecDeg,
    double PixelScaleArcsec,
    bool Flipped,
    AnalysisFrame Frame);

/// <summary>One capture-and-solve of the main camera (null = the solve failed). <see cref="RotationFrameSolver"/>
/// implements it over the profile's plate-solver stack; tests and the synthetic sky inject their own.</summary>
public interface IPositionAngleSolver {
    Task<RotationSolve?> SolvePositionAngleAsync(CancellationToken ct);

    /// <summary>Throw <see cref="PlateSolverConfigurationException"/> when a solve cannot possibly succeed on
    /// this rig as configured (checked once at start, so a run skips the step instead of failing five
    /// solves). Default: nothing to check.</summary>
    void EnsureReady() { }
}

/// <summary>
/// The readout's real solver: capture one frame through the §59 analysis seam (same device path and
/// in-flight gate as every other capture, nothing persisted), wrap it as image data and run the
/// profile-configured solver with the MAIN optics — the way the §45 polar-align solver does for guide
/// frames. Captured here rather than inside the solver's own capture helper so the frame is KEPT: the
/// client draws the framing over it. A failed solve (clouds, no stars) is a normal null; configuration
/// problems (no optics, no solver) throw <see cref="PlateSolverConfigurationException"/>.
/// </summary>
public sealed class RotationFrameSolver : IPositionAngleSolver {
    private readonly IProfileService _profileService;
    private readonly IProfileStore _store;
    private readonly IPlateSolverFactory _solverFactory;
    private readonly IAnalysisFrameSource _frames;
    private readonly ITelescopeMediator _telescope;

    public RotationFrameSolver(IProfileService profileService, IProfileStore store, IPlateSolverFactory solverFactory,
            IAnalysisFrameSource frames, ITelescopeMediator telescope) {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _solverFactory = solverFactory ?? throw new ArgumentNullException(nameof(solverFactory));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _telescope = telescope ?? throw new ArgumentNullException(nameof(telescope));
    }

    public void EnsureReady() {
        LegacyProfileBridge.SyncPlateSolve(_profileService, _store);
        var profile = _profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("no active profile is loaded");
        if (!(profile.TelescopeSettings.FocalLength > 0) || !(profile.CameraSettings.PixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                "the telescope focal length and camera pixel size must both be set in the profile (Options → Imaging → Optics)");
        }
        if (SolverPathMigration.MissingSolverBinary(_store.GetPlateSolveSettings(), System.IO.File.Exists) is string missing) {
            throw new PlateSolverConfigurationException(
                $"the plate solver binary is missing at {missing} (install astap-cli or fix Options → Plate solving)");
        }
    }

    public async Task<RotationSolve?> SolvePositionAngleAsync(CancellationToken ct) {
        // ARA store → legacy settings first (same rule and reason as PlateSolveService).
        LegacyProfileBridge.SyncPlateSolve(_profileService, _store);
        var profile = _profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot read the rotation: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;
        double focalLength = profile.TelescopeSettings.FocalLength;
        double pixelSize = profile.CameraSettings.PixelSize;
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot read the rotation: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) in the profile.");
        }

        var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureTime, settings.Binning, ct).ConfigureAwait(false);
        // The solver ignores the CFA; a one-shot-colour mosaic solves as luminance like every other path here.
        var image = new OpenAstroAra.Image.ImageData.BaseImageData(
            frame.Pixels.ToArray(), frame.Width, frame.Height, bitDepth: 16, isBayered: false,
            new OpenAstroAra.Image.ImageData.ImageMetaData(), _profileService, null!, null!);

        var plateSolver = _solverFactory.GetPlateSolver(settings);
        var blindSolver = _solverFactory.GetBlindSolver(settings);
        var imageSolver = _solverFactory.GetImageSolver(plateSolver, blindSolver);
        var parameter = new PlateSolveParameter {
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Binning = settings.Binning,
            Coordinates = _telescope.GetCurrentPosition(),
            BlindFailoverEnabled = settings.BlindFailoverEnabled,
        };
        var result = await imageSolver.Solve(image, parameter, progress: null, ct).ConfigureAwait(false);
        if (result?.Success != true || result.Coordinates is null) {
            return null;
        }
        return new RotationSolve(
            result.PositionAngle, result.Coordinates.RADegrees, result.Coordinates.Dec,
            result.Pixscale, result.Flipped, frame);
    }
}
