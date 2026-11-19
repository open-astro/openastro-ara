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
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2089 — the RA sweep bound across runs. With no Home between runs (#2048), each Start used to build its band from
    /// wherever the mount was, so "Adjust again" after Verify turned the axis 20° → 40° from the first start, and repeated
    /// runs walked it toward the pier. Polar Align now keeps a <b>sweep origin</b> for the session: the pose of the first
    /// start the user confirmed after the mount connected. Every later start turns back toward it, and a start whose turns
    /// would take the RA axis more than <see cref="MaxSweepFromOriginDeg"/> from it is refused.
    /// <para>Everything here is an RA <i>difference</i> the mount reports, less the Earth's turn over the daemon's own
    /// monotonic clock: never the mount's clock, its LST or its absolute RA, which can all be wrong (#2071 a stale clock,
    /// #2070 a false zero). The origin is forgotten on a new mount session (connect, disconnect, fault trip:
    /// <see cref="ITelescopeService.SessionEpoch"/>), on a daemon restart (it lives in memory), and when the mount was
    /// moved by other means (a slew, the hand pad, by hand) since Polar Align last turned it: then the confirm step says
    /// the pose is a new start, the user confirms it as one, and it becomes the origin.</para>
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The most any planned turn may take the RA axis from the session's sweep origin (#2089). A run whose
        /// own band is wider (turns over 15°, the user's setting) keeps to its band plus the stage tolerance instead.</summary>
        internal const double MaxSweepFromOriginDeg = 30.0;

        /// <summary>A mount that reads more than this far, by its mechanical RA turn, from where Polar Align's last move
        /// left it was moved by other means: its pose is a new start the user must confirm as one.</summary>
        internal const double MovedByOtherMeansDeg = 1.0;

        /// <summary>A pose the mount reported: its RA (degrees, as read), when (a <see cref="Stopwatch"/> timestamp, the
        /// daemon's monotonic clock), and its pier side.</summary>
        private sealed record SweepPose(double RaDeg, double DecDeg, long At, PierSide Side);

        /// <summary>The session's sweep origin: the pose, the way its first run turned (+1 east, −1 west in RA-reading
        /// terms), and the mount session it belongs to.</summary>
        private sealed record SweepOrigin(SweepPose Pose, double AwaySign, long SessionEpoch);

        /// <summary>What a start from <c>Pose</c> does with the origin: a new one, or how far the axis has turned from it
        /// (+ east) and which way the run turns first.</summary>
        private sealed record SweepPlan(SweepPose Pose, bool NewOrigin, double OffsetDeg, double DirSign, double LimitDeg, long SessionEpoch);

        // Guarded by _gate. The origin is set when a confirmed start launches its run; the last pose is where Polar
        // Align's own moves last left the mount (each motor reading, and the exit's after the stop).
        private SweepOrigin? _sweepOrigin;
        private SweepPose? _sweepLastLeft;

        private long MountSessionEpoch => (_mount as ITelescopeService)?.SessionEpoch ?? 0;

        /// <summary>The RA axis's mechanical turn between two readings, in RA-reading terms (+ east): the reading's change
        /// less the Earth's turn between them. Tracking keeps the reading still while the axis turns west; with tracking
        /// off the reading drifts while the axis stands still. Wraps across 0/24 h. Pure, for the tests.</summary>
        internal static double MechanicalTurnDeg(double fromRaDeg, double toRaDeg, double elapsedSeconds) =>
            Wrap180(toRaDeg - fromRaDeg - (elapsedSeconds * SiderealDegPerSecond));

        private static double SecondsBetween(long from, long to) => (to - from) / (double)Stopwatch.Frequency;

        /// <summary>The sweep part of a start's plan from the mount's pose read now: a new origin (the first start since
        /// the mount connected, or the mount was moved by other means), whose first turn goes away from the meridian as
        /// before (<see cref="AwayFromMeridianSign"/>); or the offset from the origin, with the turns back toward it.
        /// Refuses (<c>polar-align-sweep-limit</c>) a start whose turns would take the axis past the limit.</summary>
        private SweepPlan PlanSweep(double raDeg, double decDeg, double maxSweepDeg) {
            var pose = new SweepPose(raDeg, decDeg, Stopwatch.GetTimestamp(), _mount.GetInfo().SideOfPier);
            var epoch = MountSessionEpoch;
            SweepOrigin? origin;
            SweepPose? lastLeft;
            lock (_gate) {
                origin = _sweepOrigin;
                lastLeft = _sweepLastLeft;
            }
            var limitDeg = Math.Max(MaxSweepFromOriginDeg, maxSweepDeg + BandToleranceDeg);
            var why = origin is null ? "no origin this mount session"
                : origin.SessionEpoch != epoch ? "the mount reconnected"
                : MovedSince(lastLeft, pose) is { } moved ? moved
                : null;
            if (why is not null) {
                LogSweepNewOrigin(raDeg, why);
                return new SweepPlan(pose, NewOrigin: true, OffsetDeg: 0.0, AwayFromMeridianSign(raDeg), limitDeg, epoch);
            }
            var offset = FoldTolerantSignedTurnDeg(MechanicalTurnDeg(origin!.Pose.RaDeg, raDeg, SecondsBetween(origin.Pose.At, pose.At)));
            // Back toward the origin; at the origin (within a turn's overshoot), the way the origin's first run turned.
            var dirSign = Math.Abs(offset) <= BandToleranceDeg ? origin.AwaySign : -Math.Sign(offset);
            var farDeg = Math.Max(Math.Abs(offset), Math.Abs(offset + (dirSign * maxSweepDeg)));
            LogSweepFromOrigin(offset, dirSign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West, farDeg, limitDeg);
            if (farDeg > limitDeg) {
                throw new RefusalException(RefusalCatalog.PolarAlignSweepLimit,
                    $"The mount has turned {Math.Abs(offset):F0}° from where Polar Align started; slew it back near the pole (counterweights down) and confirm again",
                    new Dictionary<string, object?> {
                        ["offset_deg"] = Math.Round(offset, 1),
                        ["limit_deg"] = limitDeg,
                    });
            }
            return new SweepPlan(pose, NewOrigin: false, offset, dirSign, limitDeg, epoch);
        }

        /// <summary>Why the mount at <paramref name="now"/> was moved by other means since Polar Align's moves left it at
        /// <paramref name="lastLeft"/>; null when it was not.</summary>
        private static string? MovedSince(SweepPose? lastLeft, SweepPose now) {
            if (lastLeft is null) {
                return "no pose left by Polar Align";
            }
            var turned = MechanicalTurnDeg(lastLeft.RaDeg, now.RaDeg, SecondsBetween(lastLeft.At, now.At));
            if (Math.Abs(turned) > MovedByOtherMeansDeg) {
                return $"the RA axis turned {turned:F1}° since Polar Align last moved it";
            }
            if (lastLeft.Side != PierSide.pierUnknown && now.Side != PierSide.pierUnknown && lastLeft.Side != now.Side) {
                return "the pier side changed since Polar Align last moved it";
            }
            return null;
        }

        /// <summary>A confirmed start launched its run from <paramref name="plan"/>'s pose: a new origin is set; either way
        /// the pose is where Polar Align last saw the mount. Caller holds <c>_gate</c>.</summary>
        private void AdoptSweepPlanLocked(SweepPlan plan) {
            if (plan.NewOrigin) {
                _sweepOrigin = new SweepOrigin(plan.Pose, plan.DirSign, plan.SessionEpoch);
            }
            _sweepLastLeft = plan.Pose;
        }

        /// <summary>Where Polar Align's own move left the mount (a motor reading): the next start compares with it.</summary>
        private void NoteMountLeft((double RaDeg, double DecDeg) reading) {
            var pose = new SweepPose(reading.RaDeg, reading.DecDeg, Stopwatch.GetTimestamp(), _mount.GetInfo().SideOfPier);
            lock (_gate) {
                _sweepLastLeft = pose;
            }
        }

        /// <summary>The exit's reading after the mount stopped (best-effort: a failed read keeps the last motor reading).</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A best-effort read on the exit path: its fault must never mask the exit's outcome.")]
        private async Task NoteMountLeftAfterStopAsync() {
            try {
                if (await DriverPointingJnowAsync(CancellationToken.None).ConfigureAwait(false) is { } now) {
                    NoteMountLeft(now);
                }
            } catch (Exception ex) {
                LogSweepExitReadFailed(ex);
            }
        }

        [LoggerMessage(EventId = 4571, Level = LogLevel.Information, Message = "§45 polar-align sweep: RA {RaDeg:F2}° becomes the session's origin once confirmed ({Why})")]
        private partial void LogSweepNewOrigin(double raDeg, string why);

        [LoggerMessage(EventId = 4572, Level = LogLevel.Information, Message = "§45 polar-align sweep: the RA axis is {OffsetDeg:F1}° from the session's origin (+ east); the run turns {Direction} first, at most {FarDeg:F1}° from it (limit {LimitDeg:F0}°)")]
        private partial void LogSweepFromOrigin(double offsetDeg, string direction, double farDeg, double limitDeg);

        [LoggerMessage(EventId = 4573, Level = LogLevel.Debug, Message = "§45 polar-align sweep: the exit's pointing read failed; the last motor reading stands")]
        private partial void LogSweepExitReadFailed(Exception exception);
    }
}
