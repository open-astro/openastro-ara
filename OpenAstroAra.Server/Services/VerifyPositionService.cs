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
/// somewhere sane before any further movement. One attempt, no mount sync, no slew: this only LOOKS at
/// the mount. #1650 — a good solve does sync a connected, still rotator's sky angle (as Centre and
/// Rotate does), which moves nothing. Serialised like centering (both drive the camera); a second
/// caller waits.
/// </summary>
public sealed class VerifyPositionService : IVerifyPositionService, IDisposable {
    public const double DefaultExposureSeconds = 2.0;
    public const double MinExposureSeconds = 0.1;
    public const double MaxExposureSeconds = 30.0;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MainCameraPointingSolver _solver;
    private readonly Func<IRotatorMediator?> _rotator;

    public VerifyPositionService(
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IProfileStore? profileStore = null,
            ICameraService? camera = null)
        : this(new MainCameraPointingSolver(profileService, plateSolverFactory, imagingMediator, telescopeMediator,
            filterWheelMediator, profileStore, camera)) {
    }

    public VerifyPositionService(MainCameraPointingSolver solver, Func<IRotatorMediator?>? rotator = null) {
        _solver = solver;
        _rotator = rotator ?? (() => null);
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
        var mount = _solver.MountPointing();
        var intended = _solver.MountTarget();
        // The main-camera capture-and-solve is shared with the polar-align coarse stage.
        var result = await _solver.CaptureAndSolveRawAsync(exposure, mount, blindFailover: true, "Cannot verify the position", ct).ConfigureAwait(false);
        return Describe(result, mount, intended, exposure, SyncRotator(result, _rotator()));
    }

    /// <summary>#1650 — syncs a connected, still rotator to a good solve's position angle; the angle
    /// synced to, or null when nothing was (a refused sync is logged). Internal static for tests.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort side step: a rotator sync that throws (driver or HTTP error) must not lose the good solve the caller asked for; logged and reported as no sync. CA1031's log-and-recover boundary applies.")]
    internal static double? SyncRotator(PlateSolveResult result, IRotatorMediator? rotator) {
        if (!result.Success || result.Coordinates is null || !double.IsFinite(result.PositionAngle)
                || rotator?.GetInfo() is not { Connected: true, IsMoving: false }) {
            return null;
        }
        try {
            rotator.Sync((float)result.PositionAngle);
            return result.PositionAngle;
        } catch (Exception ex) {
            // The check's answer matters more than the rotator's sync: keep it.
            Logger.Warning($"Verify Position: the rotator refused the sync to {result.PositionAngle:0.##}°: {ex.Message}");
            return null;
        }
    }

    /// <summary>The pure half: the solve beside the mount's own figures. Internal static for tests.</summary>
    internal static VerifyPositionResultDto Describe(PlateSolveResult result, Coordinates? mount, Coordinates? intended, double exposure,
            double? rotatorSyncedDegrees = null) {
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
            MountEpoch: (mount ?? intended)?.Epoch.ToString(),
            PositionAngleDegrees: ok && double.IsFinite(result.PositionAngle) ? result.PositionAngle : null,
            RotatorSyncedDegrees: rotatorSyncedDegrees);
    }

    private static double? Arcmin(Coordinates? a, Coordinates? b) =>
        a is null || b is null ? null : Math.Round((a - b).Distance.ArcMinutes, 2);

    public void Dispose() => _gate.Dispose();
}
