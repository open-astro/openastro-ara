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
using System.Diagnostics.CodeAnalysis;
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
    /// same). Between the frames of a measurement the solved position angle must change as the RA turn changes it: within
    /// 10° of 0° (an RA turn of an equatorial mount keeps it), or of the change the geometry predicts on a roughly aligned
    /// mount (<see cref="FieldRotationFailure"/>); about 180° more is a pier flip (<see cref="PierFlip"/>), which ends the
    /// run with tracking off.
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

        /// <summary>How far the solved position angle may turn between two frames of a measurement from what the RA turn does
        /// (ARA-13, Joey's rule on #2048, 2026-11-20): an RA turn of an equatorial mount keeps it near 0° (or turns it by
        /// <see cref="PredictedFieldTurnDeg"/> on a roughly aligned mount); within this of 180° more is a pier flip.</summary>
        internal const double FieldRotationToleranceDeg = 10.0;

        /// <summary>A poll's RA change beyond what the motor could turn since the last change (twice its fastest rate) plus
        /// this is a jump of the reading, not motion.</summary>
        internal const double ReadingJumpSlackDeg = 1.0;

        /// <summary>How long a stopped turn may take to stand still on fresh readings before its stop counts as not obeyed.</summary>
        internal TimeSpan AxisSteadyWindow { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>#2166 rig check (HAE29C, AlpacaBridge 6.0.0-beta.4) — a move stopped by the clock that the settled
        /// reading shows short by more than this gets a top-up move for the rest, until the move's time bound. Under it the
        /// move counts as arrived where it stands.</summary>
        internal const double ClockStopTopUpDeg = 0.1;

        /// <summary>A segment stopped by the clock (the fast phase, the move, or a top-up) whose settled reading moved under
        /// this fraction of what the motor was told did not follow it: the mount stopped (a soft limit) or the reading froze.
        /// No top-up goes out then (no new motor command toward a limit), unless the reading was still moving at the stop
        /// (<see cref="ClockStopAdvancingDeg"/>): a mount slower than its speed creeps on to the time bound. A reading that
        /// lags but catches up in the settle reads nearly all of it (the rig: 0.38° for a 0.3° probe).</summary>
        internal const double ClockStopFollowFraction = 0.8;

        /// <summary>A reading step at the last poll before a clock stop above this is a reading still moving (creeping).</summary>
        internal const double ClockStopAdvancingDeg = 0.001;

        /// <summary>The fraction of a short move's commanded travel its settled reading may go the wrong way before it is
        /// wrong-way (<see cref="SettledWrongWayDeg"/>).</summary>
        internal const double SettledWrongWayFraction = 0.5;

        /// <summary>
        /// #2166 rig check — how far a move's motor has been told to turn: the rates commanded, times how long each ran, on
        /// the run's monotonic clock. The HAE29C's readings trailed its motor by up to 2 s (no RA change for 2 s of a
        /// 2.13°/s turn; a 0.3° Dec probe ran 1.9 s, 1°, before a hand Stop), so a move that stopped only on its reading
        /// overshot by the rate times that lag. Each move also stops once this reaches its distance; the settle's fresh
        /// readings then say where it ended.
        /// </summary>
        private sealed class CommandedTravel(TimeProvider clock) {
            private double _done;
            private double _rate;
            private long _since;

            /// <summary>A new segment from <paramref name="baseDeg"/> (the settled progress so far) at <paramref name="rate"/>.</summary>
            public void Reset(double baseDeg, double rate, long at) {
                _done = baseDeg;
                _rate = Math.Abs(rate);
                _since = at;
            }

            /// <summary>The motor now runs at <paramref name="rate"/> (a new speed, or the same one renewed).</summary>
            public void Run(double rate, long at) {
                _done = DegreesAt(at);
                _rate = Math.Abs(rate);
                _since = at;
            }

            public double DegreesAt(long at) => _done + (_rate * clock.GetElapsedTime(_since, at).TotalSeconds);

            /// <summary>How long until it reaches <paramref name="degrees"/> at the current rate (zero once it has).</summary>
            public TimeSpan TimeTo(double degrees, long at) =>
                _rate <= 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(Math.Max(0.0, (degrees - DegreesAt(at)) / _rate));
        }

        /// <summary>After a stop by the clock, how far a Dec move's settled reading may go the other way before the move is
        /// wrong-way: <see cref="WrongWayDeg"/>, or less for a short move: a reading that followed the motor (half of what it
        /// was told, <see cref="SettledWrongWayFraction"/>) the wrong way, but never under <see cref="ClockStopTopUpDeg"/>, so a
        /// tiny segment's noise is not wrong-way. A 0.3° leg never reaches 0.5° (#2189 review). An
        /// RA turn is at least 1°, so it keeps <see cref="WrongWayDeg"/>.</summary>
        private static double SettledWrongWayDeg(double commandedDeg) =>
            Math.Max(ClockStopTopUpDeg, Math.Min(WrongWayDeg, SettledWrongWayFraction * Math.Abs(commandedDeg)));

        /// <summary>The next poll's wait: <see cref="AxisMovePoll"/>, or sooner when the motor's commanded travel reaches the
        /// move's distance before then, so the clock's stop lands on time.</summary>
        private TimeSpan NextAxisPoll(CommandedTravel travel, double degrees) {
            var due = travel.TimeTo(degrees, Clock.GetTimestamp());
            return due < AxisMovePoll ? due : AxisMovePoll;
        }

        /// <summary>A segment stopped by the clock that, settled, moved under <see cref="ClockStopFollowFraction"/> of what the
        /// motor was told in that segment (<paramref name="moved"/> and <paramref name="commanded"/> are the segment's own),
        /// on a reading that was not moving at the stop: the mount stopped short (a soft limit) or its reading froze. The move
        /// fails as <see cref="SlewStalled"/> with no top-up, so nothing is commanded toward a limit again. A reading still
        /// moving (<paramref name="advancing"/>) creeps on: its top-ups end at the time bound (<see cref="SlewTimeout"/>).</summary>
        private void ThrowIfReadingDidNotFollow(TelescopeAxes axis, double moved, double commanded, double degrees, bool advancing, string unit) {
            if (advancing || moved >= ClockStopFollowFraction * commanded) {
                return;
            }
            LogClockStopNotFollowed(axis, moved, commanded);
            throw new RoutineFailedException(SlewStalled, string.Create(CultureInfo.InvariantCulture,
                $"the mount's reading stopped changing after {Math.Max(moved, 0):F1}° of the {degrees:F1}° {unit} while its motor was told {commanded:F1}° — check it can move freely and its connection, then try again"));
        }

        /// <summary>#2189 rig check — true when the clock's stop is due before <paramref name="read"/> answers: a read that
        /// blocks (the HAE29C's took 0.6 s and more right after a motor command) must not hold the motor on past its distance.</summary>
        private static async Task<bool> ClockDueBeforeReadAsync(Task read, TimeSpan due) {
            if (read.IsCompleted || due == TimeSpan.MaxValue) {
                return false;
            }
            var first = await Task.WhenAny(read, Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, CancellationToken.None)).ConfigureAwait(false);
            return first != read;
        }

        /// <summary>The read that lost the race to the clock's stop: waited for after the stop, so it never overlaps the
        /// settle's reads; whatever it answers or throws is set aside (the settle reads afresh).</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The read that lost the race to a stop is set aside; its fault must not mask the stop or the settle.")]
        private async Task DrainReadAsync(Task read) {
            try {
                await read.ConfigureAwait(false);
            } catch (Exception ex) {
                LogDrainedReadFailed(ex);
            }
        }

        [LoggerMessage(EventId = 4653, Level = LogLevel.Debug, Message = "§45 polar-align: a read that lost the race to the clock's stop failed (set aside)")]
        private partial void LogDrainedReadFailed(Exception exception);

        [LoggerMessage(EventId = 4597, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} move stopped by the clock at {Commanded:F2}° commanded (the reading showed {Read:F2}° of {Degrees:F2}°)")]
        private partial void LogClockStop(TelescopeAxes axis, double commanded, double read, double degrees);

        [LoggerMessage(EventId = 4598, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} move settled at {Moved:F2}° of {Degrees:F2}°: a {Rest:F2}° top-up")]
        private partial void LogClockStopTopUp(TelescopeAxes axis, double moved, double degrees, double rest);

        [LoggerMessage(EventId = 4599, Level = LogLevel.Warning, Message = "§45 polar-align: {Axis} reading moved {Moved:F2}° while the motor was told {Commanded:F2}° — the reading did not follow the motor")]
        private partial void LogClockStopNotFollowed(TelescopeAxes axis, double moved, double commanded);

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
        private void ThrowIfReadingJumped(double changeDeg, double fastRate, TimeSpan sinceLastChange, string axisName = "RA") {
            var possible = (2.0 * fastRate * sinceLastChange.TotalSeconds) + ReadingJumpSlackDeg;
            if (Math.Abs(changeDeg) > possible) {
                LogTurnReadingJumped(changeDeg, possible);
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's {axisName} reading jumped {Math.Abs(changeDeg):F1}° in one read during a turn, more than the motor can turn — check the mount and its connection, then start again"));
            }
        }

        /// <summary>
        /// After a turn's stop: fresh full readings every <see cref="AxisMovePoll"/> until two of them, at least
        /// <see cref="AxisSettle"/> apart, both say not slewing and agree within <see cref="StopStillDeg"/>. Each reading is
        /// checked like a poll of the turn (Dec, pier side). A mount that runs on more than <see cref="RunawayPastStepDeg"/>
        /// past the step, or does not stand still within <see cref="AxisSteadyWindow"/>, did not obey the stop: the confirmed
        /// stop goes out at once (<see cref="FailTurnNotStoppedAsync"/>). Returns the last reading: where the turn ended.
        /// <paramref name="checkPose"/> (#2166) replaces the RA turn's Dec and pier-side check for a Dec move, whose
        /// readings fold a Dec past the pole back over it (<paramref name="foldPastPole"/>).
        /// </summary>
        private async Task<(double RaDeg, double DecDeg, PierSide Side)> SettleAfterTurnAsync(
                (double RaDeg, double DecDeg) start, PierSide startSide, double degrees,
                Func<(double RaDeg, double DecDeg), (double RaDeg, double DecDeg), double> progress, int gen, CancellationToken ct,
                Action<FreshPointing>? checkPose = null, bool foldPastPole = false) {
            var began = Clock.GetTimestamp(); // monotonic: a wall clock that steps must not stretch or cut the settle
            (double RaDeg, double DecDeg, long At)? anchor = null;
            while (true) {
                await Task.Delay(AxisMovePoll, ct).ConfigureAwait(false);
                if (!IsCurrent(gen)) {
                    throw new OperationCanceledException("the run was superseded while the turn settled");
                }
                ThrowIfStoppedFromOutside(gen);
                var r = await RunReadingAsync(gen, MountReadKind.Full, ct, foldPastPole).ConfigureAwait(false);
                var at = Clock.GetTimestamp();
                var side = r.Side is PierSide.pierUnknown ? startSide : r.Side;
                NoteMountLeft((r.RaDeg, r.DecDeg), side);
                if (checkPose is null) {
                    ThrowIfTurnLeftItsAxis(start.DecDeg, startSide, r.DecDeg, r.Side);
                } else {
                    checkPose(r);
                }
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
            // A read that came back after a Stop, Emergency Stop or a park took the mount (the read ignores cancellation):
            // no stop round from here. Emergency Stop and a park own the mount (the run's exit skips its stop for them too),
            // and the confirmed stop's resends would abort the park and hold Emergency Stop's rung.
            if (!IsCurrent(gen)) {
                throw new OperationCanceledException("the run was stopped before its turn's runaway was stopped");
            }
            ThrowIfStoppedFromOutside(gen);
            // #2066 — a takeover or an end that gave up on this run fenced its stops: the mount is someone else's now.
            var fence = RunStopFence(gen);
            if (StopsFenced(fence)) {
                throw new OperationCanceledException("the run was fenced before its turn's runaway was stopped");
            }
            LogTurnNotStopped(what);
            var stopped = await StopMountMotionAsync("turn_not_stopped", fence).ConfigureAwait(false);
            if (!IsCurrent(gen) || stopped is null) {
                throw new OperationCanceledException("the run was stopped or fenced while its turn was stopped");
            }
            if (stopped == false) {
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

        /// <summary>ARA-13 — the solved position angle between two consecutive frames of a measurement, around the RA turn
        /// <paramref name="mountTurnDeg"/> between them (the mount's signed RA-reading turn): fails the run when
        /// <see cref="FieldRotationFailure"/> says so. Skipped when either solve gave no angle.</summary>
        internal static void ThrowIfFieldRotated(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, double mountTurnDeg, string frameId) {
            if (FieldRotationFailure(earlier, later, mountTurnDeg) is not { } reason) {
                return;
            }
            var turned = Math.Abs(Wrap180(later.PositionAngleDeg!.Value - earlier.PositionAngleDeg!.Value));
            if (reason == PierFlip) {
                throw new RoutineFailedException(PierFlip, string.Create(CultureInfo.InvariantCulture,
                    $"the {frameId} frame is turned {turned:F0}° from the one before it: the mount flipped to the other side of the pier — Polar Align stopped it and turned tracking off; check where it points, then start again"));
            }
            throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                $"the {frameId} frame is turned {turned:F0}° from the one before it, which an RA turn does not do — check the mount turns only in RA, then start again"));
        }

        /// <summary>ARA-13 — null when the field turned between two frames as an RA turn turns it; <see cref="PierFlip"/>
        /// when it turned by about 180° more; else <see cref="TurnMismatch"/>. Null when either solve gave no angle. "As an
        /// RA turn turns it": within <see cref="FieldRotationToleranceDeg"/> of 0° (Joey's rule: an equatorial mount keeps
        /// the position angle while it turns in RA), or of the turn <see cref="PredictedFieldTurnDeg"/> gives (either sign:
        /// the solver's angle convention). The camera turns about the mount's RA axis, not the true pole, so on a roughly
        /// aligned mount close to the pole (the 2–5° start the app asks for) the position angle, measured from true north,
        /// swings by up to ~20° in a 10° turn although nothing flipped. Far from the pole the two agree.</summary>
        internal static string? FieldRotationFailure(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, double mountTurnDeg) {
            if (earlier.PositionAngleDeg is not double a || later.PositionAngleDeg is not double b) {
                return null;
            }
            var turned = Wrap180(b - a);
            var predicted = PredictedFieldTurnDeg(earlier, later, mountTurnDeg) ?? 0.0;
            bool Near(double target) => Math.Abs(Wrap180(turned - target)) <= FieldRotationToleranceDeg;
            if (Near(0.0) || Near(predicted) || Near(-predicted)) {
                return null;
            }
            return Near(180.0) || Near(180.0 + predicted) || Near(180.0 - predicted) ? PierFlip : TurnMismatch;
        }

        /// <summary>
        /// The position-angle change (degrees, east of north) of a camera that turned rigidly about the axis that takes
        /// <paramref name="earlier"/>'s centre to <paramref name="later"/>'s by the mount's signed RA turn
        /// <paramref name="mountTurnDeg"/> (RA increasing = right-handed about the north celestial pole). That axis is solved
        /// from the two centres and the turn (Rodrigues: g = tan(ψ/2)·n with g × (a + b) = b − a; of its two solutions the
        /// one nearer the pole's line), and true north at the first frame is turned with it. Null when the turn or the
        /// frames give no axis (a turn under 0.5°, a frame on the pole, antipodal centres).
        /// </summary>
        internal static double? PredictedFieldTurnDeg(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, double mountTurnDeg) {
            if (Math.Abs(mountTurnDeg) < 0.5 || Math.Abs(mountTurnDeg) > 170.0) {
                return null;
            }
            var a = UnitVector(earlier.RaDegJnow, earlier.DecDegJnow);
            var b = UnitVector(later.RaDegJnow, later.DecDegJnow);
            var t = Math.Tan(mountTurnDeg * Math.PI / 360.0);
            var sum = Add(a, b);
            var sum2 = Dot(sum, sum);
            if (sum2 < 1e-9) {
                return null;
            }
            var basis = Scale(Cross(a, b), 2.0 / sum2);
            var lambda = Math.Sqrt(Math.Max(0.0, ((t * t) - Dot(basis, basis)) / sum2));
            var g1 = Add(basis, Scale(sum, lambda));
            var g2 = Add(basis, Scale(sum, -lambda));
            var g = g1.Z / t >= g2.Z / t ? g1 : g2; // the axis direction n = g / tan(ψ/2) nearer the north pole's line
            var (northA, _) = LocalNorthEast(earlier.RaDegJnow, earlier.DecDegJnow);
            var (northB, eastB) = LocalNorthEast(later.RaDegJnow, later.DecDegJnow);
            if (northA is null || northB is null) {
                return null;
            }
            var n = northA.Value;
            var gxn = Cross(g, n);
            var turnedNorth = Add(n, Scale(Add(gxn, Cross(g, gxn)), 2.0 / (1.0 + Dot(g, g))));
            return Math.Atan2(Dot(turnedNorth, eastB), Dot(turnedNorth, northB.Value)) * 180.0 / Math.PI;
        }

        private static (double X, double Y, double Z) UnitVector(double raDeg, double decDeg) {
            var ra = raDeg * Math.PI / 180.0;
            var dec = decDeg * Math.PI / 180.0;
            return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }

        /// <summary>The unit vectors toward north and east on the sky at (RA, Dec); north is null on the pole.</summary>
        private static ((double X, double Y, double Z)? North, (double X, double Y, double Z) East) LocalNorthEast(double raDeg, double decDeg) {
            var ra = raDeg * Math.PI / 180.0;
            var dec = decDeg * Math.PI / 180.0;
            var east = (-Math.Sin(ra), Math.Cos(ra), 0.0);
            return Math.Cos(dec) < 1e-9 ? (null, east) : ((-Math.Sin(dec) * Math.Cos(ra), -Math.Sin(dec) * Math.Sin(ra), Math.Cos(dec)), east);
        }

        private static (double X, double Y, double Z) Add((double X, double Y, double Z) u, (double X, double Y, double Z) v) =>
            (u.X + v.X, u.Y + v.Y, u.Z + v.Z);

        private static (double X, double Y, double Z) Scale((double X, double Y, double Z) u, double k) => (u.X * k, u.Y * k, u.Z * k);

        private static double Dot((double X, double Y, double Z) u, (double X, double Y, double Z) v) => (u.X * v.X) + (u.Y * v.Y) + (u.Z * v.Z);

        private static (double X, double Y, double Z) Cross((double X, double Y, double Z) u, (double X, double Y, double Z) v) =>
            ((u.Y * v.Z) - (u.Z * v.Y), (u.Z * v.X) - (u.X * v.Z), (u.X * v.Y) - (u.Y * v.X));

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

        [LoggerMessage(EventId = 4620, Level = LogLevel.Warning, Message = "§45 polar-align: the mount offers no {Axis} motor rate at or below {Requested:F2}°/s — no turn (slew_failed)")]
        private partial void LogNoRateAtOrBelow(TelescopeAxes axis, double requested);

        [LoggerMessage(EventId = 4621, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} turn rate {Requested:F2}°/s matched to the mount's {Matched:F3}°/s (at or below, never above)")]
        private partial void LogRateMatched(TelescopeAxes axis, double requested, double matched);

        [LoggerMessage(EventId = 4622, Level = LogLevel.Warning, Message = "§45 polar-align: the Dec reading moved {MovedDeg:F3}° during an RA turn — stopped (dec_moved)")]
        private partial void LogTurnDecMoved(double movedDeg);

        [LoggerMessage(EventId = 4623, Level = LogLevel.Warning, Message = "§45 polar-align: the pier side changed from {From} to {To} during an RA turn — stopped (pier_changed)")]
        private partial void LogTurnPierChanged(PierSide from, PierSide to);

        [LoggerMessage(EventId = 4624, Level = LogLevel.Warning, Message = "§45 polar-align: the RA reading jumped {ChangeDeg:F2}° in one poll (at most {PossibleDeg:F2}° is motion) — stopped (turn_mismatch)")]
        private partial void LogTurnReadingJumped(double changeDeg, double possibleDeg);

        [LoggerMessage(EventId = 4625, Level = LogLevel.Critical, Message = "§45 polar-align: {What} — sending the confirmed stop now")]
        private partial void LogTurnNotStopped(string what);

        [LoggerMessage(EventId = 4626, Level = LogLevel.Warning, Message = "§45 polar-align: a {Degrees:F1}° turn ended at {Moved:F2}° with the Dec {DecDeg:F3}° off — turn_mismatch")]
        private partial void LogTurnMismatch(double degrees, double moved, double decDeg);

        [LoggerMessage(EventId = 4627, Level = LogLevel.Information, Message = "§45 polar-align {Measurement}: the mount's Dec across the three points spread {SpreadArcsec:F1}″")]
        private partial void LogDecSpread(string measurement, double spreadArcsec);

        [LoggerMessage(EventId = 4628, Level = LogLevel.Warning, Message = "§45 polar-align {Measurement}: the mount's Dec across the three points spread {SpreadArcsec:F1}″, over {LimitArcsec:F0}″ — the result may be off (check the Dec clutch)")]
        private partial void LogDecSpreadWarning(string measurement, double spreadArcsec, double limitArcsec);

        [LoggerMessage(EventId = 4629, Level = LogLevel.Information, Message = "§45 polar-align {FrameId}: position angle {PositionAngleDeg:F1}° (turned {TurnedDeg:F1}° since the frame before)")]
        private partial void LogFieldRotation(string frameId, double positionAngleDeg, double turnedDeg);

        /// <summary><see cref="ThrowIfFieldRotated"/> with the log line of each compared frame.</summary>
        private void CheckFieldRotation(PolarAlignSolveOutcome earlier, PolarAlignSolveOutcome later, double mountTurnDeg, string frameId) {
            if (earlier.PositionAngleDeg is double a && later.PositionAngleDeg is double b) {
                var turned = Wrap180(b - a);
                LogFieldRotation(frameId, b, turned);
            }
            ThrowIfFieldRotated(earlier, later, mountTurnDeg, frameId);
        }
    }
}
