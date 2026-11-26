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
    /// A turn is timed, not read: the RA motor runs at the fastest rate the mount offers at or below 3°/s for the time the
    /// turn takes at that rate, its lease renewed meanwhile (until the lease covers the rest of the turn), then stops. No
    /// mount reading plays a part, so a reading that hangs, fails, is stale or makes no sense cannot end a measurement;
    /// the plate solves say what the mount did, and a fit whose frames show the same field, or a turn under 0.3 or over 3
    /// times the one the motor was told, is refused. Run on the fake GEM mount, on a clock the routine's own timed turns
    /// move.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>The routine on the fake at the daemon's own 3°/s, with NINA's 10° steps.</summary>
        private static PolarAlignService NewDaemonRateGemService(FakeGemMount mount, WsRecorder ws, IProfileStore? store = null) =>
            NewGemService(mount, ws, store, benchMotors: false);

        // The true hour angle falls as RA rises: two 10° turns east, and two west.
        private static readonly double[] TwoTurnsEast = { -10.0, -10.0 };
        private static readonly double[] TwoTurnsWest = { 10.0, 10.0 };
        private static readonly string[] StartRenewalStopTwice = {
            "move primary 3", "move primary 3", "move primary 0", "move primary 3", "move primary 3", "move primary 0",
        };

        // ── the timed turn ───────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_turn_runs_the_RA_motor_at_the_matched_rate_for_degrees_over_rate_and_then_stops_it() {
            // The mount offers 2°/s at most below the 3°/s the routine asks for: each 10° turn is 5 s of motor at 2°/s.
            var mount = PolledGemAt(-4.0);
            mount.AxisRateBands = [(0.5, 2.0), (4.0, 8.0)];
            var ws = new WsRecorder();
            using var svc = NewDaemonRateGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(2), mount.Describe());
            Assert.That(turns.Select(t => t.Rate), Has.All.EqualTo(2.0), "east at the matched rate, never the 4°/s band above the request");
            Assert.That(turns.Select(t => (t.EndedAt!.Value - t.StartedAt).TotalSeconds), Has.All.EqualTo(5.0).Within(0.002),
                "10° at 2°/s: the motor ran for degrees over rate, then its stop went out");
            Assert.That(turns.Select(t => t.End), Has.All.EqualTo(MotionEnd.Stopped));
            Assert.That(turns.Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(-10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public async Task The_motors_lease_is_renewed_through_a_turn_that_outlasts_it() {
            // A 30° turn at 2°/s runs the motor for 15 s; its lease lasts 3 s. Without the renewals the telescope service
            // stops the axis when the lease lapses, and the turn ends short.
            var mount = PolledGemAt(-4.0);
            mount.AxisRateBands = [(0.5, 2.0)];
            var ws = new WsRecorder();
            using var svc = NewDaemonRateGemService(mount, ws, NewStore());
            Assert.That(svc.AxisLeaseRenew, Is.EqualTo(TimeSpan.FromSeconds(1)), "the daemon renews about once a second");

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(2), mount.Describe());
            Assert.That(mount.Stops.Where(s => s.Kind == StopKind.LeaseStop), Is.Empty, "the lease never lapsed. " + mount.Describe());
            Assert.That(turns.Select(t => t.End), Has.All.EqualTo(MotionEnd.Stopped), "each turn ended on the routine's own stop");
            Assert.That(turns.Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(-30.0).Within(0.1), "the whole 30°");
        }

        [Test]
        public async Task A_turn_sends_no_renewal_once_the_lease_it_holds_covers_its_end() {
            // 10° at 3°/s runs the motor for 3.33 s on a 3 s lease renewed every second. The renewal at 1 s carries the lease
            // to 4 s, past the turn's end: none goes out at 2 s or at 3 s, where one that stalls would hold the stop back.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            Assert.That((svc.AxisRateDegPerSec, svc.AxisLeaseRenew), Is.EqualTo((3.0, TimeSpan.FromSeconds(1))));

            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var motor = mount.Log.Where(c => c.StartsWith("move primary", StringComparison.Ordinal)).Take(6).ToArray();
            Assert.That(motor, Is.EqualTo(StartRenewalStopTwice),
                "each turn: its start, the one renewal it needs, its stop. " + string.Join(", ", mount.Log));
            Assert.That(mount.RaDeg, Is.EqualTo(20.0).Within(0.01), "both turns ran their whole 10°: the lease never lapsed");
        }

        [Test]
        public async Task A_Stop_in_the_middle_of_a_turn_stops_the_motor_where_it_is() {
            // The clock stands still once the motor runs: the turn's time never runs out, and the Stop finds it mid-turn.
            var clock = new ManualMountClock { AutoAdvance = true };
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0, clock: clock);
            var ws = new WsRecorder();
            using var svc = NewDaemonRateGemService(mount, ws);
            clock.AutoAdvance = false;
            var turning = clock.TimerSet.Task;
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await turning.WaitAsync(SignalTimeout).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(0.5)); // half a second of motor: 1.5° of the 10°
            Assert.That(mount.IsMoving, Is.True, "the RA motor runs");
            Assert.That(svc.IsMovingMount, Is.True);

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var turn = RaTurns(mount).Single();
            Assert.That(Math.Abs(turn.TrueDeltaHaDeg), Is.EqualTo(1.5).Within(0.1), "stopped where the Stop found it. " + mount.Describe());
            Assert.That(mount.IsMoving, Is.False);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // ── no reading can end a measurement ─────────────────────────────────────────────────

        [TestCase("hang")]
        [TestCase("fail")]
        [TestCase("stale")]
        [TestCase("dec -106")]
        public async Task A_mount_whose_readings_hang_fail_are_stale_or_make_no_sense_still_measures_on_the_solves(string fault) {
            var mount = PolledGemAt(-4.0);
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            switch (fault) {
                case "hang": mount.HangReads = hang; break;
                case "fail": mount.FreshReadsFail = int.MaxValue; break;
                case "stale": mount.FreshReadsStale = int.MaxValue; break;
                default: mount.ReportedDecOffsetDeg = -191.0; break; // the tube at Dec 85° reads Dec −106°
            }
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no reading ended the run");
            // The fake's axis is the true pole: only the refraction term is left.
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(0.98).Within(0.2));
            Assert.That(RaTurns(mount).Select(t => t.TrueDeltaHaDeg), Is.EqualTo(TwoTurnsEast).Within(TrueTurnToleranceDeg), mount.Describe());
            // The readings come back before the Stop: its confirmation is the one place that needs them.
            mount.HangReads = null;
            hang.TrySetResult();
            mount.FreshReadsFail = 0;
            mount.FreshReadsStale = 0;
            mount.ReportedDecOffsetDeg = 0.0;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            mount.AssertEndedStopped(tracking: false);
        }

        // ── the solves judge the turn ────────────────────────────────────────────────────────

        [Test]
        public async Task A_solved_turn_far_from_the_told_one_fails_the_fit_as_the_mount_not_turning_as_told() {
            // The mount takes 3°/s and turns at 0.5°/s (a slipping clutch): each 10° turn moves the sky 1.7°.
            var mount = PolledGemAt(-4.0);
            mount.MaxAxisRateDegPerSec = 0.5;
            var ws = new WsRecorder();
            using var svc = NewDaemonRateGemService(mount, ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("axis_fit_failed"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("the mount did not turn as told").And.Contain("1.7°").And.Contain("10.0°"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), "both turns ran before the fit judged them. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_mount_that_turns_the_other_way_than_asked_still_measures_the_axis() {
            // A driver whose positive rate turns RA down: told east, the mount turns west. From Home either way is safe, and
            // the three frames lie on the same circle about the axis.
            var mount = PolledGemAt(-4.0);
            mount.RaMotorSign = -1.0;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(RaTurns(mount).Select(t => t.TrueDeltaHaDeg), Is.EqualTo(TwoTurnsWest).Within(TrueTurnToleranceDeg),
                "west, the other way than the positive rate asked. " + mount.Describe());
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(0.98).Within(0.2), "the fake's axis is the true pole: the refraction term");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero);
            mount.AssertEndedStopped(tracking: false);
        }

        // ── the fit's own rules ──────────────────────────────────────────────────────────────

        private static readonly DateTimeOffset FitT0 = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero);

        /// <summary>Three points a camera <paramref name="coneDeg"/> off a mount axis on the true pole solves, the mount
        /// turned by <paramref name="turnDeg"/> between them and tracking: each 30 s after the last.</summary>
        private static ((PolarAlignSolveOutcome, DateTimeOffset) A, (PolarAlignSolveOutcome, DateTimeOffset) B, (PolarAlignSolveOutcome, DateTimeOffset) C)
                PointsOnACone(double coneDeg, double turnDeg, double poleDecDeg = 90.0) {
            var dec = poleDecDeg > 0 ? 90.0 - coneDeg : -90.0 + coneDeg;
            return ((new PolarAlignSolveOutcome(true, 100.0, dec), FitT0),
                (new PolarAlignSolveOutcome(true, 100.0 + turnDeg, dec), FitT0.AddSeconds(30)),
                (new PolarAlignSolveOutcome(true, 100.0 + (2 * turnDeg), dec), FitT0.AddSeconds(60)));
        }

        [TestCase(10.0, 10.0, true)]
        [TestCase(-10.0, 10.0, true)]
        [TestCase(10.0, -10.0, true)]
        [TestCase(10.0, 10.0, false)]
        [TestCase(-10.0, 10.0, false)]
        public void A_turn_fits_with_no_mismatch_whichever_way_the_mount_turned_for_the_turn_it_was_told(double turnedDeg, double toldDeg, bool north) {
            var (a, b, c) = PointsOnACone(coneDeg: 0.2, turnedDeg, poleDecDeg: north ? 90.0 : -90.0);

            var (axis, mismatch) = PolarAlignService.FitThreePointOrFail(a, b, c, toldDeg, toldDeg, north);

            Assert.That(Math.Abs(axis.DecDeg), Is.EqualTo(90.0).Within(1e-6));
            Assert.That(axis.ConeDeg, Is.EqualTo(0.2).Within(1e-6));
            Assert.That(mismatch, Is.EqualTo(0.0).Within(1e-4), "only the size of the turn is compared");
        }

        [Test]
        public void Three_frames_of_the_same_field_are_refused_and_a_camera_a_little_further_off_the_axis_fits() {
            // 10° turns on a cone of 100″ put the points 17″ apart; on a cone of 150″, 26″.
            var (a, b, c) = PointsOnACone(coneDeg: 100.0 / 3600.0, turnDeg: 10.0);
            var sameField = Assert.Throws(Is.InstanceOf<Exception>(), () => PolarAlignService.FitThreePointOrFail(a, b, c, 10.0, 10.0, north: true));
            var (d, e, f) = PointsOnACone(coneDeg: 150.0 / 3600.0, turnDeg: 10.0);

            var (axis, _) = PolarAlignService.FitThreePointOrFail(d, e, f, 10.0, 10.0, north: true);

            Assert.That(sameField!.Message, Does.Contain("the three frames show the same field").And.Contain("17″"));
            Assert.That(PolarAlignService.MinPointSeparationArcsec, Is.EqualTo(20.0));
            Assert.That(axis.DecDeg, Is.EqualTo(90.0).Within(1e-6));
        }

        // A motor stop that stalls for seconds on a slow link overruns a timed turn (4 s at 2°/s adds 8° to a 10° turn), and
        // three distinct points still fit the axis: only a turn under 0.3 or over 3 times the told one is the mount not
        // turning as told.
        [TestCase(2.9, true)]
        [TestCase(3.1, false)]
        [TestCase(5.0, false)]
        [TestCase(18.0, false)]
        [TestCase(29.9, false)]
        [TestCase(30.1, true)]
        public void A_solved_turn_under_0_3_or_over_3_times_the_told_10_degrees_is_refused(double turnedDeg, bool refused) {
            var (a, b, c) = PointsOnACone(coneDeg: 0.2, turnedDeg);

            void Fit() => PolarAlignService.FitThreePointOrFail(a, b, c, 10.0, 10.0, north: true);

            Assert.That((PolarAlignService.MinSolvedTurnRatio, PolarAlignService.MaxSolvedTurnRatio), Is.EqualTo((0.3, 3.0)));
            if (refused) {
                Assert.That(Assert.Throws(Is.InstanceOf<Exception>(), Fit)!.Message, Does.Contain("the mount did not turn as told"));
            } else {
                Assert.DoesNotThrow(Fit);
            }
        }
    }
}
