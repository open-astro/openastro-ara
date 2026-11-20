#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2067 (ARA-11 to ARA-13), #2081 items 3 and 5, #2083 — every RA turn Polar Align makes is guarded, on the fake GEM
    /// mount: a stall, a time bound of twice the planned travel at the rates the mount really runs, a Dec reading that
    /// moves, a pier side that changes, a reading that jumps, a mount that runs on past its step after its stop, and a turn
    /// that does not arrive each stop the motor and fail the run. Frames whose field turns by 180° are a pier flip. The
    /// mount runs on the routine's own reads (<see cref="PolledGemAt"/>), so where a turn is cut short does not depend on
    /// the test machine's load.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A fine run on <paramref name="mount"/> (fake clock, 10° steps) at the daemon's 2 s stall; waits for its
        /// error event and returns its reason and message.</summary>
        private static async Task<(string Reason, string Message)> GuardedRunFailureAsync(
                FakeGemMount mount, WsRecorder ws, PolarAlignService svc) {
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            return (error.GetProperty("reason").GetString()!, error.GetProperty("message").GetString()!);
        }

        /// <summary>The routine on a polled mount at the daemon's guards: a 2 s stall (on the fake's clock).</summary>
        private static PolarAlignService NewGuardedService(FakeGemMount mount, WsRecorder ws, bool benchMotors = true, IPolarAlignFrameSolver? solver = null) {
            var svc = solver is null ? NewGemService(mount, ws, benchMotors: benchMotors) : NewGemServiceWith(mount, ws, solver);
            svc.AxisMoveStall = TimeSpan.FromSeconds(2);
            return svc;
        }

        private static string? LastCommand(FakeGemMount mount) {
            var commands = mount.Commands;
            return commands.Count == 0 ? null : commands[^1].Text;
        }

        /// <summary>The mount's commanded non-zero RA motor rates, in order.</summary>
        private static double[] RaMotorRates(FakeGemMount mount) =>
            mount.Commands.Where(c => c.Text.StartsWith("move primary ", StringComparison.Ordinal))
                .Select(c => double.Parse(c.Text["move primary ".Length..], CultureInfo.InvariantCulture))
                .Where(r => r != 0).ToArray();

        // ── a stall and the time bound (#2081 item 3) ────────────────────────────────────────

        [Test]
        public void The_daemons_turn_bound_is_twice_the_planned_time_at_the_matched_rates_and_its_stall_2_s() {
            // NINA's MoveTimeoutFactor 2: a 10° turn at 3°/s then 1°/s for its last 3° plans 5.33 s, so 12.7 s with the 2 s
            // a reading may trail the mount (it was 45 s); the stall is one poll more than iOptron's 1 s cache (was 10 s).
            var mount = PolledGemAt(-4.0);
            using var daemon = new PolarAlignService(NewFake(), NullLogger<PolarAlignService>.Instance, new TrueSkySolver(mount), mount,
                MediatorMotionControl.For(mount), NewStore());

            Assert.That(daemon.AxisMoveStall, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(daemon.TurnTimeBound(10.0, 3.0, 1.0).TotalSeconds, Is.EqualTo((2.0 * ((7.0 / 3.0) + 3.0)) + 2.0).Within(1e-6));
            Assert.That(daemon.TurnTimeBound(10.0, 2.13, 0.53).TotalSeconds, Is.EqualTo((2.0 * ((7.0 / 2.13) + (3.0 / 0.53))) + 2.0).Within(1e-6),
                "planned on the rates the mount runs, not the ones asked for");
        }

        [Test]
        public async Task A_frozen_reading_stops_the_turn_within_the_2_s_stall_as_slew_stalled() {
            // The motor turns but the reading never moves (a driver whose position froze): the turn stops 2 s after its
            // last progress, at the daemon's 3°/s about 6° of true travel, never the 21° a 10 s stall cost.
            var mount = PolledGemAt(-4.0);
            mount.ReadingFrozen = true;
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws, benchMotors: false);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.SlewStalled), message);
            var turn = RaTurns(mount).Single();
            // 2 s of no progress plus the reads that measured it (10 ms each) at the fast 3°/s.
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.LessThanOrEqualTo(3.0 * (2.0 + 0.05)), mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task An_RA_reading_that_freezes_mid_turn_fails_as_slew_stalled_and_the_motor_stops() {
            var mount = PolledGemAt(-4.0);
            var polls = 0;
            mount.OnPoseRead = () => {
                if (Interlocked.Increment(ref polls) == 5) {
                    mount.ReadingFrozen = true; // ra_freeze: the mount turns on, its reading stands
                }
            };
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, _) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.SlewStalled));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_mount_slower_than_its_rate_fails_at_twice_the_planned_time_as_slew_timeout() {
            // The routine asks 30°/s and 10°/s (the bench) and the mount takes them, but turns at 1°/s: never a stall (it
            // progresses on every few reads), but the 10° turn would take 10 s against a bound of 2 × 0.53 s + 2 s.
            var mount = PolledGemAt(-4.0);
            mount.MaxAxisRateDegPerSec = 1.0;
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);
            var bound = svc.TurnTimeBound(10.0, 30.0, 10.0);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(bound.TotalSeconds, Is.EqualTo((2.0 * ((7.0 / 30.0) + (3.0 / 10.0))) + 2.0).Within(1e-6));
            Assert.That(reason, Is.EqualTo(PolarAlignService.SlewTimeout), message);
            var turn = RaTurns(mount).Single();
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.LessThan(1.0 * (bound.TotalSeconds + 0.1)), "stopped at the bound. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── the matched rate never rounds up (#2083) ─────────────────────────────────────────

        private static readonly double[] IoptronMatchedRates = [0.53, 2.13];

        [Test]
        public async Task The_turn_runs_at_the_mounts_fastest_rates_at_or_below_3_and_1_degrees_a_second_never_above() {
            // An iOptron's discrete rates: 1°/s is between 0.53 and 1.07. Snapping to the nearest would send 1.07°/s, faster
            // than asked; the turn takes 0.53°/s for its last 3° and 2.13°/s (under 3) for the rest.
            var mount = PolledGemAt(-4.0);
            mount.AxisRateBands = [(0.27, 0.27), (0.53, 0.53), (1.07, 1.07), (2.13, 2.13), (4.27, 4.27)];
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, benchMotors: false);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var rates = RaMotorRates(mount).Select(Math.Abs).Distinct().OrderBy(r => r).ToArray();
            Assert.That(rates, Is.EqualTo(IoptronMatchedRates), "the band at or below each request, never 1.07 or 4.27");
            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_mount_with_no_rate_at_or_below_the_slow_rate_fails_before_the_motor_starts() {
            var mount = PolledGemAt(-4.0);
            mount.AxisRateBands = [(1.07, 2.13)];
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, benchMotors: false);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("slew_failed"));
            Assert.That(message, Does.Contain("no motor speed at or below 1°/s"));
            Assert.That(mount.Motions, Is.Empty, "zero motions. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── Dec and pier side on every poll (#2081 item 5, ARA-11) ───────────────────────────

        [Test]
        public async Task A_Dec_that_drifts_during_an_RA_turn_stops_it_within_0_1_degrees_as_dec_moved() {
            // The Dec axis creeps while the RA motor runs: over the whole turn it would drift 0.2°.
            var mount = PolledGemAt(-4.0);
            mount.DecDriftDegPerSec = 0.4;
            var decBefore = mount.TruePose.DecAxisDeg;
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, _) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.DecMoved));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "the first turn, cut short. " + mount.Describe());
            // Stopped on the first reading past 0.1°: one read (10 ms at 0.4°/s) later at most, plus the exit's stop.
            Assert.That(Math.Abs(mount.TruePose.DecAxisDeg - decBefore), Is.LessThan(PolarAlignService.TurnDecLimitDeg + 0.02));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_pier_side_that_changes_mid_turn_stops_it_as_pier_changed() {
            var mount = PolledGemAt(-4.0);
            var polls = 0;
            mount.OnPoseRead = () => {
                if (Interlocked.Increment(ref polls) == 5) {
                    mount.ReportedSideOverride = PierSide.pierEast; // the driver now reports the other side
                }
            };
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, _) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.PierChanged));
            var turn = RaTurns(mount).Single();
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.LessThan(10.0 * 0.5), "cut short where the side changed. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── a reading that jumps, a turn that does not arrive (ARA-12, M12) ──────────────────

        [Test]
        public async Task M12_an_RA_reading_that_jumps_12_h_mid_turn_fails_as_turn_mismatch_and_never_reverses() {
            // A 12 h jump reads as a 180° turn the wrong way: the motor must not be reversed toward it.
            var mount = PolledGemAt(-4.0);
            var polls = 0;
            mount.OnPoseRead = () => {
                if (Interlocked.Increment(ref polls) == 5) {
                    mount.RaJumpHours = 12.0;
                }
            };
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.TurnMismatch), message);
            Assert.That(message, Does.Contain("jumped"));
            Assert.That(mount.Motions.Where(m => m.End == MotionEnd.Reversed), Is.Empty, "never reversed. " + mount.Describe());
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_turn_whose_settled_reading_is_3_degrees_short_fails_as_turn_mismatch() {
            // The reading reached the step and the motor stopped; the settled reading then says 7°: off by more than 2°.
            var mount = PolledGemAt(-4.0);
            var jumped = false;
            mount.OnFullRead = () => {
                if (!jumped && LastCommand(mount) == "move primary 0" && RaTurns(mount).Length == 1) {
                    jumped = true;
                    mount.RaJumpHours = -3.0 / 15.0;
                }
            };
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.TurnMismatch), message);
            Assert.That(message, Does.Contain("for a 10° RA turn"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── a runaway past the step after the motor's stop (M13) ─────────────────────────────

        [Test]
        public async Task M13_a_motor_stop_the_mount_ignores_is_caught_2_degrees_past_the_step_and_aborted() {
            // moveaxis_stop_ignored: the turn's own MoveAxis 0 is ignored and the RA axis runs on. Past the step + 2° the
            // confirmed stop goes out at once; its AbortSlew stops the mount and the run fails as turn_mismatch.
            var mount = PolledGemAt(-4.0);
            mount.MoveAxisStopIgnored = 1;
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.TurnMismatch), message);
            Assert.That(message, Does.Contain("past the 10° turn"));
            var abort = mount.Stops.First(s => s.Kind == StopKind.Abort);
            Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)), mount.Describe());
            var turn = RaTurns(mount).Single();
            // Caught on the first reading past 12°: at the bench's slow 10°/s one read (0.1°) later at most.
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.LessThan(10.0 + PolarAlignService.RunawayPastStepDeg + (2 * TrueTurnToleranceDeg) + 0.1),
                mount.Describe());
            mount.AssertNoMotionSince(abort.At);
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task M13_a_runaway_that_never_stops_is_reported_mount_did_not_stop() {
            var mount = PolledGemAt(-4.0);
            mount.MoveAxisStopIgnored = int.MaxValue;
            mount.AbortIgnored = int.MaxValue;
            mount.RunawayDegPerSec = 1.0; // slow, so the ledger stays readable
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.MountDidNotStop), message);
            Assert.That(message, Does.Contain("Cut the mount's power"));
            Assert.That(mount.Stops.Count(s => s.Kind == StopKind.Abort), Is.GreaterThanOrEqualTo(PolarAlignService.StopSends));
            Assert.That(mount.IsMoving, Is.True, "the ledger agrees: the RA axis still turns");
        }

        // ── a soft limit (M33) ───────────────────────────────────────────────────────────────

        [Test]
        public async Task M33_a_soft_limit_that_stops_the_motor_ends_the_run_with_one_motion_and_nothing_after() {
            // The firmware stops the RA motor at |HA| 4.3 h, part way into the first turn: the reading stands, the turn
            // stalls, and nothing is sent again (never retried, never re-aimed).
            var mount = PolledGemAt(-4.0);
            mount.Limits = new SoftLimits(MaxAbsHourAngleHours: 4.3);
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            var (reason, _) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(PolarAlignService.SlewStalled));
            var turn = RaTurns(mount).Single();
            Assert.That(turn.End, Is.EqualTo(MotionEnd.LimitHit), mount.Describe());
            Assert.That(mount.Motions, Has.Count.EqualTo(1), "exactly one motion");
            mount.AssertNoMotionSince(turn.EndedAt!.Value);
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg, expectLimitHit: true);
        }

        // ── the motion stays marked until the mount stands still (ARA-3) ─────────────────────

        [Test]
        public async Task Stop_during_settle_after_early_Slewing_false_aborts() {
            // slewing_false_early: the mount reads "not slewing" while its RA axis still turns, and the turn's MoveAxis 0
            // was ignored. A Stop while the turn settles must still send AbortSlew: the motion is not over until the mount
            // stands still on fresh readings.
            var mount = PolledGemAt(-4.0);
            mount.SlewingFalseAfter = TimeSpan.Zero;
            mount.MoveAxisStopIgnored = 1;
            var inSettle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var held = false;
            mount.OnFullRead = () => {
                if (!held && LastCommand(mount) == "move primary 0" && RaTurns(mount).Length == 1) {
                    held = true;
                    inSettle.TrySetResult();
                    release.Wait(SignalTimeout); // the run waits here, mid-settle, until the Stop's round has gone out
                }
            };
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await inSettle.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(mount.IsMoving, Is.True, "the axis runs on after its ignored stop");
            var from = mount.Commands.Count;

            var stopping = svc.StopAsync(null, CancellationToken.None);
            await WaitUntilAsync(() => mount.Commands.Skip(from).Any(c => c.Text == "move secondary 0")).ConfigureAwait(false);
            var round = mount.Commands.Skip(from).Select(c => c.Text).ToArray();
            release.Set();
            await stopping.ConfigureAwait(false);

            Assert.That(round.TakeWhile(t => t != "move secondary 0"), Does.Contain("abort polar_align"),
                "the Stop's first round aborts the still-turning mount: " + string.Join(", ", round));
            mount.AssertEndedStopped(tracking: false);
        }

        private static async Task WaitUntilAsync(Func<bool> condition) {
            var deadline = DateTime.UtcNow + SignalTimeout;
            while (!condition()) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out");
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        // ── the field turns as the mount says (ARA-13, M05) ──────────────────────────────────

        /// <summary>The true sky with a scripted position angle per solve (in order); null once the script runs out.</summary>
        private sealed class AngledSkySolver(FakeGemMount mount, params double[] angles) : IPolarAlignFrameSolver {
            private int _n;

            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                var (ra, dec) = mount.TrueSky;
                var i = Interlocked.Increment(ref _n) - 1;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec, i < angles.Length ? angles[i] : null));
            }
        }

        [TestCase(190.0, PolarAlignService.PierFlip)]
        [TestCase(-172.0, PolarAlignService.PierFlip)]
        [TestCase(40.0, PolarAlignService.TurnMismatch)]
        public async Task M05_frames_whose_field_turned_fail_the_run_before_the_next_turn_with_tracking_off(double secondAngle, string expected) {
            // seed-a at 10°, seed-b turned by 180° (a pier flip) or by 30°: no RA turn does either.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws, solver: new AngledSkySolver(mount, 10.0, secondAngle));

            var (reason, message) = await GuardedRunFailureAsync(mount, ws, svc).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo(expected), message);
            Assert.That(message, Does.Contain("seed-b"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no turn after the frame. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task Frames_whose_field_stays_within_10_degrees_measure_as_before() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws, solver: new AngledSkySolver(mount, 10.0, 14.0, 6.0));

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), mount.Describe());
        }

        // ── NINA's Dec-spread warning (#2081 item 5) ─────────────────────────────────────────

        [Test]
        public async Task A_Dec_spread_over_2_arcsec_across_the_three_points_completes_with_a_warning() {
            // About 3″ of Dec creep over the two turns: far under the 0.1° stop, over NINA's 2″ warning.
            var mount = PolledGemAt(-4.0);
            mount.DecDriftDegPerSec = 3.0 / 3600.0 / (2 * ((7.0 / 30.0) + (3.0 / 10.0)));
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var progress = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignProgress).Payload;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.DecSpreadArcsec, Is.EqualTo(3.0).Within(0.6), "the spread, shown with the live readout");
            Assert.That(progress.GetProperty("dec_spread_arcsec").GetDouble(), Is.EqualTo(status.DecSpreadArcsec));
        }

        [Test]
        public async Task A_Dec_that_holds_across_the_three_points_carries_no_warning() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.DecSpreadArcsec, Is.Null);
        }

        [Test]
        public async Task A_check_with_a_Dec_spread_over_2_arcsec_says_so_on_its_verdict() {
            var mount = PolledGemAt(-4.0);
            mount.DecDriftDegPerSec = 3.0 / 3600.0 / (2 * ((7.0 / 30.0) + (3.0 / 10.0)));
            var ws = new WsRecorder();
            using var svc = NewGuardedService(mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var verdict = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignChecked).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(verdict.GetProperty("dec_spread_arcsec").GetDouble(), Is.EqualTo(3.0).Within(0.6));
            Assert.That(status.CheckResult!.DecSpreadArcsec, Is.EqualTo(verdict.GetProperty("dec_spread_arcsec").GetDouble()));
        }
    }
}
