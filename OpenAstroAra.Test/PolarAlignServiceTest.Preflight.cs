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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Test {

    /// <summary>#2068 (ARA-18) — the pre-flight list the Start card shows is the one a start refuses on.</summary>
    public partial class PolarAlignServiceTest {

        // #2083 — the start reads the mount type and CanMoveAxis(Primary) the telescope service read at connect.
        [Test]
        public async Task Start_refuses_an_alt_az_mount_and_nothing_moves() {
            var fake = NewFake();
            var mount = NewMount();
            var service = mount.As<ITelescopeService>();
            service.Setup(t => t.MountAlignmentMode).Returns(ASCOM.Common.DeviceInterfaces.AlignmentMode.AltAz);
            service.Setup(t => t.ReadNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => FreshReading(0.0, 85.0));
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignAltAzMount));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsNotIn(0.0)), Times.Never, "nothing moved");
        }

        [Test]
        public async Task Start_refuses_a_mount_that_cannot_move_its_RA_axis_and_nothing_moves() {
            var fake = NewFake();
            var mount = NewMount();
            var service = mount.As<ITelescopeService>();
            service.Setup(t => t.CanMovePrimaryAxis).Returns(false);
            service.Setup(t => t.ReadNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => FreshReading(0.0, 85.0));
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignCannotMoveAxis));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsNotIn(0.0)), Times.Never, "nothing moved");
        }

        // #2165 — the clearance is confirmed for the reach the user saw. A bigger turn size picked on the Start card after
        // that turns further, so a motor start asks again; going back to the smaller one is still confirmed.
        [Test]
        public async Task A_bigger_turn_size_after_the_confirmation_refuses_a_motor_start_and_nothing_moves() {
            var fake = NewFake();
            var mount = NewMount();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, MountClearance = null });
            using var svc = NewService(fake, new ScriptedSolver(), mount, store: store);
            var seen = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(seen.MountKey!, seen.Planned.ReachDeg));

            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 15.0 });
            var bigger = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 5.0 });
            var smaller = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);

            string StatusOf(PolarAlignPreflightDto dto) => dto.Checks.Single(c => c.Id == PolarAlignPreflightChecks.ClearanceConfirmed).Status;
            Assert.Multiple(() => {
                Assert.That(bigger.Planned.ReachDeg, Is.GreaterThan(seen.Planned.ReachDeg));
                Assert.That(StatusOf(bigger), Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
                Assert.That(refusal.Parameters["reach_deg"], Is.EqualTo(bigger.Planned.ReachDeg));
                Assert.That(StatusOf(smaller), Is.EqualTo(PolarAlignPreflightStatuses.Ok), "a smaller reach stays confirmed");
                Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            });
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, It.IsNotIn(0.0)), Times.Never, "nothing moved");
        }

        // #2165 — a Check turns by the Check step, set apart from the turn size: a confirmation for the fine run's reach
        // does not cover a bigger Check step, and the refusal carries the Check's own reach for the app to confirm.
        [Test]
        public async Task A_Check_start_with_a_bigger_Check_step_is_refused_with_the_Checks_own_reach() {
            var fake = NewFake();
            var mount = NewMount();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 20.0, MountClearance = null });
            using var svc = NewService(fake, new ScriptedSolver(), mount, store: store);
            var fine = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(fine.MountKey!, fine.Planned.ReachDeg));

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
                Assert.That(refusal.Parameters["reach_deg"], Is.EqualTo(fine.Planned.ReachDeg + 20.0), "two 20° Check turns, not two 10° turns");
                Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            });
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, It.IsNotIn(0.0)), Times.Never, "nothing moved");
        }

        [Test]
        public async Task Preflight_lists_every_check_and_Start_refuses_on_a_fail() {
            var fake = NewFake();
            var mount = NewMount();
            var store = NewStore();
            // A profile from before #2068: no mount's clearance is confirmed.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            using var svc = NewService(fake, new ScriptedSolver(), mount, store: store);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(preflight.Checks.Select(c => c.Id), Is.EqualTo(PolarAlignPreflightChecks.All), "every check, in the order a start makes them");
            string StatusOf(PolarAlignPreflightDto dto, string id) => dto.Checks.Single(c => c.Id == id).Status;
            Assert.Multiple(() => {
                Assert.That(preflight.Ready, Is.False);
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.ClearanceConfirmed), Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.MountConnected), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.StartPointing), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.MountLimits), Is.EqualTo(PolarAlignPreflightStatuses.Warn));
                Assert.That(preflight.MountKey, Is.EqualTo(PolarAlignPreflightEvaluator.UnidentifiedMountKey));
                Assert.That(preflight.Planned.Direction, Is.Not.Null, "the plan the next Start would confirm");
                Assert.That(preflight.Planned.ReachDeg, Is.EqualTo(preflight.Planned.MaxSweepDeg),
                    "the bench store has a saved RA motor direction: the run's turns, no test");
            });

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), "start-plan refuses on the same check");
            foreach (var stage in new[] { PolarAlignStages.Fine, PolarAlignStages.Coarse, PolarAlignStages.Check }) {
                var stageRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(stage, CancellationToken.None));
                Assert.That(stageRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), $"the {stage} plan refuses too");
            }
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(preflight.Planned.Direction!, preflight.Planned.NewOrigin ?? true), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, It.IsNotIn(0.0)), Times.Never, "nothing moved");

            // The user confirms the clearance (the app's dialog writes the mount's entry): the list is ready and Start goes.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(preflight.MountKey!, preflight.Planned.ReachDeg));
            var ready = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(ready.Ready, Is.True, string.Join("; ", ready.Checks.Where(c => c.Status == PolarAlignPreflightStatuses.Fail).Select(c => c.Detail)));
            Assert.That(StatusOf(ready, PolarAlignPreflightChecks.ClearanceConfirmed), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(PaCalls(fake), Is.Not.Empty, "the start went ahead and took the lease");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // ── the adjust screen's Check (EndAdjustForCheckAsync) ───────────────────────────────

        /// <summary>A refused Check leaves the adjust loop as it was: still adjusting, still holding the mount, no stop
        /// sent and no turn made since <paramref name="eventsBefore"/> / <paramref name="turnsBefore"/>.</summary>
        private static async Task AssertLoopRunsOnAsync(PolarAlignService svc, FakeGemMount mount, WsRecorder ws, int eventsBefore, int turnsBefore) {
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Multiple(() => {
                Assert.That(status.State, Is.EqualTo("adjusting"), "the loop runs on");
                Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the loop still holds the mount");
                Assert.That(ws.Events.Skip(eventsBefore).Select(e => e.Type),
                    Has.None.EqualTo(WsEventCatalog.PolarAlignStopped).And.None.EqualTo(WsEventCatalog.PolarAlignStarted), "the loop was not ended");
                Assert.That(RaTurns(mount), Has.Length.EqualTo(turnsBefore), "nothing turned. " + mount.Describe());
            });
        }

        [Test]
        public async Task An_adjust_screen_motor_Check_without_the_clearance_is_refused_and_the_loop_runs_on() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var check = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            // The user takes the confirmation back (Settings › Take back) while the loop runs, then taps Check.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            var (eventsBefore, turnsBefore) = (ws.Events.Count, RaTurns(mount).Length);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(check.Direction, check.NewOrigin), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), "a motor Check is refused");
            await AssertLoopRunsOnAsync(svc, mount, ws, eventsBefore, turnsBefore).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public async Task An_adjust_screen_Check_with_the_position_unverified_is_refused_before_the_loop_ends() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var check = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            // The mount's position stops being trusted while the loop runs (a reconnect that read a false zero).
            mount.PositionUnverifiedReason = MountPoseTrust.NotReadYet;
            var (eventsBefore, turnsBefore) = (ws.Events.Count, RaTurns(mount).Length);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(check.Direction, check.NewOrigin), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            await AssertLoopRunsOnAsync(svc, mount, ws, eventsBefore, turnsBefore).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // ── the motion owner's rank in the list ──────────────────────────────────────────────

        [Test]
        public async Task Another_motion_owner_refuses_a_motor_start_after_the_position_check_and_before_the_clearance() {
            // DESIGN.md / API_CONTRACT: polar-align-mount-busy ranks after the checks up to pose_verified and before
            // clearance_confirmed.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store);
            // The plan is read while the clearance stands (an unconfirmed one refuses the plan too), then taken back.
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            using var sequencer = mount.AcquireMotion("sequencer");
            Task Start() => svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);

            var busy = await Assert.ThrowsAsync<RefusalException>(Start);
            mount.PositionUnverifiedReason = MountPoseTrust.NotReadYet;
            var pose = await Assert.ThrowsAsync<RefusalException>(Start);

            Assert.Multiple(() => {
                Assert.That(busy!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountBusy), "the owner ranks before the clearance");
                Assert.That(pose!.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified), "the position ranks before the owner");
                Assert.That(mount.MotionOwner, Is.EqualTo("sequencer"), "the other owner keeps the mount");
                Assert.That(mount.Motions, Is.Empty, "nothing moved. " + mount.Describe());
            });
        }
    }
}
