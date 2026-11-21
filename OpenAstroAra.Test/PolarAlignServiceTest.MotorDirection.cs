#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2081 rows 4 and 5 — the RA turn direction is the profile's <c>polar_align.east_direction</c> at a new sweep origin
    /// (never the mount's hour angle), later starts turn back toward the origin (#2089), and the motor turns the way the
    /// confirm step says: a mount whose driver has no saved RA motor direction is tested first with a 0.5° turn (shown in
    /// the confirm step, inside the sweep limit), the sign is saved per profile and driver, and a turn that goes the other
    /// way from the saved sign fails within 0.5° and is never reversed. Run on the fake GEM mount, asserting on its TRUE
    /// pose: "east" must mean the hour angle really fell.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>The true RA travel of one poll of a bench turn at its fast 30°/s on a polled mount (one fresh read each),
        /// plus the reads' tracking.</summary>
        private static readonly double OnePollAtBenchFastDeg = (30.0 * FreshRead.TotalSeconds) + 0.01;

        /// <summary>A fine start confirmed from the plan read now, its direction test included; waits for the adjust loop.</summary>
        private static async Task<PolarAlignStartPlanDto> FineRunWithTestToAdjustingAsync(PolarAlignService svc) {
            var mark = Mark(svc);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: plan.DirectionTestDeg > 0),
                null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            return plan;
        }

        // ── the test move ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_first_run_on_a_mount_whose_positive_rate_lowers_RA_tests_saves_minus_one_and_completes() {
            var mount = PolledGemAt(-4.0);
            mount.RaMotorSign = -1.0;
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store, motorDirectionSaved: false);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            Assert.That((plan.Direction, plan.DirectionTestDeg), Is.EqualTo((PolarAlignDirections.East, PolarAlignService.DirectionTestDeg)),
                "the confirm step shows the test move");
            await svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: true), null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"), "the run completes: " + mount.Describe());
            var saved = store.GetPolarAlignMotorDirection();
            Assert.That(saved, Is.Not.Null);
            Assert.That((saved!.Sign, saved.DriverInfo, saved.DriverVersion), Is.EqualTo((-1, mount.DriverInfo, mount.DriverVersion)));
            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(3), "the test move, then the measurement's two turns. " + mount.Describe());
            // The test sent the rate that would turn east on NINA's assumption: this driver turned the true axis west.
            Assert.That(turns[0].TrueDeltaHaDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg).Within(TrueTurnToleranceDeg));
            // Then both turns went east, as the confirm step said: the true hour angle fell by the step each time.
            Assert.That(turns.Skip(1).Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(-10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignChecked), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_motor_whose_sign_flipped_after_the_test_fails_within_half_a_degree_and_one_poll_and_is_never_reversed() {
            // The saved direction says a positive rate raises RA; the driver now lowers it with the same name and version.
            var mount = PolledGemAt(-4.0);
            mount.RaMotorSign = -1.0;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.DirectionTestDeg, Is.Zero, "a saved direction stands for this driver: no test");
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TurnWrongWay));
            var turn = RaTurns(mount).Single();
            Assert.That(turn.Rate, Is.GreaterThan(0), "the one motor run is the confirmed (east) way's; it was never restarted the other way");
            // East would lower the true hour angle; it rose instead, and the turn stopped within 0.5° and one poll.
            Assert.That(turn.TrueDeltaHaDeg, Is.GreaterThan(0).And.LessThanOrEqualTo(PolarAlignService.WrongWayDeg + OnePollAtBenchFastDeg),
                mount.Describe());
            Assert.That(mount.Motions.Count(m => m.Kind == MotionKind.RaMotor && !m.Refused), Is.EqualTo(1), "nothing turned after the failure");
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_new_driver_version_tests_the_direction_again_and_saves_it_for_that_version() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store); // saved for version 1.0
            mount.DriverVersion = "1.1"; // an AlpacaBridge upgrade

            var plan = await FineRunWithTestToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plan.DirectionTestDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg));
            Assert.That(store.GetPolarAlignMotorDirection()!.DriverVersion, Is.EqualTo("1.1"));
            Assert.That(store.GetPolarAlignMotorDirection()!.Sign, Is.EqualTo(1));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(3), "the test move and the two turns. " + mount.Describe());
            var next = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(next.DirectionTestDeg, Is.Zero, "tested once for this version");
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // Site latitude and the driver's sign → the direction the confirm step shows (east_direction unset: east in the north,
        // west in the south). East means the true hour angle falls, west that it grows, whichever way the driver's rate runs.
        [TestCase(45.0, 1.0)]
        [TestCase(45.0, -1.0)]
        [TestCase(-45.0, 1.0)]
        [TestCase(-45.0, -1.0)]
        public async Task In_both_hemispheres_the_direction_shown_is_the_way_the_true_RA_axis_turns(double latitudeDeg, double motorSign) {
            var south = latitudeDeg < 0;
            var mount = FakeGemMount.CounterweightDownAt(south ? 4.0 : -4.0, south ? -85.0 : 85.0, lstHours: 20.0, latitudeDeg: latitudeDeg,
                clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            mount.RaMotorSign = motorSign;
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = latitudeDeg, LongitudeDeg = mount.LongitudeDeg });
            using var svc = NewGemService(mount, ws, store, motorDirectionSaved: false);

            var plan = await FineRunWithTestToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plan.Direction, Is.EqualTo(south ? PolarAlignDirections.West : PolarAlignDirections.East));
            Assert.That(store.GetPolarAlignMotorDirection()!.Sign, Is.EqualTo((int)motorSign));
            var turns = RaTurns(mount).Skip(1).ToArray(); // after the test move
            Assert.That(turns, Has.Length.EqualTo(2), mount.Describe());
            var trueSign = plan.Direction == PolarAlignDirections.East ? -1.0 : 1.0;
            Assert.That(turns.Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(trueSign * 10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // ── the confirm step and the sweep limit ─────────────────────────────────────────────

        [Test]
        public async Task A_motor_start_that_did_not_see_the_direction_test_is_refused_before_anything_moves() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var fake = NewFake();
            using var svc = NewServiceOn(fake, new TrueSkySolver(mount), mount, ws, TenDegreeStore(), motorDirectionSaved: false);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignUnconfirmed));
            Assert.That(refusal.Message, Does.Contain("to learn its RA motor's direction"));
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
        }

        [Test]
        public async Task The_direction_test_counts_toward_the_sweep_limit() {
            // Run 1 turns 2 × 15° east: the axis ends 30° from the origin. At 10° steps the limit is 30°, so a run back
            // toward the origin reaches exactly 30° from it at its start: inside the limit, but not with a 0.5° test first.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 15.0 });
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: store);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });

            var saved = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That((saved.NewOrigin, saved.Direction, saved.DirectionTestDeg), Is.EqualTo((false, PolarAlignDirections.West, 0.0)));
            Assert.That(saved.OriginOffsetDeg, Is.EqualTo(30.0).Within(0.05));

            store.PutPolarAlignMotorDirection(null); // Test direction again
            var at = mount.Count;
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit), "30° plus the 0.5° test is past the 30° limit");
            AssertNoMotionAfter(mount, at);
        }

        [Test]
        public async Task East_direction_picks_only_a_new_origins_way_and_a_later_start_turns_back_toward_the_origin() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { EastDirection = false });
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: store);

            var first = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var second = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((first.NewOrigin, first.Direction), Is.EqualTo((true, PolarAlignDirections.West)), "the setting's way");
            Assert.That((second.NewOrigin, second.Direction), Is.EqualTo((false, PolarAlignDirections.East)),
                "back toward the origin: repeated Starts never walk the mount one way");
            Assert.That(OffsetFrom(0.0, mount.RaDeg), Is.EqualTo(0.0).Within(0.5), "the second run ended back at the origin");
        }

        [TestCase(null, 45.0, 1.0)]
        [TestCase(null, -45.0, -1.0)]
        [TestCase(true, -45.0, 1.0)]
        [TestCase(false, 45.0, -1.0)]
        public void East_direction_unset_is_east_in_the_north_and_west_in_the_south(bool? eastDirection, double latitudeDeg, double sign) {
            Assert.That(PolarAlignEastDirection.Sign(eastDirection, latitudeDeg), Is.EqualTo(sign));
        }

        // ── GET /polaralign/motor-direction ──────────────────────────────────────────────────

        [Test]
        public void The_motor_direction_endpoint_says_whether_the_saved_test_stands_for_the_connected_mounts_driver() {
            var store = new InMemoryProfileStore();
            var info = new TelescopeInfo { Connected = true, DriverInfo = "AlpacaBridge iOptron", DriverVersion = "6.0.0" };
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => info);
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(ITelescopeMediator))).Returns(mount.Object);

            var never = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);
            store.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(-1, "AlpacaBridge iOptron", "6.0.0", DateTimeOffset.UtcNow));
            var same = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);
            info.DriverVersion = "6.1.0";
            var upgraded = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);
            info.Connected = false;
            var none = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);

            Assert.That((never.Saved, never.MatchesConnectedMount), Is.EqualTo(((PolarAlignMotorDirectionDto?)null, (bool?)false)));
            Assert.That((same.Saved!.Sign, same.MatchesConnectedMount), Is.EqualTo((-1, (bool?)true)));
            Assert.That(upgraded.MatchesConnectedMount, Is.False, "another driver version: the next start tests again");
            Assert.That(none.MatchesConnectedMount, Is.Null, "no mount connected");
        }
    }
}
