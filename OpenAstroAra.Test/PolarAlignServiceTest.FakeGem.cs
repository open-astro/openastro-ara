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
    /// #2064 — the key Stop and exit scenarios run against <see cref="FakeGemMount"/>: the production leased-motor
    /// path (the fake is the daemon's telescope service), and asserts on the mount's TRUE pose from its ledger rather than
    /// on what it reported. A test that measures where a turn lands runs the mount on a clock the routine's own timed
    /// turns move (<see cref="PolledGemAt"/>); the Stop tests, which only need the turn cut short, run it in real time.
    /// Every scenario ends with the five common asserts of #2048 (<see cref="FakeGemMount.AssertCommon"/>).
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>#2048's default counterweight limit L.</summary>
        private const double CounterweightLimitDeg = 60.0;

        /// <summary>How far a timed turn's true travel may be from its step on the fake: the fake integrates its motors in
        /// 10 ms steps, and tracking turns the axis while the motor runs (under 0.03° in a 5 s turn).</summary>
        private const double TrueTurnToleranceDeg = 0.05;

        /// <summary>A plate solve of the frame: where the tube really points (the mount's readings play no part).</summary>
        private sealed class TrueSkySolver(FakeGemMount mount) : IPolarAlignFrameSolver {
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        /// <summary>A counterweight-down mount whose tube points at (<paramref name="haHours"/>, <paramref name="decDeg"/>):
        /// off the pole, as a camera that many degrees off the RA axis sees the sky from Home. It cannot find Home by
        /// itself, so a start asks nothing about Home.</summary>
        private static FakeGemMount GemAt(double haHours, double decDeg, double lstHours, double latitudeDeg = 45.0,
                bool tracking = true, TimeProvider? clock = null) {
            var mount = FakeGemMount.CounterweightDownAt(haHours, decDeg, lstHours, latitudeDeg, tracking, clock);
            mount.CanFindHome = false;
            return mount;
        }

        /// <summary>The routine on the fake, with NINA's 10° steps unless <paramref name="store"/> says otherwise. With
        /// <paramref name="benchMotors"/> the motor speed is 30°/s; without, the daemon's 3°/s, so a test on a real-time
        /// mount can land inside a turn.</summary>
        private static PolarAlignService NewGemService(FakeGemMount mount, WsRecorder ws, IProfileStore? store = null, bool benchMotors = true,
                IPolarAlignerSource? aligner = null) {
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(NewFake(), new TrueSkySolver(mount), mount, ws, store ?? TenDegreeStore(), aligner: aligner);
            if (benchMotors) {
                svc.AxisRateDegPerSec = 30.0;
            }
            return svc;
        }

        /// <summary>A counterweight-down mount at (<paramref name="haHours"/>, Dec 85°), LST 20 h, that tracks, on a
        /// <see cref="ManualMountClock"/> that the routine's timed turns move: a turn takes no real time, and where it
        /// lands does not depend on the test machine's load.</summary>
        private static FakeGemMount PolledGemAt(double haHours) =>
            GemAt(haHours, 85.0, lstHours: 20.0, clock: new ManualMountClock { AutoAdvance = true });

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

        // Start hour angle (h) and the profile's east_direction (null = east in the north) → the way the turns go, the true
        // φ at the start and after the seed's two 10° turns. Set away from the meridian, the turns go toward
        // counterweight-down on both sides: φ falls 20°.
        [TestCase(-4.0, null, true, 30.0, 10.0, PierSide.pierWest)]
        [TestCase(2.0, false, false, 60.0, 40.0, PierSide.pierEast)]
        public async Task A_fine_seed_on_the_GEM_turns_the_true_RA_axis_twice_by_its_step_and_never_raises_the_counterweight(
                double haHours, bool? eastDirection, bool east, double phiStartDeg, double phiEndDeg, PierSide side) {
            var mount = PolledGemAt(haHours);
            Assert.That(mount.TruePhiDeg, Is.EqualTo(phiStartDeg).Within(0.01), "the start pose (the mount tracks from here on)");
            Assert.That(mount.TrueSide, Is.EqualTo(side));
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { EastDirection = eastDirection });
            using var svc = NewGemService(mount, ws, store);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(2), mount.Describe());
            // East raises the RA reading: the true hour angle falls by the step; west, it grows.
            var sign = east ? -1.0 : 1.0;
            Assert.That(turns.Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(sign * 10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            Assert.That(turns.Select(t => t.End), Has.All.EqualTo(MotionEnd.Stopped), "each turn ended on its own stop");
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.DecMotor), Is.Empty, "the Dec axis never moves");
            Assert.That(turns[^1].TruePhiAfterDeg, Is.EqualTo(phiEndDeg).Within(2 * TrueTurnToleranceDeg));
            Assert.That(mount.MaxTruePhiDeg, Is.LessThanOrEqualTo(phiStartDeg + (2 * TrueTurnToleranceDeg)), "no turn raised the true counterweight angle");
            Assert.That(mount.TotalTrueRaSweepDeg, Is.EqualTo(20.0).Within(2 * TrueTurnToleranceDeg));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // ── one measurement per Start ────────────────────────────────────────────────────────

        private static readonly string[] OnlyTheFineStage = { PolarAlignStages.Fine };

        [Test]
        public async Task One_Start_on_the_GEM_makes_two_RA_turns_and_no_Dec_move() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
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

        // ── Stop ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Stop_mid_turn_on_the_GEM_aborts_the_true_turn_where_it_is_and_nothing_moves_after() {
            // The daemon's own motor speeds and the bench's 30° step: the first turn takes about 12 s.
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
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
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            mount.AbortIgnored = int.MaxValue;
            mount.MoveAxisStopIgnored = int.MaxValue;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
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
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            mount.MoveAxisRefused = true;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, HandBackStore());

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("slew_failed"));
            Assert.That(mount.MotorTravelDeg, Is.Zero);
            Assert.That(mount.Motions, Has.All.Matches<MotionEntry>(m => m!.Refused), mount.Describe());
            mount.AssertEndedStopped(tracking: false); // a failure never hands tracking back, whatever the setting
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

    }
}
