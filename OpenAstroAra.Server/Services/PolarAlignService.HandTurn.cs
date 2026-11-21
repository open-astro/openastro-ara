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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2083 — the hand-turn run, NINA's Three Point Polar Alignment "manual mode": the user turns the RA axis by hand
    /// (the hand pad, or the clutch open) between the three solves, and the run sees each turn in the solves. It sends the
    /// mount no motor move, no GoTo and no Find Home, never drives a Polar Aligner's
    /// motors (the person turns its bolts, as with auto off) and never turns tracking on while it runs: the only mount
    /// writes it makes are tracking off for the live adjust loop (§45.8 needs the camera still in alt/az) and the exit's,
    /// as every run's — the stop, and tracking off or the tracking it found given back (<c>stop_tracking_when_done</c>
    /// off and the run finished, or Guiding taking the camera). The rest of the routine — the three-point fit, the live
    /// loop, the check's verdict — is the motor run's own. Like a motor run (#2082) it makes one measurement per Start, its
    /// two turns go the way the user confirmed (<see cref="PlanSweep"/>), and a start within 3° of the pole is refused
    /// before it begins (<c>polar-align-on-axis</c>, or the run's <c>on_axis</c> when the mount was moved there since).
    /// <para>How far a turn went is read from the mount when it can be: a hand-pad turn moves the mount's RA reading,
    /// which turns about the mount's own axis wherever the pole is, so the run shows that angle and takes the turn at
    /// <see cref="HandTurnTakenFraction"/> of the step. With the clutch open the reading stays put, and the solves alone
    /// cannot give the angle (a solved RA change is an angle about the celestial pole, not about the RA axis, and with a
    /// polar error of a few degrees a true 10° turn can read anywhere from backwards to 40°): the run then claims no
    /// angle, and takes the turn once the solved pointing has moved at least what that share of the step moves it at
    /// the least distance from the axis it allows for (<see cref="HandTurnPolarErrorAllowanceDeg"/>). Either way the
    /// mount must have stopped: two solves in a row within <see cref="HandTurnStillDeg"/>, and with the clutch open three,
    /// with <see cref="HandTurnClutchMinWait"/> gone since the turn was asked (<see cref="HandTurnSettled"/>). A frame
    /// whose mount read fails, or takes over <see cref="HandTurnReadTimeout"/>, is not measured: only a reading read at both ends that stood means the clutch is open. A
    /// frame where the mount's tracking changed is not measured either. A turn that takes the axis
    /// past the run's band (its two turns plus <see cref="BandToleranceDeg"/>) fails the run (<c>hand_turn_too_far</c>),
    /// and a clutch turn leaves the mount's reading wrong, so a motor start is refused until the mount is synced or
    /// reconnected (<see cref="NoteClutchTurned"/>). Frames that do not solve while the user turns (trailed stars) are
    /// expected and only retried.</para>
    /// <para>#2082 — each point averages <c>polar_align.frames_per_point</c> frames as a motor run's does
    /// (<see cref="SolveSeedFrameAsync"/>): the first where the mount points, the next two once the turn is taken and the
    /// mount still (with one frame per point, the solve that took the turn is the point).</para>
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The share of the step the solves must show before a hand turn counts: a person turns about 10°,
        /// not exactly.</summary>
        internal const double HandTurnTakenFraction = 0.8;

        /// <summary>Two solves this close (RA turn and Dec, degrees) mean the user has stopped turning.</summary>
        internal const double HandTurnStillDeg = 0.1;

        /// <summary>A mount RA reading that moved this much (degrees, beyond tracking) since the last point turned with the
        /// axis (the hand pad): the run measures the turn from it. Less, and the clutch is open.</summary>
        internal const double HandTurnReadingFollowsDeg = 1.0;

        /// <summary>The polar error a clutch turn allows for (degrees): the camera may sit this much nearer the RA axis, or
        /// further from it, than its distance from the celestial pole. A first, eyeballed setup is often 2–3° out.</summary>
        internal const double HandTurnPolarErrorAllowanceDeg = 3.0;

        // Guarded by _gate: whether the current run turns by hand (for the status), and what it waits for now.
        private bool _handTurn;
        private PolarAlignHandTurnDto? _handTurnWait;

        /// <summary>One point of a hand-turn measurement: the solve, when, and the mount's RA/Dec reading then (null when
        /// the read failed).</summary>
        private sealed record HandPoint(PolarAlignSolveOutcome S, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading);

        /// <summary>What one solve shows of a hand turn. <c>TurnedDeg</c>: the turn the asked way since the last point, by
        /// the mount's reading; null when the reading did not move (the clutch is open), as no angle can be claimed then.
        /// <c>Reached</c>: enough of a turn to take. <c>ByClutch</c>: the solves moved that far while the reading, read at
        /// both ends, stood. <c>SweepDeg</c>: how far the axis has turned from the measurement's first point, at least
        /// (either way). <c>ReadingUnknown</c>: the mount's read failed on this frame, so it is not measured (the next
        /// frame reads again).</summary>
        internal sealed record HandTurnSample(double? TurnedDeg, bool Reached, bool ByClutch, double SweepDeg, bool ReadingUnknown = false);

        /// <summary>With the clutch open, solves in a row at rest before a point is taken (with the hand pad, two).</summary>
        internal const int HandTurnClutchSolvesAtRest = 3;

        /// <summary>With the clutch open, the least time from asking for a turn to taking it: near the pole a small nudge
        /// moves the solves the distance a turn needs, and a person turning 10° takes longer than this.</summary>
        internal static readonly TimeSpan DefaultHandTurnClutchMinWait = TimeSpan.FromSeconds(5);

        /// <summary><see cref="DefaultHandTurnClutchMinWait"/>; tests shorten it.</summary>
        internal TimeSpan HandTurnClutchMinWait { get; set; } = DefaultHandTurnClutchMinWait;

        /// <summary>Whether the user has stopped turning long enough for the point to be taken: two solves in a row at rest
        /// when the mount's reading measured the turn (<paramref name="byReading"/>); with the clutch open
        /// <see cref="HandTurnClutchSolvesAtRest"/> in a row and <paramref name="clutchMinWait"/> since the turn was asked.
        /// Pure, for the tests.</summary>
        /// <param name="solvesAtRest">Solves in a row within <see cref="HandTurnStillDeg"/> of the one before, this one
        /// included (1: this one moved, or follows a failed frame).</param>
        internal static bool HandTurnSettled(bool byReading, int solvesAtRest, TimeSpan sinceAsked, TimeSpan clutchMinWait) =>
            byReading ? solvesAtRest >= 2 : solvesAtRest >= HandTurnClutchSolvesAtRest && sinceAsked >= clutchMinWait;

        /// <summary>How far the RA axis turned between two solves, in RA-reading terms (+ east), by the user's hand: the
        /// solved RA's change, less the Earth's turn when the mount does not track (tracking holds the solve still while
        /// the axis stands, so with tracking on the change is the turn itself). Pure, for the tests.</summary>
        internal static double HandTurnDeg(double fromRaDeg, double toRaDeg, double elapsedSeconds, bool tracking) =>
            Wrap180(toRaDeg - fromRaDeg - (tracking ? 0.0 : elapsedSeconds * SiderealDegPerSecond));

        /// <summary>The three points of a hand-turn measurement: a solve where the mount points, then two solves after the
        /// user turned RA by about <paramref name="stepDeg"/> each, the way the user confirmed (<c>band.AwaySign</c>). No
        /// mount command at all: the mount's reading is only read, as the first frame's solve hint and for the on-axis
        /// check every run makes (<see cref="StartPointingOffTheAxisAsync"/>).</summary>
        /// <param name="names">The three frames' ids.</param>
        /// <param name="noteSolve">Called with 1, 2, 3 as each point is taken (the check's progress).</param>
        private async Task<((PolarAlignSolveOutcome S, DateTimeOffset At) A, (PolarAlignSolveOutcome S, DateTimeOffset At) B,
                (PolarAlignSolveOutcome S, DateTimeOffset At) C)> ThreePointsByHandAsync(
                LiveCapture capture, string[] names, double stepDeg, RunBand band, Func<Task> renew,
                PolarAlignSettingsDto settings, int gen, CancellationToken ct, Func<int, Task>? noteSolve = null) {
            var hint = await StartPointingOffTheAxisAsync(gen, ct).ConfigureAwait(false);
            var (a, aAt) = await SolveSeedFrameAsync(capture, names[0], hint, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve(names[0], a.RaDegJnow, a.DecDegJnow);
            LogHandTurnMeasurement(band.AwaySign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West, stepDeg);
            if (noteSolve is not null) {
                await noteSolve(1).ConfigureAwait(false);
            }
            var start = new HandPoint(a, aAt, hint);
            var b = await WaitForHandTurnAsync(capture, names[1], start, start, point: 2, band, stepDeg, renew, settings, gen, ct).ConfigureAwait(false);
            if (noteSolve is not null) {
                await noteSolve(2).ConfigureAwait(false);
            }
            var c = await WaitForHandTurnAsync(capture, names[2], start, b, point: 3, band, stepDeg, renew, settings, gen, ct).ConfigureAwait(false);
            if (noteSolve is not null) {
                await noteSolve(3).ConfigureAwait(false);
            }
            return ((a, aAt), (b.S, b.At), (c.S, c.At));
        }

        /// <summary>What the solve <paramref name="now"/> shows of the turn from <paramref name="from"/> (the last point)
        /// toward <paramref name="dirSign"/>, and how far the axis is from <paramref name="start"/> (the measurement's
        /// first point). Pure, for the tests.</summary>
        internal static HandTurnSample MeasureHandTurn(
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) start,
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) from,
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) now,
                double dirSign, double stepDeg, bool tracking) {
            double? readingTurn = null;
            if (from.Reading is { } rf && now.Reading is { } rn) {
                readingTurn = dirSign * ReadingTurnDeg(rf.RaDeg, rn.RaDeg, (now.At - from.At).TotalSeconds, tracking);
            }
            var takenDeg = HandTurnTakenFraction * stepDeg;
            // The sweep from the first point: the mount's reading when it turned with the axis, and at least the turn the
            // solved distance needs at the furthest the camera can sit from the axis (a lower bound with the clutch open).
            var sweep = SolvedTurnAtLeastDeg(start, now, tracking);
            if (start.Reading is { } rs && now.Reading is { } rn2) {
                var byReading = Math.Abs(ReadingTurnDeg(rs.RaDeg, rn2.RaDeg, (now.At - start.At).TotalSeconds, tracking));
                sweep = Math.Max(sweep, byReading);
            }
            if (readingTurn is double turned && Math.Abs(turned) >= HandTurnReadingFollowsDeg) {
                return new HandTurnSample(turned, turned >= takenDeg, ByClutch: false, sweep);
            }
            // A failed read (null) says nothing about the clutch: the frame is not measured, and the next one reads again.
            if (now.Reading is null && from.Reading is not null) {
                return new HandTurnSample(null, Reached: false, ByClutch: false, sweep, ReadingUnknown: true);
            }
            var moved = UserFrameSeparationDeg(from, now, tracking);
            var needed = ChordDeg(takenDeg, Math.Max(MinCheckConeDeg, PoleDistanceDeg(from.DecDeg) - HandTurnPolarErrorAllowanceDeg));
            var reached = moved >= needed;
            // Only a reading read at both ends that stood says the clutch is open; with the last point's reading unknown the
            // solves still take the turn, and nothing is flagged.
            return new HandTurnSample(null, reached, ByClutch: reached && readingTurn is not null, sweep);
        }

        /// <summary>The mount reading's turn (+ east) between two readings by the user's hand: the change less the Earth's
        /// turn when the mount does not track (<see cref="HandTurnDeg"/>), folded like a motor turn's
        /// (<see cref="FoldTolerantSignedTurnDeg"/>).</summary>
        private static double ReadingTurnDeg(double fromRaDeg, double toRaDeg, double elapsedSeconds, bool tracking) =>
            Wrap180(FoldTolerantSignedTurnDeg(toRaDeg - fromRaDeg) - (tracking ? 0.0 : elapsedSeconds * SiderealDegPerSecond));

        /// <summary>The distance (degrees) the pointing moved between two solves by the user's hand: with tracking off the
        /// earlier one is first moved by the Earth's turn since, as the camera stands still in the ground frame.</summary>
        private static double UserFrameSeparationDeg(
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) from,
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) now, bool tracking) =>
            PolarAlignGeometry.SeparationDeg(
                from.RaDeg + (tracking ? 0.0 : (now.At - from.At).TotalSeconds * SiderealDegPerSecond), from.DecDeg,
                now.RaDeg, now.DecDeg);

        /// <summary>The least turn about the axis that moves the pointing the solved distance from the first point: the
        /// camera at its furthest from the axis (its pole distance plus <see cref="HandTurnPolarErrorAllowanceDeg"/>).</summary>
        private static double SolvedTurnAtLeastDeg(
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) start,
                (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) now, bool tracking) {
            var moved = UserFrameSeparationDeg(start, now, tracking);
            var coneDeg = Math.Min(90.0, PoleDistanceDeg(start.DecDeg) + HandTurnPolarErrorAllowanceDeg);
            var half = Math.Sin(moved * Math.PI / 360.0) / Math.Sin(coneDeg * Math.PI / 180.0);
            return 2.0 * Math.Asin(Math.Min(1.0, half)) * 180.0 / Math.PI;
        }

        /// <summary>The distance (degrees) a pointing <paramref name="coneDeg"/> from the axis moves in a turn of
        /// <paramref name="turnDeg"/> about it.</summary>
        internal static double ChordDeg(double turnDeg, double coneDeg) =>
            2.0 * Math.Asin(Math.Sin(coneDeg * Math.PI / 180.0) * Math.Sin(turnDeg * Math.PI / 360.0)) * 180.0 / Math.PI;

        private static double PoleDistanceDeg(double decDeg) => 90.0 - Math.Min(90.0, Math.Abs(decDeg));

        /// <summary>#2082 — the mount's reading read at <paramref name="readAt"/>, as it stands at <paramref name="at"/> with the
        /// axis still: with tracking off the RA reading runs on with the Earth's turn (<see cref="HandTurnDeg"/>). A point
        /// averaged over several frames is taken at their mean time, after the reading.</summary>
        internal static (double RaDeg, double DecDeg)? ReadingAt((double RaDeg, double DecDeg)? reading, DateTimeOffset readAt,
                DateTimeOffset at, bool tracking) =>
            reading is { } r && !tracking ? (r.RaDeg + (at - readAt).TotalSeconds * SiderealDegPerSecond, r.DecDeg) : reading;

        private static (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) Flat(HandPoint p) =>
            (p.S.RaDegJnow, p.S.DecDegJnow, p.At, p.Reading);

        /// <summary>Ask the user to turn RA by about <paramref name="stepDeg"/> the way <paramref name="band"/> says, then
        /// capture and solve until the turn is in (<see cref="MeasureHandTurn"/>, two still solves). Publishes
        /// <c>polar_align.hand_turn</c> after every solve, with how far the mount's reading shows the axis turned (none
        /// with the clutch open). Waits as long as the user needs: Stop ends it; a turn past the band fails the run.</summary>
        /// <returns>The taken point.</returns>
        private async Task<HandPoint> WaitForHandTurnAsync(
                LiveCapture capture, string frameId, HandPoint start, HandPoint from, int point, RunBand band,
                double stepDeg, Func<Task> renew, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var dirSign = band.AwaySign;
            var direction = dirSign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West;
            var limitDeg = band.MaxSweepDeg + BandToleranceDeg;
            LogHandTurnAsked(point, stepDeg, direction);
            await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, TurnedDeg: null)).ConfigureAwait(false);
            (double RaDeg, double DecDeg)? hint = (from.S.RaDegJnow, from.S.DecDegJnow);
            var askedAt = DateTimeOffset.UtcNow;
            HandPoint? last = null;
            var atRest = 0; // solves in a row at rest, the last one included (HandTurnSettled)
            bool? lastTracking = null;
            double? lastTurned = null;
            var frame = 0;
            var failures = 0;
            try {
                while (true) {
                    ct.ThrowIfCancellationRequested();
                    ThrowIfStoppedFromOutside(gen); // #2066 — a Stop Mount or Emergency Stop ends the run here too
                    await renew().ConfigureAwait(false);
                    frame++;
                    var id = frameId + "-" + frame.ToString(CultureInfo.InvariantCulture);
                    var timing = new FrameTiming();
                    var s = await capture(id, hint, settings, timing, ct).ConfigureAwait(false);
                    failures = s.Success ? 0 : failures + 1;
                    await CompleteFrameAsync(gen, id, s, timing, failures).ConfigureAwait(false);
                    var tracking = s.Success && _mount.GetInfo().TrackingEnabled;
                    if (s.Success && lastTracking is bool was && was != tracking) {
                        // Tracking changed (the user, or the mount): the daemon's cached state can lag the mount by a
                        // poll, so the frame before may have used the old one, and this one's change mixes both. Not
                        // measured; the next solves decide.
                        lastTracking = tracking;
                        last = null;
                        atRest = 0;
                    } else if (s.Success) {
                        lastTracking = tracking;
                        var at = DateTimeOffset.UtcNow;
                        hint = (s.RaDegJnow, s.DecDegJnow);
                        var now = new HandPoint(s, at, await HandTurnReadingAsync(gen, ct).ConfigureAwait(false));
                        var sample = MeasureHandTurn(Flat(start), Flat(from), Flat(now), dirSign, stepDeg, tracking);
                        if (sample.ByClutch) {
                            NoteClutchTurned();
                        }
                        if (sample.SweepDeg > limitDeg) {
                            LogHandTurnTooFar(point, sample.SweepDeg, limitDeg);
                            throw new RoutineFailedException("hand_turn_too_far", string.Create(CultureInfo.InvariantCulture,
                                $"RA has turned about {sample.SweepDeg:F0}° from the first frame, more than this measurement's {limitDeg:F0}° — turn it back near where you started, then start again"));
                        }
                        var still = last is { } l
                            && Math.Abs(HandTurnDeg(l.S.RaDegJnow, s.RaDegJnow, (at - l.At).TotalSeconds, tracking)) <= HandTurnStillDeg
                            && Math.Abs(s.DecDegJnow - l.S.DecDegJnow) <= HandTurnStillDeg;
                        atRest = still ? atRest + 1 : 1;
                        // A frame whose mount read failed keeps the last angle shown rather than blanking it.
                        var turned = sample.TurnedDeg is double t ? Math.Round(t, 1) : sample.ReadingUnknown ? lastTurned : null;
                        lastTurned = turned;
                        if (sample.Reached && HandTurnSettled(sample.TurnedDeg is not null, atRest, at - askedAt, HandTurnClutchMinWait)) {
                            LogHandTurnTaken(point, sample.TurnedDeg ?? double.NaN, sample.ByClutch);
                            await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, turned, Waiting: false)).ConfigureAwait(false);
                            if (FramesPerPoint(settings) == 1) {
                                LogSeedSolve(frameId, s.RaDegJnow, s.DecDegJnow);
                                return now;
                            }
                            // #2082 — the turn is in and the mount still: the point is measured as a motor run's is, from
                            // polar_align.frames_per_point frames averaged (retaken once when scattered, else flagged
                            // noisy), its frame ids going on from the wait's.
                            var (mean, meanAt) = await SolveSeedFrameAsync(capture, frameId, (s.RaDegJnow, s.DecDegJnow), settings, gen, ct,
                                attemptsBefore: frame).ConfigureAwait(false);
                            LogSeedSolve(frameId, mean.RaDegJnow, mean.DecDegJnow);
                            return new HandPoint(mean, meanAt, ReadingAt(now.Reading, at, meanAt, tracking));
                        }
                        last = now;
                        await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, turned)).ConfigureAwait(false);
                    } else {
                        last = null; // a trailed frame mid-turn: the next solves decide
                        atRest = 0;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(settings.LoopCadenceMs), ct).ConfigureAwait(false);
                }
            } finally {
                lock (_gate) {
                    if (_generation == gen) {
                        _handTurnWait = null;
                    }
                }
            }
        }

        /// <summary>How long a hand-turn run waits for the mount's reading on each solved frame; tests shorten it.</summary>
        internal TimeSpan HandTurnReadTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>The mount's reading for one solved frame of a hand-turn wait: a fresh position read
        /// (<see cref="ReadFreshPointingAsync"/>), bounded by <see cref="HandTurnReadTimeout"/>: a slow AlpacaBridge must not
        /// stall the wait loop. A read that times out, or one that cannot be trusted (no answer, the (0, 0) placeholder, too
        /// old), is a failed read (null): the frame is not measured, and the next one reads again. A reconnect since the
        /// run's start ends the run (<see cref="ThrowIfMountReconnected"/>), and a Dec past the pole fails it as a motor
        /// run's turn does (<see cref="PoseAmbiguous"/>).</summary>
        private async Task<(double RaDeg, double DecDeg)?> HandTurnReadingAsync(int gen, CancellationToken ct) {
            var read = ReadFreshPointingAsync(RunSessionEpoch(gen), ct, MountReadKind.Position);
            try {
                var p = await read.WaitAsync(HandTurnReadTimeout, ct).ConfigureAwait(false);
                if (p.DecPastPoleDeg is double dec) {
                    LogPoseAmbiguous(dec);
                    throw new RoutineFailedException(PoseAmbiguous, PoseAmbiguousMessage(dec));
                }
                if (p.Why is not null) {
                    ThrowIfMountReconnected(gen);
                    return null;
                }
                return (p.RaDeg, p.DecDeg);
            } catch (TimeoutException) {
                LogHandTurnReadTimedOut(HandTurnReadTimeout.TotalSeconds);
                return null;
            } finally {
                // A read left behind (timed out, or the run cancelled meanwhile) may still fault: observe it.
                _ = read.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        /// <summary>The three-point fit of a hand-turn run: the motor run's fit (<see cref="FitThreePointOrFail"/>) without
        /// its cross-check against the mount's reported turns, which a hand turn with the clutch open never moves. The
        /// geometry still has to hold: the pointings well off the axis, and each solved turn a real one.</summary>
        /// <returns>The axis, and the two solved turns about it together (degrees).</returns>
        internal static ((double RaDeg, double DecDeg, double ConeDeg) Axis, double TurnDeg) FitThreePointByHandOrFail(
                (PolarAlignSolveOutcome S, DateTimeOffset At) p1, (PolarAlignSolveOutcome S, DateTimeOffset At) p2,
                (PolarAlignSolveOutcome S, DateTimeOffset At) p3, bool north) {
            double ToRef((PolarAlignSolveOutcome S, DateTimeOffset At) p) =>
                p.S.RaDegJnow + (p3.At - p.At).TotalSeconds * SiderealDegPerSecond;
            var ra1 = ToRef(p1);
            var ra2 = ToRef(p2);
            var ra3 = p3.S.RaDegJnow;
            (double RaDeg, double DecDeg, double ConeDeg) axis;
            try {
                axis = PolarAlignGeometry.FitAxisThreePoint(ra1, p1.S.DecDegJnow, ra2, p2.S.DecDegJnow, ra3, p3.S.DecDegJnow, north);
            } catch (ArgumentOutOfRangeException ex) {
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
            var turn1 = Math.Abs(PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra1, p1.S.DecDegJnow, ra2, p2.S.DecDegJnow));
            var turn2 = Math.Abs(PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra2, p2.S.DecDegJnow, ra3, p3.S.DecDegJnow));
            if (axis.ConeDeg < MinCheckConeDeg || turn1 < MinHandTurnAboutAxisDeg || turn2 < MinHandTurnAboutAxisDeg) {
                throw new RoutineFailedException("axis_fit_failed",
                    $"the three solves do not describe a turn about one axis (turns {turn1:F1}° and {turn2:F1}°, cone {axis.ConeDeg:F2}°) — " +
                    "turn only the RA axis, about the step each time, then start again");
            }
            return (axis, turn1 + turn2);
        }

        /// <summary>A solved turn about the fitted axis under this is no turn: the fit has nothing to stand on.</summary>
        internal const double MinHandTurnAboutAxisDeg = 1.0;

        /// <summary>#2067 ARA-13 in a hand-turn run: the field check of a motor run (<see cref="CheckFieldRotation"/>) between
        /// each two of the three frames, after the fit. A hand turn has no mount turn to predict the field's rotation from
        /// (with the clutch open the reading stands), so each step's solved turn about the fitted axis stands in for it,
        /// signed as an RA turn about the north pole (the fitted axis is the hemisphere's own end). Nothing moves the mount
        /// in such a run, so the check waits for the fit; a frame turned about 180° (the tube swung over) is still
        /// <see cref="PierFlip"/>, any other turn an RA turn does not make <see cref="TurnMismatch"/>.</summary>
        private void CheckFieldRotationByHand((double RaDeg, double DecDeg, double ConeDeg) axis,
                PolarAlignSolveOutcome a, PolarAlignSolveOutcome b, PolarAlignSolveOutcome c, bool north, string bId, string cId) {
            CheckFieldRotation(a, b, HandStepRaTurnDeg(axis, a, b, north), bId);
            CheckFieldRotation(b, c, HandStepRaTurnDeg(axis, b, c, north), cId);
        }

        /// <summary>The solved turn from <paramref name="from"/> to <paramref name="to"/> about the fitted axis, signed as an
        /// RA turn (positive = RA increasing, right-handed about the north pole). Pure, for the tests.</summary>
        internal static double HandStepRaTurnDeg((double RaDeg, double DecDeg, double ConeDeg) axis,
                PolarAlignSolveOutcome from, PolarAlignSolveOutcome to, bool north) {
            var turn = PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, from.RaDegJnow, from.DecDegJnow, to.RaDegJnow, to.DecDegJnow);
            return north ? turn : -turn;
        }

        // ── a clutch turn leaves the mount's reading wrong ───────────────────────────────────

        /// <summary>The mount session and sync count when a hand-turn run saw the axis turn with the clutch open; null when
        /// none since. Guarded by <c>_gate</c>.</summary>
        private (long SessionEpoch, long SyncEpoch)? _clutchTurned;

        /// <summary>The refusal's sentence (<c>polar-align-clutch-turned</c>).</summary>
        internal const string ClutchTurnedMessage =
            "RA was turned by hand with the clutch open, so the mount's reading no longer shows where it points and a motor turn could go too far — sync the mount with a plate solve (Center does it), or reconnect it once it is homed, or turn RA by hand again";

        /// <summary>A hand-turn run's solves moved a turn's worth while the mount's RA reading stood: the clutch is open, and
        /// the reading is now off by the user's turns. The session's sweep origin and the pose Polar Align left the mount at
        /// are forgotten (both are readings), and a motor start is refused until the mount is synced or reconnected
        /// (<see cref="ThrowIfClutchTurned"/>).</summary>
        private void NoteClutchTurned() {
            bool first;
            lock (_gate) {
                first = _clutchTurned is null;
                _clutchTurned = (_motionControl.SessionEpoch, _motionControl.SyncEpoch);
                _sweepOrigin = null;
                _sweepLastLeft = null;
            }
            if (first) {
                LogClutchTurned();
            }
        }

        /// <summary>A clutch turn stands: one was seen in this mount session, with no sync since.</summary>
        private bool ClutchTurnStands() {
            var session = _motionControl.SessionEpoch;
            var sync = _motionControl.SyncEpoch;
            lock (_gate) {
                return _clutchTurned is { } c && c.SessionEpoch == session && c.SyncEpoch == sync;
            }
        }

        /// <summary>Refuses (<c>polar-align-clutch-turned</c>) a start that turns RA with the motor while a clutch turn
        /// stands: none since in this mount session and with no sync since. A hand-turn start goes ahead.</summary>
        private void ThrowIfClutchTurned(PolarAlignStartConfirmation? confirmation) {
            if (confirmation is null || confirmation.HandTurn) {
                return;
            }
            var session = _motionControl.SessionEpoch;
            var sync = _motionControl.SyncEpoch;
            lock (_gate) {
                if (_clutchTurned is not { } c) {
                    return;
                }
                if (c.SessionEpoch != session || c.SyncEpoch != sync) {
                    _clutchTurned = null; // reconnected or synced since: the reading is the mount's word again
                    return;
                }
            }
            throw new RefusalException(RefusalCatalog.PolarAlignClutchTurned, ClutchTurnedMessage);
        }

        // ── the hand-turn stages ─────────────────────────────────────────────────────────────

        /// <summary>The coarse and fine measurements' seed by hand, then the shared live adjust loop
        /// (<see cref="AdjustFromSeedAsync"/>).</summary>
        private async Task RunSeededAdjustByHandAsync(
                LiveCapture capture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, LiveCapture? liveCapture, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            var (a, b, c) = await ThreePointsByHandAsync(capture, ["seed-a", "seed-b", "seed-c"], settings.SeedRotationDeg, band,
                renewLeaseIfDue, settings, gen, ct).ConfigureAwait(false);
            var (axis, turnDeg) = FitThreePointByHandOrFail(a, b, c, north);
            CheckFieldRotationByHand(axis, a.S, b.S, c.S, north, "seed-b", "seed-c");
            await AdjustFromSeedAsync(axis, c.S, c.At, turnDeg, liveCapture ?? capture, renewLeaseIfDue, site, gen, ct).ConfigureAwait(false);
        }

        /// <summary>The check by hand: three guide-camera solves around two hand turns of <c>CheckStepDeg</c>, the fit,
        /// and the motor check's verdict (<see cref="CheckResultFor"/>). There is no mount turn to compare with, so the
        /// turn mismatch is reported as 0.</summary>
        private async Task<PolarAlignCheckResultDto> RunCheckByHandAsync(
                IGuideCaptureLink guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, CancellationToken ct) {
            // StartAsync clears the frame size per run: a check reads it afresh, as the motor check does.
            _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            LiveCapture capture = (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token);
            var (p1, p2, p3) = await ThreePointsByHandAsync(capture, ["check-1", "check-2", "check-3"], settings.CheckStepDeg, band,
                () => RenewLeaseAsync(ct), settings, gen, ct, noteSolve: done => NoteCheckSolveAsync(gen, done)).ConfigureAwait(false);
            var (axis, _) = FitThreePointByHandOrFail(p1, p2, p3, site.LatitudeDeg > 0);
            CheckFieldRotationByHand(axis, p1.S, p2.S, p3.S, site.LatitudeDeg > 0, "check-2", "check-3");
            return CheckResultFor(axis, mismatch: 0.0, site, p3.At);
        }

        private Task PublishHandTurnAsync(int gen, PolarAlignHandTurnDto wait) {
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                _handTurnWait = wait.Waiting ? wait : null;
            }
            return PublishHandTurnEventAsync(wait);
        }

        // ADR 0014 rule 4 — the payload has a typed record: through the source-generated context.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "WS publish is best-effort; a broadcaster fault must not abort the routine. Log-and-recover boundary.")]
        private async Task PublishHandTurnEventAsync(PolarAlignHandTurnDto wait) {
            if (_ws is null) {
                return;
            }
            try {
                var payload = JsonSerializer.SerializeToElement(wait, AraJsonSerializerContext.Default.PolarAlignHandTurnDto);
                await _ws.PublishAsync(WsEventCatalog.PolarAlignHandTurn, payload, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogWsPublishFailed(ex);
            }
        }

        [LoggerMessage(EventId = 4600, Level = LogLevel.Information, Message = "§45 polar-align hand-turn run: the user turns RA by hand; the run sends the mount no motor move, GoTo or Find Home, drives no Polar Aligner and never turns tracking on while it runs")]
        private partial void LogHandTurnRun();

        [LoggerMessage(EventId = 4601, Level = LogLevel.Information, Message = "§45 polar-align measurement by hand: asking for two turns of about {StepDeg:F0}° toward the {Direction}, the way the user confirmed")]
        private partial void LogHandTurnMeasurement(string direction, double stepDeg);

        [LoggerMessage(EventId = 4602, Level = LogLevel.Information, Message = "§45 polar-align: waiting for the user to turn RA about {StepDeg:F0}° toward the {Direction} (point {Point} of 3)")]
        private partial void LogHandTurnAsked(int point, double stepDeg, string direction);

        [LoggerMessage(EventId = 4603, Level = LogLevel.Information, Message = "§45 polar-align: the user's turn for point {Point} is in, {TurnedDeg:F1}° by the mount's reading (clutch open: {ByClutch}, no angle then)")]
        private partial void LogHandTurnTaken(int point, double turnedDeg, bool byClutch);

        [LoggerMessage(EventId = 4604, Level = LogLevel.Warning, Message = "§45 polar-align: waiting for point {Point}, RA has turned at least {SweepDeg:F1}° from the first frame, past the measurement's {LimitDeg:F0}°; the run fails")]
        private partial void LogHandTurnTooFar(int point, double sweepDeg, double limitDeg);

        [LoggerMessage(EventId = 4605, Level = LogLevel.Warning, Message = "§45 polar-align: the solves turned while the mount's RA reading stood (the clutch is open); its reading is off by the user's turns, so the sweep origin is forgotten and a motor start is refused until the mount is synced or reconnected")]
        private partial void LogClutchTurned();

        [LoggerMessage(EventId = 4606, Level = LogLevel.Warning, Message = "§45 polar-align hand turn: the mount's RA/Dec read took over {TimeoutSeconds:F0} s; this frame is not measured")]
        private partial void LogHandTurnReadTimedOut(double timeoutSeconds);
    }
}
