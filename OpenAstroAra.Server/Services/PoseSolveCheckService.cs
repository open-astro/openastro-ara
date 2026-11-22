#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services;

/// <summary>#2173 — see <see cref="PoseSolveCheckService"/>.</summary>
public interface IPoseSolveCheckService {
    /// <summary>Whether the check can run now (the button shows only then).</summary>
    Task<PoseSolveAvailabilityDto> AvailabilityAsync(CancellationToken ct);

    /// <summary>The check: one plate solve compared with the mount. With <paramref name="syncCheckId"/> (the
    /// <see cref="PoseSolveCheckResultDto.CheckId"/> of a mismatch the user tapped Sync on) it solves again, syncs the
    /// mount to the sky, then re-reads it and solves a second time before it trusts anything.</summary>
    Task<PoseSolveCheckResultDto> VerifyPoseBySolveAsync(string? syncCheckId, CancellationToken ct);
}

/// <summary>
/// #2173 — clear an unverified mount position with a plate solve. It sends the mount <b>no motion</b> of any kind, so it
/// runs while the position is unverified: it reads the mount fresh (RA, Dec, pier side, Slewing), takes one short exposure
/// through the existing solve pipeline (<see cref="IPoseSolveCamera"/>: the main camera, the guide camera only when there
/// is no main camera), and reads the mount again. A Dec past ±90° is folded over the pole first, and the solve is put in
/// the mount's own frame (J2000 or JNOW) before the two are compared as an angle on the sky.
/// <para>The pier side is checked two ways, and either disagreeing means no trust and no Sync offer (Home stays the way
/// out): (a) the mount's SideOfPier against the side the solved hour angle gives
/// (<see cref="MeridianFlip.ExpectedPierSide"/>, as the #2128 guards use it), and (b) the solved position angle against
/// the last trusted solve's for this mount, camera and rotator angle (<see cref="IPoseSolvePaStore"/>): about 180° off is
/// the other pier side. With no stored angle, (a) decides alone. A German mount whose side no check could tell (no
/// SideOfPier, too near the pole, no site, and no stored angle) is not trusted either.</para>
/// <para>Within max(<see cref="MinToleranceDeg"/>, 2 × the solve's uncertainty) with the pier side agreeing, the position
/// is trusted again for every reason (no eye check, no brand: ADR 0003), logged, persisted and pushed. Further off, the
/// result says where the mount reads and where the sky is, and offers one Sync (<see cref="PoseSolveCheckResultDto.CheckId"/>,
/// never automatic). The Sync is the existing mediator Sync with its guards; after it the mount is re-read and a second
/// frame solved, and only both agreeing trusts it (AlpacaBridge's Sync offset could leave a model that reads right once).
/// A failed solve, no camera, a busy camera, a refused Sync or one the mount ignores leaves it unverified; Home and
/// confirm-home stay the ways out. One check runs at a time; a second caller waits.</para>
/// </summary>
public sealed partial class PoseSolveCheckService : IPoseSolveCheckService, IDisposable {

    /// <summary>The smallest tolerance between the mount and the sky.</summary>
    public const double MinToleranceDeg = 0.5;

    /// <summary>The solve's own uncertainty, in pixels of its frame: ASTAP reports no error, and a few pixels covers the
    /// centre's error and the drift over a short exposure.</summary>
    public const double SolveUncertaintyPixels = 3.0;

    /// <summary>Inside this distance of a pole the solved hour angle says nothing about the pier side (a small cone
    /// error spans hours of RA there), so check (a) gives no answer.</summary>
    public const double PoleBandDeg = 5.0;

    /// <summary>How far the mount's two readings around the exposure may differ, beyond the sky's sidereal drift.</summary>
    public const double MovedDeg = 0.1;

    /// <summary>How close the solved position angle must be to the stored one (or to it + 180°) to tell the side.</summary>
    public const double PositionAngleMatchDeg = 30.0;

    /// <summary>How long a mismatch's Sync stays on offer.</summary>
    public static readonly TimeSpan OfferLife = TimeSpan.FromMinutes(5);

