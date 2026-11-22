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
using System.Globalization;
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
    /// <see cref="IMountMotionControl.SessionEpoch"/>), on a daemon restart (it lives in memory), and when the mount was
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
        internal sealed record SweepPose(double RaDeg, double DecDeg, long At, PierSide Side);

        /// <summary>The session's sweep origin: the pose, the way its first run turned (+1 east, −1 west in RA-reading
        /// terms), and the mount session it belongs to.</summary>
        private sealed record SweepOrigin(SweepPose Pose, double AwaySign, long SessionEpoch);

        /// <summary>What a start from <c>Pose</c> does with the origin: a new one, or how far the axis has turned from it
        /// (+ east) and which way the run turns first.</summary>
        internal sealed record SweepPlan(SweepPose Pose, bool NewOrigin, double OffsetDeg, double DirSign, double LimitDeg, long SessionEpoch);

        // Guarded by _gate. The origin is set when a confirmed start launches its run; the last pose is where Polar
        // Align's own moves last left the mount (each motor reading, and the exit's after the stop).
        private SweepOrigin? _sweepOrigin;
        private SweepPose? _sweepLastLeft;
        /// <summary>The limit of the run the last confirmed start launched (its plan's <see cref="SweepPlan.LimitDeg"/>).</summary>
        private double _sweepLimitDeg = MaxSweepFromOriginDeg;

        /// <summary>The RA axis's mechanical turn between two readings, in RA-reading terms (+ east): the reading's change
        /// less the Earth's turn between them. Tracking keeps the reading still while the axis turns west; with tracking
        /// off the reading drifts while the axis stands still. Wraps across 0/24 h. Pure, for the tests.</summary>
        internal static double MechanicalTurnDeg(double fromRaDeg, double toRaDeg, double elapsedSeconds) =>
            Wrap180(toRaDeg - fromRaDeg - (elapsedSeconds * SiderealDegPerSecond));

        /// <summary>How far the mount moved between two readings beyond what tracking explains, on or off: the smaller of
        /// the reading's change (tracking on keeps it still) and the axis's mechanical turn (tracking off keeps the axis
        /// still). Tracking is not a move by other means: counting it as one would forget the origin a few minutes after a
        /// run that handed tracking back (<c>stop_tracking_when_done</c> off, or Guiding took the camera), and the next run
        /// would build its band from where the last one ended, the walk toward the pier this bound stops. The offset
        /// from the origin still counts the tracking turn (<see cref="MechanicalTurnDeg"/>). Pure, for the tests.</summary>
        internal static double MovedByOtherMeansDegBetween(double fromRaDeg, double toRaDeg, double elapsedSeconds) =>
            Math.Min(Math.Abs(Wrap180(toRaDeg - fromRaDeg)), Math.Abs(MechanicalTurnDeg(fromRaDeg, toRaDeg, elapsedSeconds)));

        private static double SecondsBetween(long from, long to) => (to - from) / (double)Stopwatch.Frequency;

        /// <summary>The sweep part of a start's plan from the mount's pose read now: a new origin (the first start since
        /// the mount connected, or the mount was moved by other means), whose first turn goes the profile's way
        /// (<paramref name="newOriginSign"/>, <c>polar_align.east_direction</c>, #2081: never the mount's hour angle, which a
        /// wrong clock or a false zero makes arbitrary); or the offset from the origin, with the turns back toward it, so
        /// repeated Starts never walk the mount one way. Refuses (<c>polar-align-sweep-limit</c>) a start whose turns would
        /// take the axis past the limit, the motor-direction test it makes first (<paramref name="testDeg"/>, either way)
        /// included. <paramref name="epoch"/> is the mount session the pose was read in (#2066: the run keeps it), and
        /// <paramref name="side"/> the pier side that reading reported (never the refresh cache's).
        /// <paramref name="bandToleranceDeg"/> is <see cref="BandToleranceDeg"/> but for tests.</summary>
        internal SweepPlan PlanSweep(double raDeg, double decDeg, double maxSweepDeg, long epoch, PierSide side,
                double newOriginSign, double testDeg, double bandToleranceDeg = BandToleranceDeg) {
            var pose = new SweepPose(raDeg, decDeg, Stopwatch.GetTimestamp(), side);
            SweepOrigin? origin;
            SweepPose? lastLeft;
            lock (_gate) {
                origin = _sweepOrigin;
                lastLeft = _sweepLastLeft;
            }
            var limitDeg = Math.Max(MaxSweepFromOriginDeg, maxSweepDeg + bandToleranceDeg);
            var why = origin is null ? "no origin this mount session"
                : origin.SessionEpoch != epoch ? "the mount reconnected"
                : MovedSince(lastLeft, pose) is { } moved ? moved
                : null;
            if (why is not null) {
                LogSweepNewOrigin(raDeg, why);
                // #2081 — the direction test turns testDeg first, so it counts here as on a continued start.
                if (maxSweepDeg + testDeg > limitDeg) {
                    throw new RefusalException(RefusalCatalog.PolarAlignSweepLimit, string.Create(CultureInfo.InvariantCulture,
                        $"This start's turns and its {testDeg:F1}° motor-direction test would take RA past the {limitDeg:F0}° limit; set smaller steps and confirm again"),
                        new Dictionary<string, object?> {
                            ["offset_deg"] = 0.0,
                            ["limit_deg"] = limitDeg,
                        });
                }
                return new SweepPlan(pose, NewOrigin: true, OffsetDeg: 0.0, newOriginSign >= 0 ? 1.0 : -1.0, limitDeg, epoch);
            }
            var offset = FoldTolerantSignedTurnDeg(MechanicalTurnDeg(origin!.Pose.RaDeg, raDeg, SecondsBetween(origin.Pose.At, pose.At)));
            // Back toward the origin; at the origin (within a turn's overshoot), the way the origin's first run turned.
            var dirSign = Math.Abs(offset) <= BandToleranceDeg ? origin.AwaySign : -Math.Sign(offset);
            // #2081 — the direction test turns testDeg first, toward the turns or (an inverted driver) away from them.
            var farDeg = Math.Max(Math.Abs(offset), Math.Abs(offset + (dirSign * maxSweepDeg))) + testDeg;
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
            var turned = MovedByOtherMeansDegBetween(lastLeft.RaDeg, now.RaDeg, SecondsBetween(lastLeft.At, now.At));
            if (turned > MovedByOtherMeansDeg) {
                return $"the RA axis moved {turned:F1}° since Polar Align last moved it, more than tracking (on or off) explains";
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
            _sweepLimitDeg = plan.LimitDeg;
        }

        /// <summary>#2082 review — before each turn of a run, from the mount's reading at the turn's start: refuses
        /// (<c>sweep_limit</c>) a turn that would take the RA axis past the run's limit plus <see cref="BandToleranceDeg"/>.
        /// The start's plan cannot see the time a run spends at its points: with several frames per point at long
        /// exposures, tracking turns the axis many degrees between the turns. The offset counts that tracking turn
        /// (<see cref="MechanicalTurnDeg"/>). No check without an origin of this mount session.</summary>
        private void ThrowIfTurnPassesSweepLimit(double fromRaDeg, double signedDeltaDeg) {
            SweepOrigin? origin;
            double limitDeg;
            lock (_gate) {
                origin = _sweepOrigin;
                limitDeg = _sweepLimitDeg;
            }
            if (origin is null || origin.SessionEpoch != _motionControl.SessionEpoch) {
                return;
            }
            var offset = FoldTolerantSignedTurnDeg(MechanicalTurnDeg(origin.Pose.RaDeg, fromRaDeg,
                SecondsBetween(origin.Pose.At, Stopwatch.GetTimestamp())));
            var farDeg = Math.Abs(offset + signedDeltaDeg);
            if (farDeg > limitDeg + BandToleranceDeg) {
                throw new RoutineFailedException("sweep_limit",
                    $"the RA axis is {Math.Abs(offset):F0}° from where Polar Align started (tracking turns it while the frames are taken), and the next {Math.Abs(signedDeltaDeg):F0}° turn would take it past the {limitDeg:F0}° limit — slew it back near the pole (counterweights down), or take fewer or shorter frames per point, and start again");
            }
        }

        /// <summary>Where Polar Align's own move left the mount (a motor reading, with the pier side of a fresh reading): the
        /// next start compares with it. Never the refresh cache (#2066): it is read on every poll of a turn.</summary>
        private void NoteMountLeft((double RaDeg, double DecDeg) reading, PierSide side) {
            var pose = new SweepPose(reading.RaDeg, reading.DecDeg, Stopwatch.GetTimestamp(), side);
            lock (_gate) {
                _sweepLastLeft = pose;
            }
        }

        /// <summary>The exit's reading after the mount stopped (best-effort: a failed read keeps the last motor reading).</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A best-effort read on the exit path: its fault must never mask the exit's outcome.")]
        private async Task NoteMountLeftAfterStopAsync() {
            try {
                if (await TrustedPointingJnowOrNullAsync(CancellationToken.None).ConfigureAwait(false) is { } now) {
                    NoteMountLeft((now.RaDeg, now.DecDeg), now.Side);
                }
            } catch (Exception ex) {
                LogSweepExitReadFailed(ex);
            }
        }

        /// <summary>Where Polar Align last left the mount (RA, degrees JNOW); null when forgotten. For the tests.</summary>
        internal double? SweepLastLeftRaDeg {
            get {
                lock (_gate) {
                    return _sweepLastLeft?.RaDeg;
                }
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
