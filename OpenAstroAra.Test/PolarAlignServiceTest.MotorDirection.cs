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
using PierSide = OpenAstroAra.Core.Enums.PierSide;

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
            Assert.That((saved!.Sign, saved.MountKey, saved.DriverVersion), Is.EqualTo((-1, PolarAlignPreflightEvaluator.UnidentifiedMountKey, mount.DriverVersion)));
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
        public void Test_direction_again_while_a_run_is_going_is_refused_and_keeps_the_saved_direction() {
            var store = TenDegreeStore();
            var saved = new PolarAlignMotorDirectionDto(-1, "hae29c-unique-id", "6.0.0", DateTimeOffset.UtcNow);
            store.PutPolarAlignMotorDirection(saved);
            var running = new Mock<IPolarAlignService>();
            running.SetupGet(s => s.IsActive).Returns(true);

            var refusal = Assert.Throws<RefusalException>(() => EquipmentEndpoints.RetestPolarAlignMotorDirection(store, running.Object));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignAlreadyRunning));
            Assert.That(store.GetPolarAlignMotorDirection(), Is.EqualTo(saved),
                "a run's own test could save right after a forget: nothing is forgotten while it runs");
            running.SetupGet(s => s.IsActive).Returns(false);
            EquipmentEndpoints.RetestPolarAlignMotorDirection(store, running.Object);
            Assert.That(store.GetPolarAlignMotorDirection(), Is.Null, "with no run going, the forget goes through");
        }

        [Test]
        public async Task A_profile_switch_during_the_direction_test_saves_no_sign_and_the_run_still_turns_with_it() {
            var mount = PolledGemAt(-4.0);
            mount.RaMotorSign = -1.0;
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store, motorDirectionSaved: false);
            var active = Guid.NewGuid();
            svc.ActiveProfileId = () => active;
            // Another profile is selected while the test move runs: the store now holds that profile's sections.
            mount.OnFreshRead = _ => {
                if (mount.Motions.Any(m => m.Kind == MotionKind.RaMotor && !m.Refused)) {
                    active = Guid.NewGuid();
                }
            };

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: true), null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"), "the run completes: " + mount.Describe());
            Assert.That(store.GetPolarAlignMotorDirection(), Is.Null, "the sign is not written into the profile switched to");
            var turns = RaTurns(mount);
            Assert.That(turns, Has.Length.EqualTo(3), mount.Describe());
            // The run still used the sign it learned: both turns went east, as the confirm step said.
            Assert.That(turns.Skip(1).Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(-10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public async Task A_direction_test_whose_reading_ends_under_a_quarter_degree_fails_turn_mismatch_and_saves_nothing() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store, motorDirectionSaved: false);
            // Once the test move's motor has stopped, the RA reading falls back 0.45°: it ends under 0.25° from its start.
            mount.OnFreshRead = _ => {
                if (mount.RaJumpHours == 0 && mount.Motions.Any(m => m.Kind == MotionKind.RaMotor && !m.Refused) && mount.MotorRate(0) == 0) {
                    mount.RaJumpHours = -0.45 / 15.0;
                }
            };

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: true), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TurnMismatch), mount.Describe());
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("direction test"));
            Assert.That(store.GetPolarAlignMotorDirection(), Is.Null, "a test that did not move the reading learns no sign");
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "nothing turned after the test move. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public void A_new_origin_whose_turns_fit_the_limit_but_not_with_the_direction_test_is_refused() {
            var mount = PolledGemAt(-4.0);
            using var svc = NewGemService(mount, new WsRecorder(), TenDegreeStore(), motorDirectionSaved: false);
            // A 40° sweep and a 0.25° band tolerance: the limit is 40.25°, so the sweep fits and the 0.5° test does not.
            var fits = svc.PlanSweep(0.0, 85.0, 40.0, epoch: 1, PierSide.pierWest, newOriginSign: 1.0, testDeg: 0.0, bandToleranceDeg: 0.25);
            Assert.That((fits.NewOrigin, fits.LimitDeg), Is.EqualTo((true, 40.25)));

            var refusal = Assert.Throws<RefusalException>(() => svc.PlanSweep(0.0, 85.0, 40.0, epoch: 1, PierSide.pierWest,
                newOriginSign: 1.0, testDeg: PolarAlignService.DirectionTestDeg, bandToleranceDeg: 0.25));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit));
            Assert.That(refusal.Parameters["limit_deg"], Is.EqualTo(40.25));
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

        // ── #2167: the key is the mount's identity ─────────────────────────────────────────────

        /// <summary>A fake GEM service with a capturing logger and the bench motor rates.</summary>
        private static PolarAlignService NewGemServiceLogged(FakeGemMount mount, WsRecorder ws, IProfileStore store, CapturingLogger logger) {
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(NewFake(), new TrueSkySolver(mount), mount, ws, store, logger: logger, motorDirectionSaved: false);
            svc.AxisFastRateDegPerSec = 30.0;
            svc.AxisSlowRateDegPerSec = 10.0;
            return svc;
        }

        [Test]
        public async Task A_direction_saved_on_the_blank_driver_key_is_tested_again_and_saved_for_the_mounts_identity() {
            // Rig night 2026-11-21 (HAE29C, AlpacaBridge 6.0.0~beta2): the daemon never read DriverInfo or DriverVersion, so
            // the direction was saved for the driver '' ''. That save stands for no mount: the next start tests again.
            var mount = PolledGemAt(-4.0);
            mount.DeviceId = "fake-gem-unique-id";
            mount.DriverVersion = "6.0.0~beta2";
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(mount.DeviceId));
            store.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(1, string.Empty, string.Empty, DateTimeOffset.UtcNow));
            var logger = new CapturingLogger();
            using var svc = NewGemServiceLogged(mount, ws, store, logger);

            var plan = await FineRunWithTestToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plan.DirectionTestDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg), "the blank-keyed save does not stand");
            var saved = store.GetPolarAlignMotorDirection()!;
            Assert.That((saved.Sign, saved.MountKey, saved.DriverVersion), Is.EqualTo((1, "fake-gem-unique-id", "6.0.0~beta2")));
            var line = logger.Entries.Single(e => e.EventId == 4651).Message;
            Assert.That(line, Does.Contain("fake-gem-unique-id, driver 6.0.0~beta2"));
            Assert.That(line, Does.Not.Contain("''").And.Not.Contain("\"\""));
            var next = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(next.DirectionTestDeg, Is.Zero, "tested once for this mount and version");
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_mount_whose_driver_reports_no_version_is_keyed_by_its_name_alone_and_tested_once() {
            var mount = PolledGemAt(-4.0);
            mount.DeviceId = string.Empty; // no Alpaca UniqueID: the name is the identity, as for mount_clearance
            mount.Name = "iOptron HAE29C";
            mount.DriverVersion = string.Empty;
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(mount.Name));
            var logger = new CapturingLogger();
            using var svc = NewGemServiceLogged(mount, ws, store, logger);

            var plan = await FineRunWithTestToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plan.DirectionTestDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg));
            var saved = store.GetPolarAlignMotorDirection()!;
            Assert.That((saved.MountKey, saved.DriverVersion), Is.EqualTo(("iOptron HAE29C", string.Empty)));
            foreach (var id in new[] { 4650, 4651 }) {
                var line = logger.Entries.Single(e => e.EventId == id).Message;
                Assert.That(line, Does.Contain("iOptron HAE29C").And.Not.Contain("driver ").And.Not.Contain("\"\""), line);
            }
            var next = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(next.DirectionTestDeg, Is.Zero, "the name alone keys it; no version is not a new version");
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public void A_saved_direction_with_a_blank_mount_key_stands_for_no_mount() {
            // A test saved before #2167 reads back with no mount_key (its driver_info is ignored).
            var older = System.Text.Json.JsonSerializer.Deserialize(
                "{\"sign\":1,\"driver_info\":\"\",\"driver_version\":\"\",\"tested_utc\":\"2026-11-21T03:00:00Z\"}",
                OpenAstroAra.Server.AraJsonSerializerContext.Default.PolarAlignMotorDirectionDto)!;
            var blank = new PolarAlignMotorDirectionDto(1, " ", string.Empty, DateTimeOffset.UtcNow);

            Assert.That(older.IsFor(PolarAlignPreflightEvaluator.UnidentifiedMountKey, string.Empty), Is.False);
            Assert.That(older.IsFor(null, null), Is.False, "a blank driver name and version are never the whole key");
            Assert.That(blank.IsFor(" ", string.Empty), Is.False);
            Assert.That(new PolarAlignMotorDirectionDto(1, "m", string.Empty, DateTimeOffset.UtcNow).IsFor("m", null), Is.True);
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
        public async Task A_confirm_that_showed_a_direction_test_no_longer_owed_is_refused_before_anything_moves() {
            // The confirm step showed the 0.5° test; before Start, a direction was saved for this driver (another session's
            // run): the start would make no test, which is not what the user confirmed.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var fake = NewFake();
            var store = TenDegreeStore();
            using var svc = NewServiceOn(fake, new TrueSkySolver(mount), mount, ws, store, motorDirectionSaved: false);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.DirectionTestDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg));
            store.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(1, PolarAlignPreflightEvaluator.UnidentifiedMountKey, mount.DriverVersion, DateTimeOffset.UtcNow));
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: true), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignUnconfirmed));
            Assert.That(refusal.Message, Does.Contain("makes no direction test"));
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
        }

        [Test]
        public async Task A_direction_test_that_would_pass_the_sweep_limit_when_it_moves_fails_before_the_motor_runs() {
            // The start's plan fits; between the confirmed start and the test move the axis is 31.8° from the origin (a hand on
            // the mount, tracking through a long wait): the 0.5° test either way would take it past the 30° limit and the 2° a
            // run may drift. The test move reads where the axis is now and fails before the motor runs.
            var mount = new FakeStopMount(tracking: false);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var movedAt = -1;
            mount.OnTrackingOn = () => {
                mount.MoveByHand(31.8);
                Volatile.Write(ref movedAt, mount.Count);
            };
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore(), motorDirectionSaved: false);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.DirectionTestDeg, Is.EqualTo(PolarAlignService.DirectionTestDeg));

            await svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, DirectionTest: true), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("sweep_limit"));
            Assert.That(Volatile.Read(ref movedAt), Is.GreaterThanOrEqualTo(0), "the axis was moved before the test");
            AssertNoMotionAfter(mount, Volatile.Read(ref movedAt));
            Assert.That(mount.RaDeg, Is.EqualTo(31.8).Within(0.01), "the test move never ran");
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
            var info = new TelescopeInfo { Connected = true, DeviceId = "hae29c-unique-id", DriverVersion = "6.0.0" };
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => info);
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(ITelescopeMediator))).Returns(mount.Object);

            var never = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);
            store.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(-1, "hae29c-unique-id", "6.0.0", DateTimeOffset.UtcNow));
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

        [Test]
        public void The_motor_direction_endpoint_sends_a_test_saved_before_2167_as_nothing_saved() {
            // A test saved before #2167 reads back with no mount_key: the client's strict reader would refuse a null one.
            var older = System.Text.Json.JsonSerializer.Deserialize(
                "{\"sign\":1,\"driver_info\":\"\",\"driver_version\":\"\",\"tested_utc\":\"2026-11-21T03:00:00Z\"}",
                OpenAstroAra.Server.AraJsonSerializerContext.Default.PolarAlignMotorDirectionDto)!;
            var store = new InMemoryProfileStore();
            store.PutPolarAlignMotorDirection(older);
            var info = new TelescopeInfo { Connected = true, DeviceId = "hae29c-unique-id", DriverVersion = "6.0.0" };
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(() => info);
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(ITelescopeMediator))).Returns(mount.Object);

            var connected = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);
            info.Connected = false;
            var none = EquipmentEndpoints.PolarAlignMotorDirection(store, services.Object);

            Assert.That((connected.Saved, connected.MatchesConnectedMount), Is.EqualTo(((PolarAlignMotorDirectionDto?)null, (bool?)false)));
            Assert.That((none.Saved, none.MatchesConnectedMount), Is.EqualTo(((PolarAlignMotorDirectionDto?)null, (bool?)null)));
            var json = System.Text.Json.JsonSerializer.Serialize(connected,
                OpenAstroAra.Server.AraJsonSerializerContext.Default.PolarAlignMotorDirectionStateDto);
            Assert.That(json, Does.Not.Contain("mount_key"), json);
        }
    }
}
