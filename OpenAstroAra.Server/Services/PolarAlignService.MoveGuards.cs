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
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2067 (ARA-11 to ARA-13), #2081 items 3 and 5 — the guards on every RA turn Polar Align makes with the motor, so a
    /// turn stops the moment the mount does something it was not told to do. The turn runs at the rates the mount really
    /// offers at or below NINA's 3°/s and 1°/s (never rounded up, #2083), and is bounded by twice its planned travel time at
    /// those rates (<see cref="SlewTimeout"/>) and by 2 s without progress (<see cref="SlewStalled"/>). Every poll reads the
    /// position and the pier side: the Dec reading may not move more than 0.1° (<see cref="DecMoved"/>), the pier side may
    /// not change (<see cref="PierChanged"/>), and the RA reading may not jump further than the motor could turn it
    /// (<see cref="TurnMismatch"/>). Once the reading reaches the step the motor stops and the turn waits for the mount to
    /// stand still on fresh readings (the motion stays marked until then, so a Stop meanwhile still sends AbortSlew); a
    /// mount that runs on past the step by more than 2° is stopped with the confirmed stop and the run fails. The turn must
    /// then have arrived: within max(2°, 10%) of its step, with the Dec within 1° (<see cref="TurnMismatch"/>). After the
    /// three points the mount's Dec spread is logged, and a spread over 2″ is a warning on the result (NINA's TPPA warns the
    /// same). Between the frames of a measurement the solved position angle must stay within 10° (an RA turn of an
    /// equatorial mount keeps it); about 180° is a pier flip (<see cref="PierFlip"/>), which ends the run with tracking off.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The <c>polar_align.error</c> reason of a turn that made no progress for <see cref="AxisMoveStall"/>.</summary>
        internal const string SlewStalled = "slew_stalled";

        /// <summary>The <c>polar_align.error</c> reason of a turn that did not arrive within twice its planned time.</summary>
        internal const string SlewTimeout = "slew_timeout";

        /// <summary>The <c>polar_align.error</c> reason of a turn during which the Dec reading moved.</summary>
        internal const string DecMoved = "dec_moved";

        /// <summary>The <c>polar_align.error</c> reason of a turn during which the reported pier side changed.</summary>
        internal const string PierChanged = "pier_changed";

        /// <summary>The <c>polar_align.error</c> reason of frames whose field turned by about 180°: the pier flipped.</summary>
        internal const string PierFlip = "pier_flip";

        /// <summary>The <c>polar_align.error</c> reason of a turn that did not turn as commanded: it ended off its step, ran
        /// past it, its reading jumped, or the field rotated between two frames.</summary>
        internal const string TurnMismatch = "turn_mismatch";

        /// <summary>How far the Dec reading may move from the turn's start during an RA turn (#2081: an RA motor move should
        /// not move it at all; tighter than ARA-11's 1°).</summary>
        internal const double TurnDecLimitDeg = 0.1;

        /// <summary>How far past its step a turn may run before it is a runaway (ARA-11).</summary>
        internal const double RunawayPastStepDeg = 2.0;

        /// <summary>A turn arrives within max(this, <see cref="ArrivalToleranceFraction"/> of its step) of its step (ARA-12).</summary>
        internal const double ArrivalToleranceDeg = 2.0;
        internal const double ArrivalToleranceFraction = 0.1;

        /// <summary>How far the Dec may differ between a turn's start and its arrival (ARA-12).</summary>
        internal const double ArrivalDecLimitDeg = 1.0;

        /// <summary>The mount's Dec spread across the three points above which the result carries a warning (NINA's TPPA).</summary>
        internal const double DecSpreadWarnArcsec = 2.0;

        /// <summary>How far the solved position angle may turn between two frames of a measurement (ARA-13, Joey's rule on
        /// #2048, 2026-11-20): an RA turn of an equatorial mount keeps it near 0°; within this of 180° is a pier flip.</summary>
        internal const double FieldRotationToleranceDeg = 10.0;

        /// <summary>A poll's RA change beyond what the motor could turn since the last change (twice its fastest rate) plus
        /// this is a jump of the reading, not motion.</summary>
        internal const double ReadingJumpSlackDeg = 1.0;

        /// <summary>How long a stopped turn may take to stand still on fresh readings before its stop counts as not obeyed.</summary>
        internal TimeSpan AxisSteadyWindow { get; set; } = TimeSpan.FromSeconds(8);

        // The mount's Dec spread across the current measurement's three points, when it is over DecSpreadWarnArcsec (the
        // warning); null otherwise. Guarded by _gate; cleared per measurement (SetStageAsync).
        private double? _decSpreadArcsec;

        /// <summary>The rates a turn of <paramref name="axis"/> runs at: the fastest the mount offers at or below
        /// <see cref="AxisFastRateDegPerSec"/> and <see cref="AxisSlowRateDegPerSec"/> (never above, #2083). A mount that offers
        /// none at or below one of them fails the run with <c>slew_failed</c> before the motor starts.</summary>
        private (double Fast, double Slow) MatchTurnRates(TelescopeAxes axis) {
            var fast = MatchedRateOrFail(axis, AxisFastRateDegPerSec);
            var slow = MatchedRateOrFail(axis, AxisSlowRateDegPerSec);
            return (fast, Math.Min(slow, fast));
        }

        private double MatchedRateOrFail(TelescopeAxes axis, double requested) {
            var matched = _motionControl.MatchMoveAxisRate((int)axis, requested);
            if (matched is not double rate || !double.IsFinite(rate) || Math.Abs(rate) <= 0) {
                LogNoRateAtOrBelow(axis, requested);
                throw new RoutineFailedException("slew_failed", string.Create(CultureInfo.InvariantCulture,
                    $"the mount offers no motor speed at or below {requested:0.##}°/s for the turn (Polar Align never runs a motor faster than it asks) — check the mount's connection and its rates, then start again"));
            }
            var magnitude = Math.Min(Math.Abs(rate), Math.Abs(requested)); // never above the request, whatever the seam says
            if (magnitude < Math.Abs(requested)) {
                LogRateMatched(axis, requested, magnitude);
            }
            return magnitude;
        }

        /// <summary>The longest a turn of <paramref name="degrees"/> may take (NINA's MoveTimeoutFactor of 2): twice its travel
        /// time at the matched rates — fast for all but the last <see cref="AxisSlowZoneDeg"/>, slow for those — plus
        /// <see cref="AxisMoveBoundSlack"/>, since the stop lands on a reading that may trail the mount.</summary>
        internal TimeSpan TurnTimeBound(double degrees, double fastRate, double slowRate) {
            var slowDeg = Math.Min(degrees, AxisSlowZoneDeg);
            var fastDeg = degrees - slowDeg;
            var planned = (fastDeg / fastRate) + (slowDeg / slowRate);
            return TimeSpan.FromSeconds(2.0 * planned) + AxisMoveBoundSlack;
        }

        /// <summary>One poll's checks on the turn's reading against its start: the Dec reading moved more than
        /// <see cref="TurnDecLimitDeg"/>, or the pier side changed (both known).</summary>
        private void ThrowIfTurnLeftItsAxis(double startDecDeg, PierSide startSide, double nowDecDeg, PierSide nowSide) {
            var decMoved = Math.Abs(nowDecDeg - startDecDeg);
            if (decMoved > TurnDecLimitDeg) {
                LogTurnDecMoved(decMoved);
                throw new RoutineFailedException(DecMoved, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's Dec reading moved {decMoved:F2}° during an RA turn (an RA turn should not move it) — check the Dec clutch is tight and the mount turns only in RA, then start again"));
            }
            if (startSide is not PierSide.pierUnknown && nowSide is not PierSide.pierUnknown && nowSide != startSide) {
                LogTurnPierChanged(startSide, nowSide);
                throw new RoutineFailedException(PierChanged,
                    "the mount reported a different side of the pier during an RA turn — Polar Align never flips the mount; check where it points, then start again");
            }
        }

        /// <summary>A poll's RA reading that changed further than the motor could have turned it since the last change: a
        /// reading that jumped (a bad read, a driver glitch), never motion to steer by.</summary>
        private void ThrowIfReadingJumped(double changeDeg, double fastRate, TimeSpan sinceLastChange) {
            var possible = (2.0 * fastRate * sinceLastChange.TotalSeconds) + ReadingJumpSlackDeg;
            if (Math.Abs(changeDeg) > possible) {
                LogTurnReadingJumped(changeDeg, possible);
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's RA reading jumped {Math.Abs(changeDeg):F1}° in one read during a turn, more than the motor can turn — check the mount and its connection, then start again"));
            }
        }

        /// <summary>
        /// After a turn's stop: fresh full readings every <see cref="AxisMovePoll"/> until two of them, at least
        /// <see cref="AxisSettle"/> apart, both say not slewing and agree within <see cref="StopStillDeg"/>. Each reading is
        /// checked like a poll of the turn (Dec, pier side). A mount that runs on more than <see cref="RunawayPastStepDeg"/>
        /// past the step, or does not stand still within <see cref="AxisSteadyWindow"/>, did not obey the stop: the confirmed
        /// stop goes out at once (<see cref="FailTurnNotStoppedAsync"/>). Returns the last reading: where the turn ended.
        /// </summary>
        private async Task<(double RaDeg, double DecDeg, PierSide Side)> SettleAfterTurnAsync(
                (double RaDeg, double DecDeg) start, PierSide startSide, double degrees,
                Func<(double RaDeg, double DecDeg), (double RaDeg, double DecDeg), double> progress, int gen, CancellationToken ct) {
            var began = Clock.GetTimestamp(); // monotonic: a wall clock that steps must not stretch or cut the settle
            (double RaDeg, double DecDeg, long At)? anchor = null;
            while (true) {
                await Task.Delay(AxisMovePoll, ct).ConfigureAwait(false);
                if (!IsCurrent(gen)) {
                    throw new OperationCanceledException("the run was superseded while the turn settled");
                }
                ThrowIfStoppedFromOutside(gen);
                var r = await RunReadingAsync(gen, MountReadKind.Full, ct).ConfigureAwait(false);
                var at = Clock.GetTimestamp();
                var side = r.Side is PierSide.pierUnknown ? startSide : r.Side;
                NoteMountLeft((r.RaDeg, r.DecDeg), side);
                ThrowIfTurnLeftItsAxis(start.DecDeg, startSide, r.DecDeg, r.Side);
                var past = progress(start, (r.RaDeg, r.DecDeg)) - degrees;
                if (past > RunawayPastStepDeg) {
                    await FailTurnNotStoppedAsync(string.Create(CultureInfo.InvariantCulture,
                        $"the mount ran on {past:F1}° past the {degrees:F0}° turn after its motor was stopped"), gen).ConfigureAwait(false);
                }
                if (r.Slewing == false) {
                    if (anchor is { } a && Math.Abs(Wrap180(r.RaDeg - a.RaDeg)) < StopStillDeg && Math.Abs(r.DecDeg - a.DecDeg) < StopStillDeg) {
                        if (Clock.GetElapsedTime(a.At, at) >= AxisSettle) {
                            return (r.RaDeg, r.DecDeg, side);
                        }
                    } else {
                        anchor = (r.RaDeg, r.DecDeg, at);
                    }
                } else {
                    anchor = null; // slewing, or no Slewing answer: never still
                }
                if (Clock.GetElapsedTime(began, at) > AxisSteadyWindow) {
                    await FailTurnNotStoppedAsync(string.Create(CultureInfo.InvariantCulture,
                        $"the mount did not stand still within {AxisSteadyWindow.TotalSeconds:F0} s of the turn's stop"), gen).ConfigureAwait(false);
                }
            }
        }

        /// <summary>A turn whose mount did not obey its motor stop: the confirmed stop (AbortSlew, MoveAxis 0, fresh reads)
        /// goes out now, not after the run unwinds. Confirmed: the run fails with <see cref="TurnMismatch"/>. Not confirmed:
        /// with <see cref="MountDidNotStop"/> and the cut-power sentence; the motion stays marked, so the exit stops it again.</summary>
        private async Task FailTurnNotStoppedAsync(string what, int gen) {
            LogTurnNotStopped(what);
            var stopped = await StopMountMotionAsync("turn_not_stopped").ConfigureAwait(false);
            if (!IsCurrent(gen)) {
                throw new OperationCanceledException("the run was stopped while its turn was stopped");
            }
            if (!stopped) {
                throw new RoutineFailedException(MountDidNotStop, what + ". " + CutPowerSentence);
            }
            throw new RoutineFailedException(TurnMismatch,
                what + "; Ara stopped it — check the mount turns freely and stops when told, then start again");
        }

        /// <summary>ARA-12 — the turn arrived: its RA-reading turn (tracking holds the sky reading, so it is the motor's own
        /// turn) is within max(<see cref="ArrivalToleranceDeg"/>, <see cref="ArrivalToleranceFraction"/> × step) of the step,
        /// and the Dec within <see cref="ArrivalDecLimitDeg"/> of the start.</summary>
        private void ThrowIfTurnDidNotArrive(double degrees, double moved, double startDecDeg, double endDecDeg) {
            var tolerance = Math.Max(ArrivalToleranceDeg, ArrivalToleranceFraction * degrees);
            var decChange = Math.Abs(endDecDeg - startDecDeg);
            if (Math.Abs(moved - degrees) > tolerance || decChange > ArrivalDecLimitDeg) {
                LogTurnMismatch(degrees, moved, decChange);
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount reports a turn of {moved:F1}° (Dec {decChange:F2}° off) for a {degrees:F0}° RA turn — check it turns freely in RA only, then start again"));
            }
        }

        /// <summary>ARA-13 — the solved position angle between two consecutive frames of a measurement: within
        /// <see cref="FieldRotationToleranceDeg"/> of 0° (an RA turn of an equatorial mount keeps it); within it of 180° the
        /// pier flipped (<see cref="PierFlip"/>: the run ends, tracking off); anything else the field rotated as no RA turn
        /// does (<see cref="TurnMismatch"/>). Skipped when either solve gave no angle.</summary>
        internal static void ThrowIfFieldRotated(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, string frameId) {
            if (earlier.PositionAngleDeg is not double a || later.PositionAngleDeg is not double b) {
                return;
            }
            var turned = Math.Abs(Wrap180(b - a));
            if (turned <= FieldRotationToleranceDeg) {
                return;
            }
            if (turned >= 180.0 - FieldRotationToleranceDeg) {
                throw new RoutineFailedException(PierFlip, string.Create(CultureInfo.InvariantCulture,
                    $"the {frameId} frame is turned {turned:F0}° from the one before it: the mount flipped to the other side of the pier — Polar Align stopped it and turned tracking off; check where it points, then start again"));
            }
            throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                $"the {frameId} frame is turned {turned:F0}° from the one before it, which an RA turn does not do — check the mount turns only in RA, then start again"));
        }

        /// <summary>NINA's check after its three points: the mount's Dec readings at the three points, logged with their
        /// spread; a spread over <see cref="DecSpreadWarnArcsec"/> is kept for the result (status, progress, check result)
        /// and logged as a warning.</summary>
        private void NoteDecSpread(int gen, string measurement, double dec1, double dec2, double dec3) {
            var decs = new[] { dec1, dec2, dec3 };
            var spreadArcsec = (decs.Max() - decs.Min()) * 3600.0;
            var warn = spreadArcsec > DecSpreadWarnArcsec;
            if (warn) {
                LogDecSpreadWarning(measurement, spreadArcsec, DecSpreadWarnArcsec);
            } else {
                LogDecSpread(measurement, spreadArcsec);
            }
            lock (_gate) {
                if (_generation == gen) {
                    _decSpreadArcsec = warn ? Math.Round(spreadArcsec, 1) : null;
                }
            }
        }

        /// <summary>The current measurement's Dec-spread warning (arcseconds), or null.</summary>
        private double? RunDecSpreadArcsec(int gen) {
            lock (_gate) {
                return _generation == gen ? _decSpreadArcsec : null;
            }
        }

        [LoggerMessage(EventId = 4600, Level = LogLevel.Warning, Message = "§45 polar-align: the mount offers no {Axis} motor rate at or below {Requested:F2}°/s — no turn (slew_failed)")]
        private partial void LogNoRateAtOrBelow(TelescopeAxes axis, double requested);

        [LoggerMessage(EventId = 4601, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} turn rate {Requested:F2}°/s matched to the mount's {Matched:F3}°/s (at or below, never above)")]
        private partial void LogRateMatched(TelescopeAxes axis, double requested, double matched);

        [LoggerMessage(EventId = 4602, Level = LogLevel.Warning, Message = "§45 polar-align: the Dec reading moved {MovedDeg:F3}° during an RA turn — stopped (dec_moved)")]
        private partial void LogTurnDecMoved(double movedDeg);

        [LoggerMessage(EventId = 4603, Level = LogLevel.Warning, Message = "§45 polar-align: the pier side changed from {From} to {To} during an RA turn — stopped (pier_changed)")]
        private partial void LogTurnPierChanged(PierSide from, PierSide to);

        [LoggerMessage(EventId = 4604, Level = LogLevel.Warning, Message = "§45 polar-align: the RA reading jumped {ChangeDeg:F2}° in one poll (at most {PossibleDeg:F2}° is motion) — stopped (turn_mismatch)")]
        private partial void LogTurnReadingJumped(double changeDeg, double possibleDeg);

        [LoggerMessage(EventId = 4605, Level = LogLevel.Critical, Message = "§45 polar-align: {What} — sending the confirmed stop now")]
        private partial void LogTurnNotStopped(string what);

        [LoggerMessage(EventId = 4606, Level = LogLevel.Warning, Message = "§45 polar-align: a {Degrees:F1}° turn ended at {Moved:F2}° with the Dec {DecDeg:F3}° off — turn_mismatch")]
        private partial void LogTurnMismatch(double degrees, double moved, double decDeg);

        [LoggerMessage(EventId = 4607, Level = LogLevel.Information, Message = "§45 polar-align {Measurement}: the mount's Dec across the three points spread {SpreadArcsec:F1}″")]
        private partial void LogDecSpread(string measurement, double spreadArcsec);

        [LoggerMessage(EventId = 4608, Level = LogLevel.Warning, Message = "§45 polar-align {Measurement}: the mount's Dec across the three points spread {SpreadArcsec:F1}″, over {LimitArcsec:F0}″ — the result may be off (check the Dec clutch)")]
        private partial void LogDecSpreadWarning(string measurement, double spreadArcsec, double limitArcsec);

        [LoggerMessage(EventId = 4609, Level = LogLevel.Information, Message = "§45 polar-align {FrameId}: position angle {PositionAngleDeg:F1}° (turned {TurnedDeg:F1}° since the frame before)")]
        private partial void LogFieldRotation(string frameId, double positionAngleDeg, double turnedDeg);

        /// <summary><see cref="ThrowIfFieldRotated"/> with the log line of each compared frame.</summary>
        private void CheckFieldRotation(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, string frameId) {
            if (earlier.PositionAngleDeg is double a && later.PositionAngleDeg is double b) {
                var turned = Wrap180(b - a);
                LogFieldRotation(frameId, b, turned);
            }
            ThrowIfFieldRotated(earlier, later, frameId);
        }
    }
}
