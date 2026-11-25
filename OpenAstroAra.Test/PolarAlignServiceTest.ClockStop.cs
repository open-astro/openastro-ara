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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2166 rig check (HAE29C on AlpacaBridge 6.0.0-beta.4, 2026-11-22) — the mount's position reads trailed its motor by
    /// up to 2 s, so a move that stopped only on its reading overshot by its rate times that lag. Each move also stops
    /// once the motor has been told its distance (the rates commanded times how long they ran). The fake mount's driver
    /// cache plays the lag: it answers every read with a sample up to 2 s old.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>How far the rig's readings trailed the motor.</summary>
        private static readonly TimeSpan RigReadingLag = TimeSpan.FromSeconds(2);

        /// <summary>The HAE29C's matched rates (2.13°/s and 0.53°/s) and the daemon's 2 s stall, on a polled fake.</summary>
        private static PolarAlignService NewRigLagService(FakeGemMount mount, WsRecorder ws) {
            mount.DriverCache = RigReadingLag;
            var svc = NewGemService(mount, ws, benchMotors: false);
            svc.AxisFastRateDegPerSec = 2.13;
            svc.AxisSlowRateDegPerSec = 0.53;
            svc.AxisMoveStall = TimeSpan.FromSeconds(2);
            svc.AxisSettle = TimeSpan.FromSeconds(2.5); // the daemon's: longer than the lag, so the settle reads a fresh sample
            return svc;
        }

        /// <summary>A move stopped by the clock lands within one fresh read's travel of its distance: at most 10 ms at
        /// 2.13°/s (0.02°), plus the sky's turn while it moves (under 0.05° in 7 s).</summary>
        private const double ClockStopToleranceDeg = 0.1;

        [Test]
        public async Task Behind_a_2_s_reading_lag_each_10_degree_RA_turn_stops_by_the_clock_at_its_step_and_the_run_reaches_adjusting() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewRigLagService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turns = RaTurns(mount);
            Assert.Multiple(() => {
                Assert.That(turns.Select(t => Math.Abs(t.TrueDeltaHaDeg)), Has.All.LessThanOrEqualTo(10.0 + ClockStopToleranceDeg),
                    "no turn runs past its step by the lag's travel. " + mount.Describe());
                Assert.That(Math.Abs(turns.Sum(t => t.TrueDeltaHaDeg)), Is.EqualTo(20.0).Within(2 * ClockStopToleranceDeg),
                    "two 10° turns, each where it was told. " + mount.Describe());
            });
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }
        /// <summary>Bench speeds (30°/s and 10°/s) behind the 2 s lag: every move ends by the clock before the reading
        /// shows any of it, so only the settled reading can see which way it went.</summary>
        private static PolarAlignService NewBenchLagService(FakeGemMount mount, WsRecorder ws) {
            mount.DriverCache = RigReadingLag;
            var svc = NewGemService(mount, ws);
            svc.AxisMoveStall = TimeSpan.FromSeconds(2);
            svc.AxisSettle = TimeSpan.FromSeconds(2.5);
            return svc;
        }

        [Test]
        public async Task An_RA_turn_stopped_by_the_clock_that_settles_the_wrong_way_fails_as_turn_wrong_way() {
            // The RA motor turns the other way from the saved sign, and the 10° turn (0.5 s at the bench's speeds) ends by
            // the clock before the 2 s-old reading moves: the settled reading is the first to show it, 10° the wrong way.
            var mount = PolledGemAt(-4.0);
            mount.RaMotorSign = -1.0;
            var ws = new WsRecorder();
            using var svc = NewBenchLagService(mount, ws);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.TurnWrongWay), message + " " + mount.Describe());
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "never reversed, no top-up. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
        }

        // ── #2189 rig check: reads that block while the motor runs ──────────────────────────────

        /// <summary>The rig's readings on a fake in real time: a driver cache and a blocking read together up to 2 s old (the
        /// HAE29C's reads took 0.6 s and more right after a motor command), at its matched speeds and the daemon's 2 s stall.</summary>
        private static PolarAlignService NewBlockingReadService(FakeGemMount mount, WsRecorder ws) {
            mount.DriverCache = TimeSpan.FromSeconds(1.4);
            mount.SlowReads = TimeSpan.FromSeconds(0.6);
            var svc = NewGemService(mount, ws, benchMotors: false);
            svc.AxisFastRateDegPerSec = 2.13;
            svc.AxisSlowRateDegPerSec = 0.53;
            svc.AxisMoveStall = TimeSpan.FromSeconds(2);
            svc.AxisMovePoll = TimeSpan.FromMilliseconds(250);
            svc.MotorRampAllowance = TimeSpan.FromSeconds(0.5); // the daemon's
            // The daemon's stop confirmation: reads of 0.6 s need its 8 s window, not the bench's 0.5 s.
            svc.StopVerifyPoll = TimeSpan.FromMilliseconds(250);
            svc.StopVerifyStillFor = TimeSpan.FromSeconds(1.5);
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(2);
            svc.StopResendAfter = TimeSpan.FromSeconds(2);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(8);
            return svc;
        }

        /// <summary>A real-time move stops within this of its distance: the clock's stop lands on its timer (tens of ms at
        /// 0.53°/s is under 0.05°), whatever a read in flight is doing.</summary>
        private const double BlockingReadToleranceDeg = 0.1;

        [Test]
        public async Task Behind_blocking_reads_each_10_degree_RA_turn_stops_by_the_clock_at_its_step() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = NewBlockingReadService(mount, ws);

            // In real time two turns behind 0.6 s reads take longer than the bench's event wait: wait for adjusting here.
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            await ws.WhenAsync(events => events.Any(e => e.Type == WsEventCatalog.PolarAlignProgress || e.Type == WsEventCatalog.PolarAlignError))
                .WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            var state = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(state.State, Is.EqualTo("adjusting"), mount.Describe());
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turns = RaTurns(mount);
            Assert.Multiple(() => {
                // Each turn is a fast phase stopped by the clock at the slow zone and a slow top-up; the sky turns ~0.04° west
                // under a 9 s turn, which the motor's travel does not count.
                Assert.That(mount.TotalTrueRaSweepDeg, Is.EqualTo(20.0).Within(2 * (BlockingReadToleranceDeg + 0.05)),
                    "two 10° turns, each where it was told. " + mount.Describe());
                Assert.That(turns.Select(t => Math.Abs(t.TrueDeltaHaDeg)), Has.All.LessThanOrEqualTo(10.0 + BlockingReadToleranceDeg),
                    mount.Describe());
            });
            mount.AssertEndedStopped(tracking: false);
        }
    }
}
