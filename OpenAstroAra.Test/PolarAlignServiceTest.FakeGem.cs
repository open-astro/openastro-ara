#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2064 — the key Stop, exit and sweep scenarios run against <see cref="FakeGemMount"/>: the production leased-motor
    /// path (the fake is the daemon's telescope service), and asserts on the mount's TRUE pose from its ledger rather than
    /// on what it reported. A test that measures where a turn lands runs the mount on the routine's own reads
    /// (<see cref="PolledGemAt"/>); the Stop tests, which only need the turn cut short, run it in real time. Every
    /// scenario ends with the five common asserts of #2048 (<see cref="FakeGemMount.AssertCommon"/>).
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>#2048's default counterweight limit L.</summary>
        private const double CounterweightLimitDeg = 60.0;

        /// <summary>How long each of the routine's fresh reads takes on a polled mount's clock (<see cref="PolledGemAt"/>).</summary>
        private static readonly TimeSpan FreshRead = TimeSpan.FromMilliseconds(10);

        /// <summary>The RA axis's true turn past its step a bench move may make on a polled mount: the stop lands on the
        /// first reading past the step, so at most one read's travel at the bench's slow 10°/s (0.1°), plus the tracking
        /// of the reads while it turns (under 0.01°). Exact whatever the test machine's load: the mount moves only when
        /// the routine reads it.</summary>
        private const double TrueTurnToleranceDeg = (10.0 * 0.010) + 0.01;

        /// <summary>A plate solve of the frame: where the tube really points (the mount's readings play no part).</summary>
        private sealed class TrueSkySolver(FakeGemMount mount) : IPolarAlignFrameSolver {
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        /// <summary>The routine on the fake, with NINA's 10° steps unless <paramref name="store"/> says otherwise. With
        /// <paramref name="benchMotors"/> the motor speeds are 30°/s and 10°/s, so a run takes seconds; without, the
        /// daemon's 3°/s and 1°/s, so a test can land inside a turn.</summary>
        private static PolarAlignService NewGemService(FakeGemMount mount, WsRecorder ws, IProfileStore? store = null, bool benchMotors = true,
                IPolarAlignerSource? aligner = null) {
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(NewFake(), new TrueSkySolver(mount), mount, ws, store ?? TenDegreeStore(), aligner: aligner);
            if (benchMotors) {
                svc.AxisFastRateDegPerSec = 30.0;
                svc.AxisSlowRateDegPerSec = 10.0;
            }
            return svc;
        }

        /// <summary>A counterweight-down mount at (<paramref name="haHours"/>, Dec 85°), LST 20 h, that tracks, on a
        /// <see cref="ManualMountClock"/> that only the routine's own fresh reads move (<see cref="FreshRead"/> each): it
        /// turns a fixed amount per poll, so where a bench turn lands does not depend on how late a poll ran.</summary>
        private static FakeGemMount PolledGemAt(double haHours) {
            var mount = FakeGemMount.CounterweightDownAt(haHours, 85.0, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            return mount;
        }

        /// <summary>The index of the last <paramref name="type"/> event: the run's end event for the common asserts.</summary>
        private static int LastEventIndex(WsRecorder ws, string type) {
            var events = ws.Events.ToArray();
            var at = Array.FindLastIndex(events, e => e.Type == type);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "no " + type + " event");
            return at;
        }

        private static MotionEntry[] RaTurns(FakeGemMount mount) =>
            mount.Motions.Where(m => m.Kind == MotionKind.RaMotor && !m.Refused).ToArray();

        // ── a fine run, from either side of the meridian ─────────────────────────────────────

        // Start hour angle (h) → the direction the user confirms, the true φ at the start and after the seed's two 10° turns.
        // Away from the meridian is toward counterweight-down on both sides: φ falls 20°.
        [TestCase(-4.0, PolarAlignDirections.East, 30.0, 10.0, PierSide.pierWest)]
        [TestCase(2.0, PolarAlignDirections.West, 60.0, 40.0, PierSide.pierEast)]
        public async Task A_fine_seed_on_the_GEM_turns_the_true_RA_axis_twice_by_its_step_and_never_raises_the_counterweight(
                double haHours, string direction, double phiStartDeg, double phiEndDeg, PierSide side) {
            var mount = PolledGemAt(haHours);
            Assert.That(mount.TruePhiDeg, Is.EqualTo(phiStartDeg).Within(0.01), "the start pose (the mount tracks from here on)");
            Assert.That(mount.TrueSide, Is.EqualTo(side));
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            var plan = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plan.Direction, Is.EqualTo(direction));
            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(2), mount.Describe());
            // East raises the RA reading: the true hour angle falls by the step; west, it grows.
            var sign = direction == PolarAlignDirections.East ? -1.0 : 1.0;
            Assert.That(turns.Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(sign * 10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            Assert.That(turns.Select(t => t.End), Has.All.EqualTo(MotionEnd.Stopped), "each turn ended on its own stop");
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.DecMotor), Is.Empty, "Dec 85° is off the axis: no tilt");
            Assert.That(turns[^1].TruePhiAfterDeg, Is.EqualTo(phiEndDeg).Within(2 * TrueTurnToleranceDeg));
            Assert.That(mount.MaxTruePhiDeg, Is.LessThanOrEqualTo(phiStartDeg + 0.05), "no turn raised the true counterweight angle");
            Assert.That(mount.TotalTrueRaSweepDeg, Is.EqualTo(20.0).Within(2 * TrueTurnToleranceDeg));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // ── one measurement per Start (#2082) ────────────────────────────────────────────────

        private static readonly string[] OnlyTheFineStage = { PolarAlignStages.Fine };

        [Test]
        public async Task One_Start_on_the_GEM_makes_two_RA_turns_and_no_Dec_move_and_Next_starts_nothing() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            // Next used to lead the run into the check: two more turns. It is retired: 409, and the loop runs on.
            var next = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NextAsync(null, CancellationToken.None));
            Assert.That(next!.Message, Is.EqualTo(PolarAlignService.NextRetiredMessage));
            var mark = ws.Events.Count;
            await ws.WhenAsync(events => events.Skip(mark).Count(e => e.Type == WsEventCatalog.PolarAlignProgress) >= 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), "at most two primary-axis moves. " + mount.Describe());
            Assert.That(RaTurns(mount).Select(t => Math.Abs(t.TrueDeltaHaDeg)), Has.All.EqualTo(10.0).Within(TrueTurnToleranceDeg), "each of SeedRotationDeg");
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.DecMotor), Is.Empty, "no secondary-axis move");
            Assert.That(ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignStage).Select(e => e.Payload.GetProperty("stage").GetString()),
                Is.EqualTo(OnlyTheFineStage), "one measurement, never a second stage");
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_start_at_Dec_88_on_the_GEM_is_refused_before_any_motion() {
            // Within 3° of the pole the camera looks along the RA axis. Polar Align used to tilt it 5° off with the Dec motor;
            // it now asks the user to point 2–5° from the pole instead, before anything moves.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 88.0, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            var fake = NewFake();
            using var svc = NewServiceOn(fake, new TrueSkySolver(mount), mount, ws, NewStore());

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            var startRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                null, new PolarAlignStartConfirmation(PolarAlignDirections.East, NewOrigin: true), null, CancellationToken.None));

            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignOnAxis), "the confirm step already says so");
            Assert.That(startRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignOnAxis));
            Assert.That(startRefusal.Message, Does.Contain("point it 2–5° from the pole"));
            Assert.That(mount.Motions, Is.Empty, "zero motions. " + mount.Describe());
            Assert.That(mount.MotorTravelDeg, Is.Zero);
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
        }

        // ── Stop ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Stop_mid_turn_on_the_GEM_aborts_the_true_turn_where_it_is_and_nothing_moves_after() {
            // The daemon's own motor speeds and the bench's 30° step: the first turn takes about 12 s.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turn = RaTurns(mount).Single();
            Assert.That(turn.End, Is.EqualTo(MotionEnd.Aborted), "the Stop's AbortSlew ended the turn. " + mount.Describe());
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.LessThan(5.0), "the 30° turn was cut short where the Stop caught it");
            var abort = mount.Stops.First(s => s.Kind == StopKind.Abort && s.At >= at);
            Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)));
            mount.AssertNoMotionSince(abort.At);
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_stop_the_GEM_ignores_is_reported_mount_did_not_stop_while_its_true_RA_axis_runs_on() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.AbortIgnored = int.MaxValue;
            mount.MoveAxisStopIgnored = int.MaxValue;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero);
            var stops = mount.Stops.Where(s => s.At >= at).ToArray();
            Assert.That(stops.Count(s => s.Kind == StopKind.Abort), Is.GreaterThanOrEqualTo(1 + PolarAlignService.StopSends));
            Assert.That(stops.Where(s => s.Kind == StopKind.Abort || s.Axis == 0).Select(s => s.Honoured), Has.All.False);
            Assert.That(mount.IsMoving, Is.True, "the ledger sees what the reported 'not confirmed' means: the RA axis still turns");
            Assert.That(mount.MotorTravelSince(at), Is.GreaterThan(0.1));
        }

        // ── failures ─────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_refused_motor_move_on_the_GEM_fails_the_run_with_nothing_moved_and_tracking_off() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.MoveAxisRefused = true;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, HandBackStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("slew_failed"));
            Assert.That(mount.MotorTravelDeg, Is.Zero);
            Assert.That(mount.Motions, Has.All.Matches<MotionEntry>(m => m!.Refused), mount.Describe());
            mount.AssertEndedStopped(tracking: false); // a failure never hands tracking back, whatever the setting
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── the sweep origin across runs ─────────────────────────────────────────────────────

        [Test]
        public async Task Repeated_starts_on_the_GEM_keep_the_true_RA_axis_within_20_degrees_of_the_origin() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            var directions = new List<string>();
            for (var run = 0; run < 5; run++) {
                directions.Add((await FineRunToAdjustingAsync(svc).ConfigureAwait(false)).Direction);
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            Assert.That(directions, Is.EqualTo(new[] {
                PolarAlignDirections.East, PolarAlignDirections.West, PolarAlignDirections.East, PolarAlignDirections.West, PolarAlignDirections.East,
            }));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(10), mount.Describe());
            // The true RA axis swings between the origin and 20° east of it: never toward the walk to the pier.
            var excursion = mount.MaxTrueRaExcursionDeg(TimeSpan.Zero);
            Assert.That(excursion, Is.EqualTo(20.0).Within(2 * TrueTurnToleranceDeg), mount.Describe());
            Assert.That(excursion, Is.LessThan(PolarAlignService.MaxSweepFromOriginDeg));
            Assert.That(mount.MaxTruePhiDeg, Is.LessThanOrEqualTo(30.0 + TrueTurnToleranceDeg),
                "every turn stayed between φ 30° and φ 10°, give or take a turn back to the origin landing a little past it");
            Assert.That(mount.TotalTrueRaSweepDeg, Is.EqualTo(100.0).Within(10 * TrueTurnToleranceDeg));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }
    }
}
