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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2089 — the RA sweep bound across runs. With no Home each new Start used to build a new band from wherever the mount
    /// was (#2082: every measurement is its own Start now, Verify included): "Adjust again" after Verify
    /// turned 20 → 40° from the first start, and repeated runs walked toward the pier. Polar Align now keeps the session's
    /// sweep origin, turns every later run back toward it, and refuses a run that would go more than 30° from it. All of
    /// it from RA differences the mount reports, never its clock, LST or absolute RA.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>NINA's 10° steps for the seed and the check: every run's band is 20°.</summary>
        private static InMemoryProfileStore TenDegreeStore() {
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });
            return store;
        }

        /// <summary>A fine start the user confirmed from the plan read now; waits for the adjust loop. Returns the plan.</summary>
        private static async Task<PolarAlignStartPlanDto> FineRunToAdjustingAsync(PolarAlignService svc) {
            var mark = Mark(svc);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            return plan;
        }

        private static double OffsetFrom(double originRaDeg, double raDeg) => ((raDeg - originRaDeg + 540.0) % 360.0) - 180.0;

        // ── within ±20° over Rough, Fine, Verify and Adjust again ────────────────────────────

        [Test]
        public async Task Rough_Fine_Verify_and_Adjust_again_stay_within_20_degrees_of_the_origin() {
            // The reviewers' case, one measurement per Start (#2082): Rough 0 → 20, Stop; Fine 20 → 0; Verify (the Check of the
            // adjust loop) 0 → 20; then "Adjust again" (a fine start) used to build a new band at 20 and turn 20 → 40. Every
            // Start goes through the session's sweep plan and turns back toward the origin: 20 → 0, and its Verify 0 → 20.
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewMount(); // RA 0 / Dec 85, east of the meridian at LST 20 h
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c, stopped: stops.Enqueue);
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount, store: TenDegreeStore(), mainCamera: new FakeMainCamera());

            var first = await svc.GetStartPlanAsync(PolarAlignStages.Coarse, CancellationToken.None).ConfigureAwait(false);
            Assert.That((first.NewOrigin, first.OriginOffsetDeg, first.Direction, first.SweepLimitDeg), Is.EqualTo((true, 0.0, PolarAlignDirections.East, 30.0)));
            await svc.StartAsync(PolarAlignStages.Coarse, new PolarAlignStartConfirmation(first.Direction, first.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            // Adjust again: the plan says where the axis is from the origin, and that it turns back (west).
            var again = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(again.NewOrigin, Is.False);
            Assert.That(again.OriginOffsetDeg, Is.EqualTo(20.0).Within(0.5));
            Assert.That(again.Direction, Is.EqualTo(PolarAlignDirections.West), "back toward the origin, as the confirm step shows");
            mark = Mark(svc);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(again.Direction, again.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            var turns = stops.Select(c => OffsetFrom(0.0, c.RADegrees)).ToArray();
            Assert.That(turns, Is.EqualTo(RoughFineVerifyAgainTurns).Within(1e-6), "Rough, Fine, Verify, then Adjust again and its Verify");
            Assert.That(turns, Has.All.InRange(-20.0, 20.0));
            NoHomeNoGoTo(mount);
        }

        private static readonly double[] RoughFineVerifyAgainTurns = { 10.0, 20.0, 10.0, 0.0, 10.0, 20.0, 10.0, 0.0, 10.0, 20.0 };

        // ── repeated Starts ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task Repeated_starts_never_turn_the_RA_axis_more_than_30_degrees_from_the_origin() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());

            var offsets = new List<double>();
            var directions = new List<string>();
            for (var run = 0; run < 5; run++) {
                var plan = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
                directions.Add(plan.Direction);
                Assert.That(Math.Abs(plan.OriginOffsetDeg), Is.LessThanOrEqualTo(PolarAlignService.MaxSweepFromOriginDeg));
                Assert.That(plan.NewOrigin, Is.EqualTo(run == 0), "only the first start sets the origin");
                offsets.Add(OffsetFrom(0.0, mount.RaDeg)); // the run's far end: both turns done
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            Assert.That(offsets, Has.All.InRange(-PolarAlignService.MaxSweepFromOriginDeg, PolarAlignService.MaxSweepFromOriginDeg));
            Assert.That(offsets, Has.All.InRange(-1.0, 21.0), "the runs swing between the origin and 20° east of it");
            Assert.That(directions, Is.EqualTo(new[] {
                PolarAlignDirections.East, PolarAlignDirections.West, PolarAlignDirections.East, PolarAlignDirections.West, PolarAlignDirections.East,
            }), "every run after the first turns back toward the origin");
        }

        // ── the refusal ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_start_whose_turns_would_pass_the_sweep_limit_is_refused_until_the_mount_is_slewed_back() {
            // A run at the bench's 30° steps leaves the axis 60° from the origin; at 10° steps the limit is 30°.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var store = NewStore();
            var fake = NewFake();
            using var svc = NewService(fake, new HintFollowingSolver(), mount.Mock, ws, store: store);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });
            var at = mount.Count;
            var leases = fake.LeaseCalls.Count;

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None));
            var startRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.West), null, CancellationToken.None));

            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit), "the confirm step already says so");
            Assert.That(startRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit));
            Assert.That(startRefusal.Message, Is.EqualTo(
                "The mount has turned 60° from where Polar Align started; slew it back near the pole (counterweights down) and confirm again"));
            Assert.That((double)startRefusal.Parameters["offset_deg"]!, Is.EqualTo(60.0).Within(0.5));
            Assert.That(startRefusal.Parameters["limit_deg"], Is.EqualTo(PolarAlignService.MaxSweepFromOriginDeg));
            AssertNoMotionAfter(mount, at);
            Assert.That(fake.LeaseCalls.Count, Is.EqualTo(leases), "refused before the camera is taken");

            // Slewed back near the start: a new pose, which the user confirms as Polar Align's new origin.
            mount.MoveByHand(2.0);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That((plan.NewOrigin, plan.OriginOffsetDeg), Is.EqualTo((true, 0.0)));
        }

        [Test]
        public async Task Repeated_Checks_are_refused_at_the_sweep_limit() {
            // #2082 — Check is a confirmed Start(check) through the session's sweep plan, from the adjust loop too: the fine
            // run's bench 30° turns leave the axis 60° from the origin, past the 30° a check (2 × 10°) may reach. Next used
            // to lead the run into its check inside the run's own 60° band, with no session limit.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var fake = NewFake();
            using var svc = NewService(fake, new HintFollowingSolver(), mount.Mock, ws, store: NewStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var at = mount.Count;
            var leases = fake.LeaseCalls.Count;
            var events = ws.Events.Count;

            for (var attempt = 0; attempt < 2; attempt++) {
                var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None));
                var startRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                    PolarAlignStages.Check, new PolarAlignStartConfirmation(PolarAlignDirections.West), null, CancellationToken.None));
                Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit), "the confirm step already says so");
                Assert.That(startRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit));
                Assert.That((double)startRefusal.Parameters["offset_deg"]!, Is.EqualTo(60.0).Within(0.5));
            }

            // Refused before the loop was touched: it runs on, nothing moved, the camera was not taken again.
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That((status.State, status.Stage), Is.EqualTo(("adjusting", PolarAlignStages.Fine)));
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.Since(at), Does.Not.Contain("abort polar_align"), "the loop was not stopped");
            Assert.That(ws.Events.Skip(events).Select(e => e.Type), Has.None.EqualTo(WsEventCatalog.PolarAlignStopped).And.None.EqualTo(WsEventCatalog.PolarAlignStarted));
            Assert.That(fake.LeaseCalls.Count, Is.EqualTo(leases));

            // After the loop ends (Stop, or Done), a Check is refused the same way.
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var afterStop = mount.Count;
            var after = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Check, new PolarAlignStartConfirmation(PolarAlignDirections.West), null, CancellationToken.None));
            Assert.That(after!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSweepLimit));
            AssertNoMotionAfter(mount, afterStop);
        }

        [Test]
        public async Task A_run_that_failed_after_a_readout_is_the_last_run_a_Check_compares_with() {
            // #2082 review — the last run's readout, whatever ended it: a fine loop a Stop Mount failed still had one.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var loop = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(loop.CurrentErrorArcmin, Is.Not.Null, "the loop has a readout");

            var mark = Mark(svc);
            mount.StopMount();
            Assert.That((await PollStateAsync(svc, mark, "failed").ConfigureAwait(false)).State, Is.EqualTo("failed"));
            mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var check = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(check.State, Is.EqualTo("checked"));
            Assert.That(check.CheckResult!.FineErrorArcmin, Is.Not.Null, "the failed run's readout is the last run's");
        }

        // ── moved by other means ─────────────────────────────────────────────────────────────

        [Test]
        public async Task A_mount_moved_by_other_means_needs_its_new_start_confirmed_and_becomes_the_origin() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var seen = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That((seen.NewOrigin, seen.Direction), Is.EqualTo((false, PolarAlignDirections.West)));

            // The user slews the mount to RA 270° (west of the meridian) after reading the plan. The turn direction is
            // west either way; but the plan they confirmed continued the old origin, so the confirmation moves nothing.
            mount.MoveByHand(270.0);
            var at = mount.Count;
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(seen.Direction, seen.NewOrigin), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignUnconfirmed), "confirm again");
            AssertNoMotionAfter(mount, at);

            // The confirm step asks again: a new start, which becomes the origin.
            var fresh = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            Assert.That((fresh.NewOrigin, fresh.OriginOffsetDeg, fresh.Direction), Is.EqualTo((true, 0.0, PolarAlignDirections.West)));
            var far = OffsetFrom(270.0, mount.RaDeg);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var next = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            Assert.That(next.NewOrigin, Is.False);
            Assert.That(next.OriginOffsetDeg, Is.EqualTo(far).Within(0.5), "measured from the new origin at RA 270°");
            Assert.That(Math.Abs(next.OriginOffsetDeg), Is.EqualTo(20.0).Within(0.5));
        }

        [Test]
        public async Task A_pier_side_that_changed_since_Polar_Align_last_turned_the_mount_is_a_new_start() {
            var mount = new FakeStopMount(tracking: true) { Side = PierSide.pierEast };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false)).NewOrigin, Is.False);

            mount.Side = PierSide.pierWest; // a flip GoTo that came back to the same RA reading

            Assert.That((await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false)).NewOrigin, Is.True);
        }

        [Test]
        public async Task A_pier_flip_the_refresh_cache_missed_is_a_new_start() {
            // The cache's last SideOfPier read failed (pierUnknown): only the fresh reading shows the flip.
            var mount = new FakeStopMount(tracking: true) { Side = PierSide.pierEast, CachePierSideFailed = true };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false)).NewOrigin, Is.False);

            mount.Side = PierSide.pierWest; // a flip GoTo that came back to the same RA reading

            Assert.That((await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false)).NewOrigin, Is.True);
        }

        // ── reconnect ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Reconnecting_the_mount_forgets_the_origin() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var before = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That((before.NewOrigin, Math.Round(before.OriginOffsetDeg)), Is.EqualTo((false, 20.0)));

            mount.Reconnect();
            var after = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            Assert.That((after.NewOrigin, after.OriginOffsetDeg), Is.EqualTo((true, 0.0)), "a reconnected mount may have been moved meanwhile");
            Assert.That(after.Direction, Is.EqualTo(PolarAlignDirections.East), "a new origin turns away from the meridian, as before");
        }

        // ── RA wrap ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_offset_from_the_origin_wraps_across_0_h() {
            // Origin at RA 355°: the first run turns east across 0 h (355 → 5 → 15). The axis is then 20° east of the
            // origin, not 340° west, and the next run turns back across 0 h (15 → 5 → 355).
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            mount.MoveByHand(355.0);

            var first = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var firstFar = mount.RaDeg;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var second = await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var secondFar = mount.RaDeg;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var third = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            Assert.That(first.Direction, Is.EqualTo(PolarAlignDirections.East));
            Assert.That(OffsetFrom(355.0, firstFar), Is.EqualTo(20.0).Within(0.5), "RA 15°: 20° east of the origin across 0 h");
            Assert.That((second.NewOrigin, second.Direction), Is.EqualTo((false, PolarAlignDirections.West)));
            Assert.That(second.OriginOffsetDeg, Is.EqualTo(20.0).Within(0.5), "+20°, never −340°");
            Assert.That(OffsetFrom(355.0, secondFar), Is.EqualTo(0.0).Within(0.5), "back to the origin across 0 h");
            Assert.That(third.NewOrigin, Is.False, "the RA wrap is not a move by other means");
            Assert.That(third.OriginOffsetDeg, Is.EqualTo(0.0).Within(0.5));
            Assert.That(third.Direction, Is.EqualTo(PolarAlignDirections.East), "at the origin: the origin's own first way");
        }

        // ── a driver read failure in the middle of a turn ───────────────────────────────────────

        [Test]
        public async Task A_driver_read_that_fails_during_a_turn_stops_the_motor_and_never_turns_it_back_past_the_start() {
            // The second turn runs from RA 10° east. One read fails mid-turn: the cache is then the (0, 0) placeholder,
            // and progress read from it (−10°) looked like a wrong-way turn: the motor reversed and ran west, out of the
            // band and toward the far side of the origin, until the stall bound. The move now stops and the run fails.
            var mount = new FakeStopMount(tracking: true) { FailReadPastRaDeg = 12.0 };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.Direction, Is.EqualTo(PolarAlignDirections.East));

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("no fresh reading of where the mount points"));
            Assert.That(mount.Log.Where(c => c.StartsWith("move primary -", StringComparison.Ordinal)), Is.Empty,
                "never reversed on the placeholder: " + string.Join(", ", mount.Log));
            Assert.That(mount.RaDeg, Is.InRange(12.0, 13.0), "stopped where the read failed");
            Assert.That(mount.AtRest, Is.True);
        }

        // ── the axis's place before each turn ────────────────────────────────────────────────

        /// <summary>Answers at the hint (like <see cref="HintFollowingSolver"/>) and runs <see cref="OnFrame"/> on each frame
        /// it solves, by id (e.g. <c>seed-b-1</c>).</summary>
        private sealed class FrameHookSolver : IPolarAlignFrameSolver {
            public Action<string>? OnFrame { get; set; }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                OnFrame?.Invoke(System.IO.Path.GetFileNameWithoutExtension(fitsPath));
                return Task.FromResult(hintRa is double ra ? new PolarAlignSolveOutcome(true, ra, 85.0) : SolveA);
            }
        }

        [Test]
        public async Task A_turn_that_would_take_the_axis_past_the_sweep_limit_after_its_frames_fails_before_the_motor_runs() {
            // #2082 review — five frames per point at long exposures keep the mount tracking at each point for many minutes,
            // and tracking turns the RA axis on: the start's plan (0 → 20° east here) cannot see that. The second turn reads
            // where the axis is now: 25° from the origin (the frames' time, stood in for by the reading), and its 10° would
            // take it to 35°, past the 30° limit and the 2° a run may drift. It fails before the motor runs.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var movedAt = -1;
            var solver = new FrameHookSolver {
                OnFrame = frame => {
                    if (frame == "seed-b-1") {
                        mount.MoveByHand(25.0);
                        Volatile.Write(ref movedAt, mount.Count);
                    }
                },
            };
            using var svc = NewService(NewFake(), solver, mount.Mock, ws, store: TenDegreeStore());
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.Direction, Is.EqualTo(PolarAlignDirections.East));

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("sweep_limit"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("25°").And.Contain("slew it back near the pole"));
            Assert.That(Volatile.Read(ref movedAt), Is.GreaterThanOrEqualTo(0));
            AssertNoMotionAfter(mount, Volatile.Read(ref movedAt));
            Assert.That(mount.RaDeg, Is.EqualTo(25.0).Within(0.01), "the second turn never ran");
        }

        [Test]
        public async Task A_turn_that_ends_within_the_drift_allowance_past_the_limit_runs_as_planned() {
            // The other side of the guard: the axis at 21° before the second turn ends it at 31°, past the 30° limit but
            // within the 2° a run may drift (a turn's overshoot, tracking between the turns). That is not a failure.
            var mount = new FakeStopMount(tracking: true);
            var solver = new FrameHookSolver {
                OnFrame = frame => {
                    if (frame == "seed-b-1") {
                        mount.MoveByHand(21.0);
                    }
                },
            };
            using var svc = NewService(NewFake(), solver, mount.Mock, store: TenDegreeStore());

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
        }

        // ── tracking is not a move by other means ────────────────────────────────────────────

        // from RA, to RA, seconds between → how far the mount moved beyond what tracking (on or off) explains.
        [TestCase(10.0, 10.0, 1800.0, 0.0, TestName = "Tracking on for 30 minutes (the run handed tracking back) is not a move by other means")]
        [TestCase(10.0, 17.52, 1800.0, 0.0, TestName = "Tracking off for 30 minutes (the reading drifts) is not a move by other means")]
        [TestCase(10.0, 15.0, 0.0, 5.0, TestName = "A 5 degree slew is a move by other means")]
        [TestCase(359.0, 4.0, 0.0, 5.0, TestName = "A 5 degree slew across 0 h is a move by other means")]
        [TestCase(10.0, 40.0, 1800.0, 22.48, TestName = "A slew with tracking off is a move by other means")]
        public void MovedByOtherMeansDegBetween_does_not_count_tracking(double fromRaDeg, double toRaDeg, double seconds, double expectedDeg) {
            Assert.That(PolarAlignService.MovedByOtherMeansDegBetween(fromRaDeg, toRaDeg, seconds), Is.EqualTo(expectedDeg).Within(0.01));
        }

        // ── the mechanical turn ──────────────────────────────────────────────────────────────

        // from RA, to RA, seconds between → the RA axis's turn (+ east).
        [TestCase(359.0, 1.0, 0.0, 2.0, TestName = "MechanicalTurnDeg wraps east across 0 h")]
        [TestCase(1.0, 359.0, 0.0, -2.0, TestName = "MechanicalTurnDeg wraps west across 0 h")]
        [TestCase(10.0, 10.0, 240.0, -1.0027, TestName = "MechanicalTurnDeg counts tracking as the axis turning west")]
        [TestCase(10.0, 11.0027, 240.0, 0.0, TestName = "MechanicalTurnDeg ignores the reading's drift with tracking off")]
        public void MechanicalTurnDeg_is_the_RA_axis_turn_from_two_readings(double fromRaDeg, double toRaDeg, double seconds, double expectedDeg) {
            Assert.That(PolarAlignService.MechanicalTurnDeg(fromRaDeg, toRaDeg, seconds), Is.EqualTo(expectedDeg).Within(1e-3));
        }
    }
}
