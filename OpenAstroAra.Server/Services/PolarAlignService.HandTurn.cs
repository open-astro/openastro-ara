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
    /// mount no motor move, no GoTo and no Find Home, never tilts the tube off the axis, never drives a Polar Aligner's
    /// motors (the person turns its bolts, as with auto off) and never turns tracking on while it runs: the only mount
    /// writes it makes are tracking off for the live adjust loop (§45.8 needs the camera still in alt/az) and the exit's,
    /// as every run's — the stop, and tracking off or the tracking it found given back (<c>stop_tracking_when_done</c>
    /// off and the run finished, or Guiding taking the camera). The rest of the routine — the three-point fit, the live
    /// loop, the check's verdict — is the motor run's own.
    /// <para>A turn is taken once two solves in a row show the RA axis at least <see cref="HandTurnTakenFraction"/> of the
    /// step past the last point, the way the user confirmed, and within <see cref="HandTurnStillDeg"/> of each other (the
    /// mount has stopped). Frames that do not solve while the user turns (trailed stars) are expected and only retried.</para>
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The share of the step the solves must show before a hand turn counts: a person turns about 10°,
        /// not exactly.</summary>
        internal const double HandTurnTakenFraction = 0.8;

        /// <summary>Two solves this close (RA turn and Dec, degrees) mean the user has stopped turning.</summary>
        internal const double HandTurnStillDeg = 0.1;

        // Guarded by _gate: whether the current run turns by hand (for the status), and what it waits for now.
        private bool _handTurn;
        private PolarAlignHandTurnDto? _handTurnWait;

        // The RA axis's turn from the run's start pose by the user's hand, measured from the solves, positive away from
        // the meridian (the way the user confirmed). Guarded by _gate; StartAsync resets it.
        private double _handOffsetDeg;

        /// <summary>How far the RA axis turned between two solves, in RA-reading terms (+ east), by the user's hand: the
        /// solved RA's change, less the Earth's turn when the mount does not track (tracking holds the solve still while
        /// the axis stands, so with tracking on the change is the turn itself). Pure, for the tests.</summary>
        internal static double HandTurnDeg(double fromRaDeg, double toRaDeg, double elapsedSeconds, bool tracking) =>
            Wrap180(toRaDeg - fromRaDeg - (tracking ? 0.0 : elapsedSeconds * SiderealDegPerSecond));

        /// <summary>The way this stage asks the user to turn (RA-reading sign: +1 east), inside the band the user
        /// confirmed, from the turns the solves measured: the motor run's rule (<see cref="PlanStageTurns"/>). Where the
        /// band wants a turn back to the start first, the user is asked for it by hand before the three points
        /// (<see cref="ThreePointsByHandAsync"/>), as the motor run turns back first: turning back over the arc instead
        /// would end past the start, toward the meridian.</summary>
        /// <returns>The sign of the stage's two turns, and how far to turn back toward the start first (degrees, 0 for
        /// none).</returns>
        private (double DirSign, double ReturnDeg) HandTurnDirection(RunBand band, double travelDeg) {
            double offsetDeg;
            lock (_gate) {
                offsetDeg = _handOffsetDeg;
            }
            var (dirSign, returnFirst) = PlanStageTurns(band.AwaySign, offsetDeg, travelDeg, band.MaxSweepDeg);
            var stage = CurrentStage() ?? "?";
            var direction = dirSign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West;
            var returnDeg = Math.Abs(returnFirst);
            LogHandTurnStage(stage, offsetDeg, direction, returnDeg);
            return (dirSign, returnDeg);
        }

        /// <summary>How far a hand turn back to the start must get before it counts: within <see cref="BandToleranceDeg"/>
        /// of the start, and at least the usual share of the turn.</summary>
        internal static double HandReturnTakenDeg(double returnDeg) =>
            Math.Max(returnDeg - BandToleranceDeg, HandTurnTakenFraction * returnDeg);

        /// <summary>Adds a hand turn the solves measured to the axis's offset from the run's start (positive away from
        /// the meridian). <paramref name="dirSign"/> is the RA-reading way it was asked.</summary>
        private void AddHandTurn(RunBand band, double dirSign, double turnedDeg, int gen) {
            lock (_gate) {
                if (_generation == gen) {
                    _handOffsetDeg += band.AwaySign * dirSign * turnedDeg;
                }
            }
        }

        /// <summary>The three points of a hand-turn stage: a solve where the mount points, then two solves after the user
        /// turned RA by about <paramref name="stepDeg"/> each, the way <see cref="HandTurnDirection"/> picks. No mount
        /// command at all: the mount's reading is only read, as the first frame's solve hint.</summary>
        /// <param name="names">The three frames' ids.</param>
        /// <param name="noteSolve">Called with 1, 2, 3 as each point is taken (the check's progress).</param>
        private async Task<((PolarAlignSolveOutcome S, DateTimeOffset At) A, (PolarAlignSolveOutcome S, DateTimeOffset At) B,
                (PolarAlignSolveOutcome S, DateTimeOffset At) C)> ThreePointsByHandAsync(
                LiveCapture capture, string[] names, double stepDeg, RunBand band, Func<Task> renew,
                PolarAlignSettingsDto settings, int gen, CancellationToken ct, Func<int, Task>? noteSolve = null) {
            var hint = await FreshMountPointingJnowAsync(ct).ConfigureAwait(false);
            var a = await SolveSeedFrameAsync(capture, names[0], hint, settings, gen, ct).ConfigureAwait(false);
            var aAt = DateTimeOffset.UtcNow;
            LogSeedSolve(names[0], a.RaDegJnow, a.DecDegJnow);
            // A camera along the axis gives the fit no circle, and a hand run never tilts the tube itself.
            if (Math.Abs(a.DecDegJnow) > OnAxisDecDeg) {
                throw new RoutineFailedException(HandTurnOnAxis,
                    $"the camera looks along the RA axis (Dec {a.DecDegJnow:F1}°) — turn the Dec axis by hand so the tube points about 5° from the pole, then start again");
            }
            var (dirSign, returnDeg) = HandTurnDirection(band, 2.0 * stepDeg);
            if (returnDeg > 0.0) {
                // Back toward the start first (point 1 again): its last solve is the stage's first point.
                var back = await WaitForHandTurnAsync(capture, names[0] + "-back", (a, aAt), point: 1, -band.AwaySign, returnDeg,
                    HandReturnTakenDeg(returnDeg), renew, settings, gen, ct).ConfigureAwait(false);
                AddHandTurn(band, -band.AwaySign, back.TurnedDeg, gen);
                (a, aAt) = (back.S, back.At);
            }
            if (noteSolve is not null) {
                await noteSolve(1).ConfigureAwait(false);
            }
            var takenDeg = HandTurnTakenFraction * stepDeg;
            var b = await WaitForHandTurnAsync(capture, names[1], (a, aAt), point: 2, dirSign, stepDeg, takenDeg, renew, settings, gen, ct).ConfigureAwait(false);
            if (noteSolve is not null) {
                await noteSolve(2).ConfigureAwait(false);
            }
            var c = await WaitForHandTurnAsync(capture, names[2], (b.S, b.At), point: 3, dirSign, stepDeg, takenDeg, renew, settings, gen, ct).ConfigureAwait(false);
            AddHandTurn(band, dirSign, b.TurnedDeg + c.TurnedDeg, gen);
            if (noteSolve is not null) {
                await noteSolve(3).ConfigureAwait(false);
            }
            return ((a, aAt), (b.S, b.At), (c.S, c.At));
        }

        /// <summary>Ask the user to turn RA by about <paramref name="stepDeg"/> toward <paramref name="dirSign"/>, then
        /// capture and solve until the turn is in (<paramref name="takenDeg"/> or more, two still solves). Publishes
        /// <c>polar_align.hand_turn</c> after every solve, so the screen shows how far the axis has turned. Waits as long as
        /// the user needs: Stop ends it.</summary>
        /// <returns>The taken solve, when, and how far it shows the axis turned the asked way (degrees).</returns>
        private async Task<(PolarAlignSolveOutcome S, DateTimeOffset At, double TurnedDeg)> WaitForHandTurnAsync(
                LiveCapture capture, string frameId, (PolarAlignSolveOutcome S, DateTimeOffset At) from, int point, double dirSign,
                double stepDeg, double takenDeg, Func<Task> renew, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var direction = dirSign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West;
            LogHandTurnAsked(point, stepDeg, direction);
            await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, TurnedDeg: null)).ConfigureAwait(false);
            (double RaDeg, double DecDeg)? hint = (from.S.RaDegJnow, from.S.DecDegJnow);
            (double Turned, double DecDeg)? last = null;
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
                    if (s.Success) {
                        var at = DateTimeOffset.UtcNow;
                        hint = (s.RaDegJnow, s.DecDegJnow);
                        var turned = dirSign * HandTurnDeg(from.S.RaDegJnow, s.RaDegJnow, (at - from.At).TotalSeconds,
                            _mount.GetInfo().TrackingEnabled);
                        var still = last is { } l
                            && Math.Abs(turned - l.Turned) <= HandTurnStillDeg
                            && Math.Abs(s.DecDegJnow - l.DecDeg) <= HandTurnStillDeg;
                        if (turned >= takenDeg && still) {
                            LogSeedSolve(frameId, s.RaDegJnow, s.DecDegJnow);
                            LogHandTurnTaken(point, turned);
                            await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, Math.Round(turned, 1), Waiting: false)).ConfigureAwait(false);
                            return (s, at, turned);
                        }
                        last = (turned, s.DecDegJnow);
                        await PublishHandTurnAsync(gen, new PolarAlignHandTurnDto(point, direction, stepDeg, Math.Round(turned, 1))).ConfigureAwait(false);
                    } else {
                        last = null; // a trailed frame mid-turn: the next two solves decide
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

        /// <summary>The cause of a hand-turn run that started with the camera along the RA axis.</summary>
        internal const string HandTurnOnAxis = "hand_turn_on_axis";

        // ── the hand-turn stages ─────────────────────────────────────────────────────────────

        /// <summary>The coarse and fine stages' seed by hand, then the shared live adjust loop
        /// (<see cref="AdjustFromSeedAsync"/>).</summary>
        private async Task RunSeededAdjustByHandAsync(
                LiveCapture capture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, LiveCapture? liveCapture, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            var (a, b, c) = await ThreePointsByHandAsync(capture, ["seed-a", "seed-b", "seed-c"], settings.SeedRotationDeg, band,
                renewLeaseIfDue, settings, gen, ct).ConfigureAwait(false);
            var (axis, turnDeg) = FitThreePointByHandOrFail(a, b, c, north);
            await AdjustFromSeedAsync(axis, c.S, c.At, turnDeg, liveCapture ?? capture, renewLeaseIfDue, site, gen, ct).ConfigureAwait(false);
        }

        /// <summary>The check by hand: three guide-camera solves around two hand turns of <c>CheckStepDeg</c>, the fit,
        /// and the motor check's verdict (<see cref="CheckResultFor"/>). There is no mount turn to compare with, so the
        /// turn mismatch is reported as 0.</summary>
        private async Task<PolarAlignCheckResultDto> RunCheckByHandAsync(
                IGuideCaptureLink guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, CancellationToken ct) {
            if (_runFrameSize is null) {
                _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            }
            LiveCapture capture = (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token);
            var (p1, p2, p3) = await ThreePointsByHandAsync(capture, ["check-1", "check-2", "check-3"], settings.CheckStepDeg, band,
                () => RenewLeaseAsync(ct), settings, gen, ct, noteSolve: done => NoteCheckSolveAsync(gen, done)).ConfigureAwait(false);
            var (axis, _) = FitThreePointByHandOrFail(p1, p2, p3, site.LatitudeDeg > 0);
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

        [LoggerMessage(EventId = 4590, Level = LogLevel.Information, Message = "§45 polar-align hand-turn run: the user turns RA by hand; the run sends the mount no motor move, GoTo or Find Home, drives no Polar Aligner and never turns tracking on while it runs")]
        private partial void LogHandTurnRun();

        [LoggerMessage(EventId = 4591, Level = LogLevel.Information, Message = "§45 polar-align {Stage} stage by hand: the RA axis is {OffsetDeg:F1}° from the run's start by the solves; asking for turns toward the {Direction} (turn back to the start first {ReturnDeg:F1}°)")]
        private partial void LogHandTurnStage(string stage, double offsetDeg, string direction, double returnDeg);

        [LoggerMessage(EventId = 4592, Level = LogLevel.Information, Message = "§45 polar-align: waiting for the user to turn RA about {StepDeg:F0}° toward the {Direction} (point {Point} of 3; point 1 is a turn back to the start)")]
        private partial void LogHandTurnAsked(int point, double stepDeg, string direction);

        [LoggerMessage(EventId = 4593, Level = LogLevel.Information, Message = "§45 polar-align: the user's turn for point {Point} is in, {TurnedDeg:F1}° by the solves")]
        private partial void LogHandTurnTaken(int point, double turnedDeg);
    }
}
