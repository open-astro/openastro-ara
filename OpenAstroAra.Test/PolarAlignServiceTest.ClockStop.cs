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
    /// up to 2 s, so a move that stopped only on its reading overshot by its rate times that lag: the 0.3° Dec probe of
    /// Move to start position ran 1.9 s, Dec 88.00° → 89.01°, before a hand Stop. Each move also stops once the motor has
    /// been told its distance (the rates commanded times how long they ran). The fake mount's driver cache plays the lag:
    /// it answers every read with a sample up to 2 s old.
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
        public async Task Behind_a_2_s_reading_lag_the_move_to_starts_probe_stops_by_the_clock_at_0_3_degrees_and_the_move_arrives() {
            var mount = PolledGemNearThePole();
            var startOffPole = OffPoleDeg(mount);
            using var svc = NewRigLagService(mount, new WsRecorder());
            double? afterProbe = null;
            mount.OnFullRead = () => {
                // The second move's steady check reads the mount after the probe stood still, before the move starts.
                if (DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    afterProbe = OffPoleDeg(mount);
                }
            };

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(afterProbe, Is.Not.Null, mount.Describe());
                Assert.That(afterProbe!.Value - startOffPole, Is.EqualTo(PolarAlignService.MoveToStartProbeDeg).Within(ClockStopToleranceDeg),
                    "the probe stops at 0.3°, not 1° on a reading 2 s behind. " + mount.Describe());
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(OffPoleDeg(mount), Is.EqualTo(PolarAlignService.MoveToStartOffPoleDeg).Within(ClockStopToleranceDeg),
                    "4° from the pole, not past it by the lag's travel");
            });
            mount.AssertEndedStopped(tracking: true);
        }

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

        [Test]
        public async Task A_Dec_move_stopped_by_the_clock_that_settles_the_wrong_way_fails_as_turn_wrong_way() {
            // The probe learns the Dec motor's way; then the motor's sign flips, so the move off the pole goes toward it.
            // At the bench's speeds that 0.9° move ends by the clock before the 2 s-old reading moves: only the settled
            // reading shows it went the wrong way.
            var mount = PolledGemNearThePole(decDeg: 87.2);
            using var svc = NewBenchLagService(mount, new WsRecorder());
            var flipped = false;
            mount.OnFullRead = () => {
                if (!flipped && DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    flipped = true;
                    mount.DecMotorSign = -mount.DecMotorSign;
                }
            };

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(flipped, Is.True, mount.Describe());
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message);
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnWrongWay), result.Message + " " + mount.Describe());
                Assert.That(DecMoves(mount), Has.Length.EqualTo(2), "the probe and the one move: no top-up, never reversed. " + mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
            });
        }
    }
}
