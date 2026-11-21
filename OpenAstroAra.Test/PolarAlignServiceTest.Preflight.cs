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
                Assert.That(preflight.Planned.MaxCwAngleDeg, Is.EqualTo(PolarAlignSettingsClamp.DefaultMaxCwAngleDeg));
            });

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), "start-plan refuses on the same check");
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(preflight.Planned.Direction!, preflight.Planned.NewOrigin ?? true), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, It.IsNotIn(0.0)), Times.Never, "nothing moved");

            // The user confirms the clearance (the app's dialog writes the mount's entry): the list is ready and Start goes.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(preflight.MountKey!));
            var ready = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(ready.Ready, Is.True, string.Join("; ", ready.Checks.Where(c => c.Status == PolarAlignPreflightStatuses.Fail).Select(c => c.Detail)));
            Assert.That(StatusOf(ready, PolarAlignPreflightChecks.ClearanceConfirmed), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(PaCalls(fake), Is.Not.Empty, "the start went ahead and took the lease");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // Joey, 2026-11-21: a Turn RA by hand start sends the mount no move (#2083), so an unconfirmed clearance only warns
        // for it; every motor start (the plan, Start, the adjust screen's Check, Rough first) is still refused.
        [Test]
        public async Task A_hand_turn_start_goes_ahead_without_the_clearance_and_a_motor_start_is_still_refused() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0, tracking: true, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            using var svc = NewGemService(mount, ws, store);
            string StatusOf(PolarAlignPreflightDto dto) => dto.Checks.Single(c => c.Id == PolarAlignPreflightChecks.ClearanceConfirmed).Status;

            var motorList = await svc.GetPreflightAsync(null, handTurn: false, CancellationToken.None).ConfigureAwait(false);
            var handList = await svc.GetPreflightAsync(null, handTurn: true, CancellationToken.None).ConfigureAwait(false);
            Assert.Multiple(() => {
                Assert.That(StatusOf(motorList), Is.EqualTo(PolarAlignPreflightStatuses.Fail), "a motor start refuses on it");
                Assert.That(motorList.Ready, Is.False);
                Assert.That(StatusOf(handList), Is.EqualTo(PolarAlignPreflightStatuses.Warn), "a hand-turn start only warns");
                Assert.That(handList.Ready, Is.True, string.Join("; ", handList.Checks.Where(c => c.Status == PolarAlignPreflightStatuses.Fail).Select(c => c.Detail)));
            });

            foreach (var stage in new[] { PolarAlignStages.Fine, PolarAlignStages.Coarse, PolarAlignStages.Check }) {
                var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(stage, handTurn: false, CancellationToken.None));
                Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), $"a motor {stage} plan is refused");
            }
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, handTurn: true, CancellationToken.None).ConfigureAwait(false);
            var motorStart = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None));
            Assert.That(motorStart!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), "a motor Start is refused");

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, HandTurn: true), null,
                CancellationToken.None).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.HandTurn, Is.True, "the hand-turn run started");
            Assert.That(mount.Motions, Is.Empty, "and Ara moved nothing. " + mount.Describe());
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

        [Test]
        public async Task An_adjust_screen_hand_turn_Check_without_the_clearance_goes_ahead() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, ws, store);
            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            var checkFrom = ws.Events.Count;

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, handTurn: true, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Check, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, HandTurn: true), null,
                CancellationToken.None).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, from: checkFrom).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, from: checkFrom).ConfigureAwait(false);
            var status = await PollStateAsync(svc, checkFrom, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            Assert.That(status.HandTurn, Is.True, "the hand-turn check ran");
            Assert.That(mount.Motions, Is.Empty, "and Ara moved nothing. " + mount.Describe());
        }

        // ── the motion owner's rank in the list ──────────────────────────────────────────────

        [Test]
        public async Task Another_motion_owner_refuses_a_motor_start_after_the_position_check_and_before_the_clearance() {
            // DESIGN.md / API_CONTRACT: polar-align-mount-busy ranks after the checks up to pose_verified and before
            // clearance_confirmed.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            using var svc = NewGemService(mount, ws, store);
            // The motor plan is refused on the clearance; the hand-turn plan gives the direction to confirm.
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, handTurn: true, CancellationToken.None).ConfigureAwait(false);
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