    private readonly ITelescopeService _mount;
    private readonly IPoseSolveCamera _camera;
    private readonly IProfileStore? _profiles;
    private readonly IPoseSolvePaStore _angles;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Id, long Session, string? Device, DateTimeOffset At)? _offer;

    public PoseSolveCheckService(ITelescopeService mount, IPoseSolveCamera camera, IProfileStore? profiles, IPoseSolvePaStore angles,
            ILogger<PoseSolveCheckService>? logger = null, TimeProvider? clock = null) {
        _mount = mount ?? throw new ArgumentNullException(nameof(mount));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _profiles = profiles;
        _angles = angles ?? throw new ArgumentNullException(nameof(angles));
        _logger = logger ?? NullLogger<PoseSolveCheckService>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PoseSolveAvailabilityDto> AvailabilityAsync(CancellationToken ct) {
        if (_mount.PoseDeviceId is null) {
            return new(false, null, "mount_not_connected");
        }
        if (!NeedsCheck(await _mount.PositionUnverifiedReasonAsync(ct).ConfigureAwait(false))) {
            return new(false, null, "not_needed");
        }
        var choice = await _camera.ChooseAsync(ct).ConfigureAwait(false);
        return new(choice.UnavailableReason is null, choice.Camera, choice.UnavailableReason);
    }

    private static bool NeedsCheck(string? reason) => reason is not null && reason != MountPoseTrust.NotReadYet;

    public async Task<PoseSolveCheckResultDto> VerifyPoseBySolveAsync(string? syncCheckId, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            return await VerifyCoreAsync(syncCheckId, ct).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
    }

    private async Task<PoseSolveCheckResultDto> VerifyCoreAsync(string? syncCheckId, CancellationToken ct) {
        var device = _mount.PoseDeviceId ?? throw new RefusalException(RefusalCatalog.MountNotConnected,
            "No mount is connected, so its position cannot be checked.");
        var reason = await _mount.PositionUnverifiedReasonAsync(ct).ConfigureAwait(false);
        if (!NeedsCheck(reason)) {
            _offer = null;
            throw new RefusalException(RefusalCatalog.PoseCheckNotNeeded, reason == MountPoseTrust.NotReadYet
                ? "The mount was just connected and its position is still being read; try again in a few seconds."
                : "The mount's position is trusted; there is nothing to check.");
        }
        if (syncCheckId is not null) {
            var offer = _offer;
            _offer = null; // one tap per offer
            if (offer is not { } o || o.Id != syncCheckId || o.Session != _mount.SessionEpoch || o.Device != device
                    || _clock.GetUtcNow() - o.At > OfferLife) {
                throw new RefusalException(RefusalCatalog.PoseSyncNotOffered,
                    "No plate-solve Sync is on offer (it was used, it is older than 5 minutes, or the mount reconnected): check the position again, then tap Sync.");
            }
        }
        var camera = await _camera.ChooseAsync(ct).ConfigureAwait(false);
        switch (camera.UnavailableReason) {
            case "no_camera":
                throw new RefusalException(RefusalCatalog.NoCamera, "No camera is connected to take the plate-solve check's frame.");
            case "camera_busy":
                throw new RefusalException(RefusalCatalog.CameraBusy, MainCameraPointingSolver.CameraBusyMessage);
            case "solver_not_set":
                throw new RefusalException(RefusalCatalog.SolverNotSet, "ASTAP or its star database is not installed, so the frame cannot be solved.");
            default:
                break; // optics_not_set: the capture refuses with the missing optics named
        }
        var first = await MeasureAsync(camera, ct).ConfigureAwait(false);
        if (first.Failed is { } failed) {
            return Log(failed);
        }
        var verdict = Judge(camera, first.Value!, device);
        if (syncCheckId is null || verdict.Outcome != "mismatch") {
            if (verdict.Outcome == "mismatch") {
                var id = Guid.NewGuid().ToString("N");
                _offer = (id, first.Value!.Session, device, _clock.GetUtcNow());
                verdict = verdict with { CheckId = id };
            }
            return Log(Settle(camera, first.Value!, verdict, device));
        }
        // The user tapped Sync on the offer: sync the mount to the sky just solved (the existing Sync and its guards).
        var sky = first.Value!.Frame.Coordinates!;
        if (!await _mount.SyncForPoseCheckAsync(sky, ct).ConfigureAwait(false)) {
            return Log(verdict with {
                Outcome = "sync_refused", CheckId = null,
                Detail = "The mount (or one of the Sync guards: parked, the mount clock, its coordinate system, polar alignment) refused the Sync; the position stays unverified. Home is the way out.",
            });
        }
        // Re-read: a Sync the mount did not take leaves its reading where it was.
        var reread = await _mount.ReadNowAsync(ct).ConfigureAwait(false);
        var solvedInFrame = first.Value.SolvedInFrame;
        if (reread is null || FoldPose(reread.RaHours, reread.DecDegrees) is not { } r
                || SeparationDeg(r.RaHours, r.DecDeg, solvedInFrame.RA, solvedInFrame.Dec) > verdict.ToleranceDegrees + Drift(first.Value.After, reread)) {
            return Log(verdict with {
                Outcome = "sync_not_taken", CheckId = null,
                MountRaHours = reread is null ? verdict.MountRaHours : FoldPose(reread.RaHours, reread.DecDegrees)?.RaHours,
                MountDecDegrees = reread is null ? verdict.MountDecDegrees : FoldPose(reread.RaHours, reread.DecDegrees)?.DecDeg,
                Detail = "After the Sync the mount still does not read where the sky is: it did not take the Sync. The position stays unverified; Home is the way out.",
            });
        }
        // A second frame, after the Sync: only a model that agrees with the sky again is trusted.
        var second = await MeasureAsync(camera, ct).ConfigureAwait(false);
        if (second.Failed is { } secondFailed) {
            return Log(secondFailed with { Detail = "The mount took the Sync, but the second check failed: " + secondFailed.Detail });
        }
        var again = Judge(camera, second.Value!, device);
        if (again.Outcome == "mismatch") {
            return Log(again with {
                Outcome = "sync_not_taken", CheckId = null,
                Detail = "After the Sync a second solve still disagrees with the mount: the Sync did not hold. The position stays unverified; Home is the way out.",
            });
        }
        return Log(Settle(camera, second.Value!, again, device));
    }

    // One read-expose-read round. Failed is set when the round decides nothing.
    private sealed record Measurement(MountReading Before, MountReading After, PoseSolveFrame Frame, Coordinates SolvedInFrame,
        long Session, long Commands);

    private readonly record struct MeasureResult(Measurement? Value, PoseSolveCheckResultDto? Failed);

    private async Task<MeasureResult> MeasureAsync(PoseSolveCameraChoice camera, CancellationToken ct) {
        var session = _mount.SessionEpoch;
        var commands = _mount.PoseCommandCount;
        var before = await _mount.ReadNowAsync(ct).ConfigureAwait(false);
        if (before is null || FoldPose(before.RaHours, before.DecDegrees) is not { } b) {
            return new(null, Empty(camera, "mount_moved", "The mount did not answer a fresh read, so nothing was compared."));
        }
        if (before.Slewing != false) {
            return new(null, Empty(camera, "mount_moved", "The mount is moving (or does not say whether it is), so nothing was compared."));
        }
        var hint = new Coordinates(b.RaHours, b.DecDeg, before.Frame, Coordinates.RAType.Hours);
        var frame = await _camera.CaptureAndSolveAsync(camera, hint, ct).ConfigureAwait(false);
        var after = await _mount.ReadNowAsync(ct).ConfigureAwait(false);
        if (after is null || FoldPose(after.RaHours, after.DecDegrees) is not { } a || after.SessionEpoch != session
                || _mount.SessionEpoch != session || _mount.PoseCommandCount != commands || after.Slewing == true
                || SeparationDeg(a.RaHours, a.DecDeg, b.RaHours, b.DecDeg) > MovedDeg + Drift(before, after)) {
            return new(null, Empty(camera, "mount_moved",
                "The mount moved, was commanded or reconnected during the check, so the frame says nothing about where it reads now."));
        }
        if (!frame.Success || frame.Coordinates is null) {
            return new(null, Empty(camera, "solve_failed", "The frame did not solve: " + (frame.Failure ?? "no stars")
                + ". The position stays unverified; Home and Confirm Home are the ways out."));
        }
        return new(new Measurement(before, after, frame, frame.Coordinates.Transform(before.Frame), session, commands), null);
    }

    // The sky drifts 15″/s in RA past a mount that is not tracking; a tracking one holds it. Either may be the case.
    private static double Drift(MountReading earlier, MountReading later) =>
        Math.Abs((later.ReadAtUtc - earlier.ReadAtUtc).TotalSeconds) * 15.0 / 3600.0 + 1e-9;

    private static PoseSolveCheckResultDto Empty(PoseSolveCameraChoice camera, string outcome, string detail) =>
        new(outcome, camera.Camera ?? PoseSolveCameras.Main, null, null, null, null, null, null, MinToleranceDeg,
            "unknown", null, null, "none_stored", null, null, detail);

    private PoseSolveCheckResultDto Judge(PoseSolveCameraChoice camera, Measurement m, string device) {
        var mount = FoldPose(m.Before.RaHours, m.Before.DecDegrees)!.Value;
        var solved = m.SolvedInFrame;
        var separation = SeparationDeg(mount.RaHours, mount.DecDeg, solved.RA, solved.Dec);
        var uncertainty = m.Frame.PixelScaleArcsec is double scale ? scale * SolveUncertaintyPixels / 3600.0 : 0.0;
        var tolerance = Math.Max(MinToleranceDeg, 2.0 * uncertainty);
        var reported = m.Before.SideOfPier;
        // (a) the side the solved hour angle gives, unless near the pole or with no site.
        var expected = ExpectedSide(m.Frame.Coordinates!, m.Before.ReadAtUtc);
        // (b) the solved position angle against the last trusted one for this mount, camera and rotator angle.
        var (paCheck, paSide) = PositionAngleSide(AngleKey(device, camera), m.Frame.PositionAngleDeg);
        var german = CounterweightGuard.HasCounterweight(_mount.MountAlignmentMode);
        string outcome;
        string detail;
        if (german && reported != PierSide.pierUnknown && expected is PierSide e && e != reported) {
            outcome = "pier_side_disagrees";
            detail = $"The mount reports the {Side(reported)} side of the pier, but where the sky is puts it on the {Side(e)} side: its model may be a pier flip off. No Sync is offered; Home is the way out.";
        } else if (german && paSide is PierSide p && p != (reported != PierSide.pierUnknown ? reported : expected ?? p)) {
            outcome = "pier_side_disagrees";
            detail = $"The camera's position angle turned about 180° from the last trusted solve, so the mount is on the {Side(p)} side, not the one it reports: its model may be a pier flip off. No Sync is offered; Home is the way out.";
        } else if (german && (reported == PierSide.pierUnknown || expected is null) && paSide is null) {
            // No check could tell the side (no SideOfPier, too near the pole, no site; no stored angle): fail safe.
            outcome = "pier_side_unknown";
            detail = "The pier side could not be checked (the mount does not report it, the scope is too near the pole, or the site is not set, and no earlier solve's angle is stored), so the position is not trusted. Home is the way out.";
        } else if (separation <= tolerance) {
            outcome = "trusted";
            detail = string.Create(CultureInfo.InvariantCulture,
                $"The sky agrees with the mount within {separation:0.00}° (tolerance {tolerance:0.00}°), and the pier side agrees.");
        } else {
            outcome = "mismatch";
            detail = string.Create(CultureInfo.InvariantCulture,
                $"The mount reads RA {mount.RaHours:0.000} h, Dec {mount.DecDeg:0.00}°, but the sky is at RA {solved.RA:0.000} h, Dec {solved.Dec:0.00}° ({separation:0.0}° apart). Tap Sync to set the mount to the sky.");
        }
        return new(outcome, camera.Camera ?? PoseSolveCameras.Main, m.Before.Frame.ToString(), mount.RaHours, mount.DecDeg, solved.RA, solved.Dec,
            Math.Round(separation, 4), Math.Round(tolerance, 4), SideToken(reported), expected is PierSide x ? SideToken(x) : null,
            m.Frame.PositionAngleDeg, paCheck, null, null, detail);
    }

    // A trusted verdict clears the flag (refused when the mount moved or reconnected since the read) and keeps the angle.
    private PoseSolveCheckResultDto Settle(PoseSolveCameraChoice camera, Measurement m, PoseSolveCheckResultDto verdict, string device) {
        if (verdict.Outcome != "trusted") {
            return verdict;
        }
        var cleared = _mount.TrustPositionBySolve(m.Session, m.Commands, m.Before.DecDegrees);
        if (cleared is null) {
            return verdict with {
                Outcome = "mount_moved",
                Detail = "The mount was commanded or reconnected while the check ran, so its position was not trusted; check again.",
            };
        }
        _offer = null;
        var side = m.Before.SideOfPier != PierSide.pierUnknown ? m.Before.SideOfPier : ExpectedSide(m.Frame.Coordinates!, m.Before.ReadAtUtc);
        if (m.Frame.PositionAngleDeg is double pa && side is PierSide s and not PierSide.pierUnknown) {
            _angles.Store(AngleKey(device, camera), pa, s);
        }
        return verdict with { ClearedReason = cleared.Length == 0 ? null : cleared };
    }

    private static string AngleKey(string device, PoseSolveCameraChoice camera) => $"{device}|{camera.Key ?? camera.Camera}";

    private (string Check, PierSide? Side) PositionAngleSide(string key, double? positionAngleDeg) {
        if (positionAngleDeg is not double pa || _angles.Lookup(key) is not { } stored) {
            return ("none_stored", null);
        }
        var turned = Math.Abs(Wrap180(pa - stored.PositionAngleDeg));
        if (turned <= PositionAngleMatchDeg) {
            return ("agrees", stored.SideOfPier);
        }
        if (turned >= 180.0 - PositionAngleMatchDeg) {
            return ("flipped", stored.SideOfPier == PierSide.pierEast ? PierSide.pierWest : PierSide.pierEast);
        }
        return ("turned", null); // the camera was turned by hand since: no answer
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A site read or a NOVAS transform that fails means check (a) gives no answer; the fail-safe path then decides.")]
    private PierSide? ExpectedSide(Coordinates solved, DateTimeOffset at) {
        try {
            var site = _profiles?.GetSiteSettings();
            if (site is null || !double.IsFinite(site.LongitudeDeg) || (site.LatitudeDeg == 0 && site.LongitudeDeg == 0)) {
                return null;
            }
            if (Math.Abs(solved.Dec) > 90.0 - PoleBandDeg) {
                return null;
            }
            var lstHours = SiteAstrometry.LocalSiderealTimeDeg(at, site.LongitudeDeg) / 15.0;
            return MeridianFlip.ExpectedPierSide(solved, Angle.ByHours(lstHours));
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>A pose whose Dec reads past ±90° (the Dec axis over the pole) folded to the same pointing: Dec 180° − Dec,
    /// RA + 12 h. Null when it is no position at all. Internal static for tests.</summary>
    internal static (double RaHours, double DecDeg)? FoldPose(double raHours, double decDeg) {
        if (!double.IsFinite(raHours) || MountPoseTrust.FoldDec(decDeg) is not double dec) {
            return null;
        }
        var ra = Math.Abs(decDeg) > 90.0 ? raHours + 12.0 : raHours;
        return (((ra % 24.0) + 24.0) % 24.0, dec);
    }

    /// <summary>The angle between two positions on the sky (RA hours, Dec degrees), degrees.</summary>
    internal static double SeparationDeg(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg) {
        static double Rad(double deg) => deg * Math.PI / 180.0;
        var d1 = Rad(dec1Deg);
        var d2 = Rad(dec2Deg);
        var dra = Rad((ra2Hours - ra1Hours) * 15.0);
        var h = Math.Pow(Math.Sin((d2 - d1) / 2), 2) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(dra / 2), 2);
        return 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(h))) * 180.0 / Math.PI;
    }

    private static double Wrap180(double deg) {
        var d = ((deg % 360.0) + 360.0) % 360.0;
        return d > 180.0 ? d - 360.0 : d;
    }

    private static string SideToken(PierSide side) => side switch {
        PierSide.pierEast => "east",
        PierSide.pierWest => "west",
        _ => "unknown",
    };

    private static string Side(PierSide side) => side == PierSide.pierEast ? "east" : "west";

    private PoseSolveCheckResultDto Log(PoseSolveCheckResultDto result) {
        LogCheck(result.Outcome, result.Camera, result.SeparationDegrees, result.Detail);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope: plate-solve position check: {Outcome} ({Camera} camera, separation {SeparationDeg}°): {Detail} (#2173)")]
    private partial void LogCheck(string outcome, string camera, double? separationDeg, string detail);

    public void Dispose() => _gate.Dispose();
}
