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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The polar-align coarse stage's frame source: one main-camera snapshot, solved with the
/// main optics, returned as an apparent-of-date pointing. Failure to solve is <c>Success=false</c>;
/// a busy camera throws <see cref="CameraBusyException"/>, unset optics
/// <see cref="PlateSolverConfigurationException"/>.</summary>
public interface IPolarAlignMainCameraSolver {
    Task<PolarAlignSolveOutcome> CaptureAndSolveAsync(
        double exposureSeconds, (double RaDeg, double DecDeg)? hintJnow, CancellationToken ct);
}

/// <summary>
/// One main-camera snapshot (SNAPSHOT type — never catalogued), plate-solved with the profile's main
/// optics and the mount's reported pointing as the hint. The shared capture-and-solve behind §57.4
/// Verify Position and the polar-align coarse stage: both only LOOK — one attempt, no sync, no slew.
/// </summary>
public sealed class MainCameraPointingSolver : IPolarAlignMainCameraSolver {
    private readonly IProfileService _profileService;
    private readonly IProfileStore? _profileStore;
    private readonly IPlateSolverFactory _plateSolverFactory;
    private readonly IImagingMediator _imagingMediator;
    private readonly ITelescopeMediator _telescopeMediator;
    private readonly IFilterWheelMediator _filterWheelMediator;
    private readonly ICameraService? _camera;

    public MainCameraPointingSolver(
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IProfileStore? profileStore = null,
            ICameraService? camera = null) {
        _profileService = profileService;
        _plateSolverFactory = plateSolverFactory;
        _imagingMediator = imagingMediator;
        _telescopeMediator = telescopeMediator;
        _filterWheelMediator = filterWheelMediator;
        _profileStore = profileStore;
        _camera = camera;
    }

    /// <summary>The mount's reported pointing, or null when it is not connected.</summary>
    public Coordinates? MountPointing() {
        var info = _telescopeMediator.GetInfo();
        return info?.Connected == true ? info.Coordinates : null;
    }

    /// <summary>Where the last slew was aimed, or null when the mount is not connected.</summary>
    public Coordinates? MountTarget() {
        var info = _telescopeMediator.GetInfo();
        return info?.Connected == true ? info.TargetCoordinates : null;
    }

    /// <summary>Capture one snapshot and solve it. <paramref name="purpose"/> prefixes the
    /// configuration messages ("Cannot verify the position", "Cannot polar-align").</summary>
    public async Task<PlateSolveResult> CaptureAndSolveRawAsync(
            double exposureSeconds, Coordinates? hint, bool blindFailover, string purpose, CancellationToken ct) {
        LegacyProfileBridge.SyncPlateSolve(_profileService, _profileStore);
        var profile = _profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException($"{purpose}: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;
        var (focalLength, pixelSize) = PlateSolveService.ResolveSolveGeometry(
            _profileStore?.GetOpticsSettings(), profile.TelescopeSettings.FocalLength, profile.CameraSettings.PixelSize);
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"{purpose}: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) — set them in Options → Imaging → Optics.");
        }

        // Fail fast while another exposure holds the camera: the capture path would otherwise wait
        // on the gate for up to 20 minutes, long past any client's timeout (review of #1346).
        if (_camera is not null && !_camera.IsFreeToCapture(this)) {
            throw new CameraBusyException();
        }

        var captureSolver = _plateSolverFactory.GetCaptureSolver(
            _plateSolverFactory.GetPlateSolver(settings), _plateSolverFactory.GetBlindSolver(settings), _imagingMediator, _filterWheelMediator);
        // SNAPSHOT: never catalogued, like the centering exposures.
        var sequence = new CaptureSequence(exposureSeconds, ImageTypes.SNAPSHOT, filterType: null,
            binning: new BinningMode(settings.Binning, settings.Binning), exposureCount: 1) { Gain = settings.Gain };
        var parameter = new CaptureSolverParameter {
            Coordinates = hint,              // the hint: where the mount THINKS it is
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Timeout = PlateSolveService.TimeoutFrom(settings),
            Binning = settings.Binning,
            BlindFailoverEnabled = blindFailover,
            Attempts = 1,                    // one look; the caller decides what to do next
            ReattemptDelay = TimeSpan.Zero,
        };
        return await captureSolver.Solve(sequence, parameter, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Coarse-stage frame: the mount at Home may be degrees off, so the near solve may miss
    /// and the blind fallback is ON (a 1-in-several-seconds loop, not the 1 s guide loop).</summary>
    public async Task<PolarAlignSolveOutcome> CaptureAndSolveAsync(
            double exposureSeconds, (double RaDeg, double DecDeg)? hintJnow, CancellationToken ct) {
        Coordinates? hint = hintJnow is { } h
            ? new Coordinates(h.RaDeg, h.DecDeg, Epoch.JNOW, Coordinates.RAType.Degrees)
            : MountPointing();
        var result = await CaptureAndSolveRawAsync(exposureSeconds, hint, blindFailover: true, "Cannot polar-align", ct).ConfigureAwait(false);
        if (result?.Success != true || result.Coordinates is null) {
            return new PolarAlignSolveOutcome(false, 0, 0);
        }
        var apparent = result.Coordinates.Transform(Epoch.JNOW);
        return new PolarAlignSolveOutcome(true, apparent.RADegrees, apparent.Dec);
    }
}
