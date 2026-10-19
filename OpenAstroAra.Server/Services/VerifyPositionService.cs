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
using OpenAstroAra.Server.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§57.4 — the camera's in-flight gate is held (a sequence sub still exposing, a live-view
/// frame): Verify-Position fails FAST with this instead of queueing behind it (the sequencer's capture
/// path waits up to 20 minutes, which no interactive client outlives). The user taps again later.</summary>
public sealed class CameraBusyException : InvalidOperationException {
    public const string DefaultMessage = "The camera is busy with an exposure — try Verify Position again once it has read out.";
    public CameraBusyException() : base(DefaultMessage) { }
    public CameraBusyException(string message) : base(message) { }
    public CameraBusyException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>§57.4 Verify-Position — see <see cref="VerifyPositionService"/>.</summary>
public interface IVerifyPositionService {
    Task<VerifyPositionResultDto> VerifyAsync(double? exposureSeconds, CancellationToken ct);
}

/// <summary>
/// §57.4 — after the user taps Stop Mount, "Verify Position — show me an image": one quick snapshot
/// (2 s by default, never catalogued), solved with the mount's reported position as the hint, reported
/// beside what the mount says and where the last slew was aimed, so the user can confirm the mount is
/// somewhere sane before any further movement. One attempt, no sync, no slew: this only LOOKS. Serialised
/// like centering (both drive the camera); a second caller waits.
/// </summary>
public sealed class VerifyPositionService : IVerifyPositionService, IDisposable {
    public const double DefaultExposureSeconds = 2.0;
    public const double MinExposureSeconds = 0.1;
    public const double MaxExposureSeconds = 30.0;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProfileService profileService;
    private readonly IProfileStore? profileStore;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IImagingMediator imagingMediator;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IFilterWheelMediator filterWheelMediator;
    private readonly ICameraService? camera;

    public VerifyPositionService(
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IProfileStore? profileStore = null,
            ICameraService? camera = null) {
        this.profileService = profileService;
        this.plateSolverFactory = plateSolverFactory;
        this.imagingMediator = imagingMediator;
        this.telescopeMediator = telescopeMediator;
        this.filterWheelMediator = filterWheelMediator;
        this.profileStore = profileStore;
        this.camera = camera;
    }

    public async Task<VerifyPositionResultDto> VerifyAsync(double? exposureSeconds, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            return await VerifyCoreAsync(ClampExposure(exposureSeconds), ct).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
    }

    internal static double ClampExposure(double? requested) =>
        requested is double s && !double.IsNaN(s) ? Math.Clamp(s, MinExposureSeconds, MaxExposureSeconds) : DefaultExposureSeconds;

    private async Task<VerifyPositionResultDto> VerifyCoreAsync(double exposure, CancellationToken ct) {
        LegacyProfileBridge.SyncPlateSolve(profileService, profileStore);
        var profile = profileService.ActiveProfile
            ?? throw new PlateSolverConfigurationException("Cannot verify the position: no active profile is loaded.");
        var settings = profile.PlateSolveSettings;
        var (focalLength, pixelSize) = PlateSolveService.ResolveSolveGeometry(
            profileStore?.GetOpticsSettings(), profile.TelescopeSettings.FocalLength, profile.CameraSettings.PixelSize);
        if (!(focalLength > 0) || !(pixelSize > 0)) {
            throw new PlateSolverConfigurationException(
                $"Cannot verify the position: telescope focal length ({focalLength}) and camera pixel size ({pixelSize}) must both be configured (> 0) — set them in Options → Imaging → Optics.");
        }

        // Fail fast while another exposure holds the camera: the capture path would otherwise wait
        // on the gate for up to 20 minutes, long past any client's timeout (review of #1346).
        if (camera is not null && !camera.IsFreeToCapture(this)) {
            throw new CameraBusyException();
        }

        var info = telescopeMediator.GetInfo();
        var mount = info?.Connected == true ? info.Coordinates : null;
        var intended = info?.Connected == true ? info.TargetCoordinates : null;

        var captureSolver = plateSolverFactory.GetCaptureSolver(
            plateSolverFactory.GetPlateSolver(settings), plateSolverFactory.GetBlindSolver(settings), imagingMediator, filterWheelMediator);
        // SNAPSHOT: never catalogued, like the centering exposures.
        var sequence = new CaptureSequence(exposure, ImageTypes.SNAPSHOT, filterType: null,
            binning: new BinningMode(settings.Binning, settings.Binning), exposureCount: 1) { Gain = settings.Gain };
        var parameter = new CaptureSolverParameter {
            Coordinates = mount,             // the hint: where the mount THINKS it is
            FocalLength = focalLength,
            PixelSize = pixelSize,
            SearchRadius = settings.SearchRadius,
            Regions = settings.Regions,
            DownSampleFactor = settings.DownSampleFactor,
            MaxObjects = settings.MaxObjects,
            Timeout = PlateSolveService.TimeoutFrom(settings),
            Binning = settings.Binning,
            Attempts = 1,                    // one look; the user decides what to do next
            ReattemptDelay = TimeSpan.Zero,
        };
        var result = await captureSolver.Solve(sequence, parameter, null, null, ct).ConfigureAwait(false);
        return Describe(result, mount, intended, exposure);
    }

    /// <summary>The pure half: the solve beside the mount's own figures. Internal static for tests.</summary>
    internal static VerifyPositionResultDto Describe(PlateSolveResult result, Coordinates? mount, Coordinates? intended, double exposure) {
        var solved = result.Success ? result.Coordinates : null;
        var ok = solved is not null;
        string? note = ok ? null
            : mount is null ? "The solve failed and the mount is not connected, so nothing can be compared."
            : "The solve failed (clouds, a closed cover, too short an exposure, or the mount is far from where it thinks it is). The mount's own figures are shown.";
        return new VerifyPositionResultDto(
            Success: ok,
            SolvedRaHours: solved?.RA,
            SolvedDecDegrees: solved?.Dec,
            MountRaHours: mount?.RA,
            MountDecDegrees: mount?.Dec,
            IntendedRaHours: intended?.RA,
            IntendedDecDegrees: intended?.Dec,
            SolvedToMountArcmin: Arcmin(solved, mount),
            SolvedToIntendedArcmin: Arcmin(solved, intended),
            ExposureSeconds: exposure,
            Note: note,
            MountEpoch: (mount ?? intended)?.Epoch.ToString());
    }

    private static double? Arcmin(Coordinates? a, Coordinates? b) =>
        a is null || b is null ? null : Math.Round((a - b).Distance.ArcMinutes, 2);

    public void Dispose() => _gate.Dispose();
}
