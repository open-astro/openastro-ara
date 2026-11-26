#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A manual slew while a sequence run is in progress stops guiding and parks the run (2026-10-18:
    /// a Planning GoTo mid-dither, accepted without a word). The endpoint refuses it with 409 and a
    /// sentence unless the caller forces it; a sync never moves the mount.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsSlewTest {
        // #2140 — "a run is active" is a Session holding the mount in Rig Occupancy.
        private static RigOccupancy Runs(bool active) => active ? HeldBy.Mount(RigOccupancy.Session) : new RigOccupancy();

        private static Mock<IPolarAlignService> PolarAlign(bool movingMount) {
            var polarAlign = new Mock<IPolarAlignService>();
            polarAlign.Setup(p => p.IsMovingMount).Returns(movingMount);
            return polarAlign;
        }

        [Test]
        public void A_slew_with_no_run_in_progress_goes_ahead() {
            Assert.That(MoveAdmission.MountHeldRefusal(null, forced: false, MoveKind.Slew), Is.Null);
        }

        [Test]
        public void A_slew_during_a_run_is_refused_with_the_reason_and_the_way_out() {
            var refusal = MoveAdmission.MountHeldRefusal(RigOccupancy.Session, forced: false, MoveKind.Slew);
            Assert.That(refusal, Does.Contain("a sequence is running").And.Contain("stops guiding").And.Contain("force"));
            // #2140 — another holder is named, and the text offers no force: force skips only a sequence.
            Assert.That(MoveAdmission.MountHeldRefusal("centering", forced: false, MoveKind.Slew),
                Does.Contain("centering is running").And.Not.Contain("force"));
        }

        [Test]
        public async Task The_endpoint_answers_409_run_in_progress_during_a_run_and_202_when_forced_or_idle() {
            // #2139 — the wiring through the service seam: the Telescope service asks the sequencer and the endpoint answers
            // the problem title the client keys on; a forced request (and an idle sequencer) reach the mount.
            using (var busy = await FakeMountRig.ConnectAsync(occupancy: Runs(active: true))) {
                var refused = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, busy.Service, CancellationToken.None);
                Assert.That((refused as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
                Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Title, Is.EqualTo("run_in_progress"));
                Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Detail, Does.Contain("force"));
                await Task.Delay(100);
                Assert.That(busy.Fake.Slews, Is.Zero, "a refused slew never reaches the mount");

                var forced = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25, Force: true), "k1", busy.Service, CancellationToken.None);
                Assert.That((forced as Accepted<OperationAcceptedDto>)?.Value?.IdempotencyKey, Is.EqualTo("k1"), "the idempotency key rides through");
                await busy.SlewedAsync();
            }
            using var idle = await FakeMountRig.ConnectAsync(occupancy: Runs(active: false));
            var quiet = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, idle.Service, CancellationToken.None);
            Assert.That(quiet, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await idle.SlewedAsync();
        }

        // #2066 — a GoTo while Polar Align turns the mount would drive the motors twice at once: refused before the mount,
        // force or not. A sync is refused too: it shifts the RA reading the turn steers by, which can reverse the motor.
        [Test]
        public async Task A_goto_or_sync_while_polar_align_turns_the_mount_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(occupancy: Runs(active: false), polarAlign: PolarAlign(movingMount: true).Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.SlewAsync(
                new SlewRequestDto(5.5, -12.25, Force: true), null, rig.Service, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));

            var sync = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.SlewAsync(
                new SlewRequestDto(5.5, -12.25, Sync: true), null, rig.Service, CancellationToken.None, polarAlign: PolarAlign(movingMount: true).Object));
            Assert.That(sync!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
            Assert.That(sync.Message, Does.Contain("the sync was not sent"));
            await Task.Delay(100);
            Assert.That((rig.Fake.Slews, rig.Fake.Syncs), Is.EqualTo((0, 0)));
        }

        // #2066 — Park and Home while Polar Align turns the mount: refused before the mount; accepted once it is still.
        [Test]
        public async Task A_park_while_polar_align_turns_the_mount_is_refused() {
            var polarAlign = PolarAlign(movingMount: true);
            using var rig = await FakeMountRig.ConnectAsync(polarAlign: polarAlign.Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.TelescopeParkAsync(null, null, rig.Service, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
            await Task.Delay(100);
            Assert.That(rig.Fake.Parks, Is.Zero);

            polarAlign.Setup(p => p.IsMovingMount).Returns(false);
            var accepted = await EquipmentEndpoints.TelescopeParkAsync(null, null, rig.Service, CancellationToken.None);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await FakeMountRig.WaitForAsync(() => rig.Fake.Parks > 0, "the park at the mount");
        }

        [Test]
        public async Task A_find_home_while_polar_align_turns_the_mount_is_refused() {
            var polarAlign = PolarAlign(movingMount: true);
            using var rig = await FakeMountRig.ConnectAsync(polarAlign: polarAlign.Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.TelescopeFindHomeAsync(null, rig.Service, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
            await Task.Delay(100);
            Assert.That(rig.Fake.Homes, Is.Zero);

            polarAlign.Setup(p => p.IsMovingMount).Returns(false);
            var accepted = await EquipmentEndpoints.TelescopeFindHomeAsync(null, rig.Service, CancellationToken.None);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await FakeMountRig.WaitForAsync(() => rig.Fake.Homes > 0, "the home at the mount");
        }

        // #2066 (ARA-2) — Joey, 2026-11-20 on #2048: Polar Align holds the mount for its whole run, the adjust loop and its
        // solves included, not only while a motor turns. A GoTo, sync, Park or Home between its turns is refused too.
        [Test]
        public async Task GoTo_Park_and_Home_are_refused_while_Polar_Align_holds_the_mount_between_turns() {
            using var rig = await FakeMountRig.ConnectAsync(occupancy: Runs(active: false), polarAlign: PolarAlign(movingMount: false).Object);
            using var hold = rig.Service.AcquireMotion(MotionOwners.PolarAlign); // solving or adjusting: no motor runs

            var goTo = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.SlewAsync(
                new SlewRequestDto(5.5, -12.25, Force: true), null, rig.Service, CancellationToken.None));
            var park = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.TelescopeParkAsync(null, null, rig.Service, CancellationToken.None));
            var home = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.TelescopeFindHomeAsync(null, rig.Service, CancellationToken.None));

            Assert.That(new[] { goTo!.Cause, park!.Cause, home!.Cause }, Is.All.EqualTo(RefusalCatalog.PolarAlignMovingMount));
            Assert.That(goTo.Message, Does.Contain("Stop polar alignment first"));
            var sync = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.SlewAsync(
                new SlewRequestDto(5.5, -12.25, Sync: true), null, rig.Service, CancellationToken.None));
            Assert.That(sync!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount), "a sync moves the readings the run measures by");
            await Task.Delay(100);
            Assert.That((rig.Fake.Slews, rig.Fake.Parks, rig.Fake.Homes, rig.Fake.Syncs), Is.EqualTo((0, 0, 0, 0)));
        }

        // #2140 (C1) — a holder of the mount other than a sequence (a Center, a polar alignment, End Night) refuses a GoTo
        // even with force; the 409 keeps the title run_in_progress and its text names the holder.
        [Test]
        public async Task A_forced_goto_while_a_center_holds_the_mount_is_refused_naming_it() {
            using var rig = await FakeMountRig.ConnectAsync(occupancy: HeldBy.Mount("centering"));
            var refused = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25, Force: true), null, rig.Service, CancellationToken.None);
            Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Title, Is.EqualTo("run_in_progress"));
            Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Detail, Does.Contain("centering is running"));
            await Task.Delay(100);
            Assert.That(rig.Fake.Slews, Is.Zero);
        }

        // #2140 — record 0025 rule 1: a GoTo asks what holds the mount. Live View holds only the main camera.
        [Test]
        public async Task A_goto_while_live_view_runs_goes_ahead() {
            using var rig = await FakeMountRig.ConnectAsync(occupancy: HeldBy.Camera("Live View"));
            var accepted = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, rig.Service, CancellationToken.None);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await rig.SlewedAsync();
        }

        // #2140 (B1) — a person's park and home are refused while a sequence runs, as the GoTo is; there is no force for them.
        [Test]
        public async Task A_persons_park_and_home_are_refused_while_a_sequence_runs() {
            using var rig = await FakeMountRig.ConnectAsync(occupancy: Runs(active: true));
            var park = await EquipmentEndpoints.TelescopeParkAsync(null, null, rig.Service, CancellationToken.None);
            var home = await EquipmentEndpoints.TelescopeFindHomeAsync(null, rig.Service, CancellationToken.None);
            Assert.That(new[] { (park as ProblemHttpResult)?.ProblemDetails.Title, (home as ProblemHttpResult)?.ProblemDetails.Title },
                Is.All.EqualTo("run_in_progress"));
            Assert.That((park as ProblemHttpResult)?.ProblemDetails.Detail, Does.Contain("stop it before you park"));
            await Task.Delay(100);
            Assert.That((rig.Fake.Parks, rig.Fake.Homes), Is.EqualTo((0, 0)));
        }

        // #2140 (D) — a Find Home from a parked mount is refused (409 mount_parked); a park on a parked mount is not.
        [Test]
        public async Task A_find_home_from_a_parked_mount_is_refused_and_a_park_is_not() {
            using var rig = await FakeMountRig.ConnectAsync(configure: f => f.AtParkScript = () => true);
            await FakeMountRig.WaitForAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.Runtime?.Parked == true, "the parked mount");
            var home = await EquipmentEndpoints.TelescopeFindHomeAsync(null, rig.Service, CancellationToken.None);
            Assert.That((home as ProblemHttpResult)?.ProblemDetails.Title, Is.EqualTo("mount_parked"));
            var park = await EquipmentEndpoints.TelescopeParkAsync(null, null, rig.Service, CancellationToken.None);
            Assert.That(park, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await FakeMountRig.WaitForAsync(() => rig.Fake.Parks == 1, "the park at the mount");
            Assert.That(rig.Fake.Homes, Is.Zero);
        }

        [Test]
        public async Task Confirm_home_answers_204_when_the_mount_reports_home() {
            // #2070 — the user confirmed Home and the rig trusts the position again.
            var telescope = new Mock<ITelescopeService>();
            telescope.Setup(t => t.ConfirmHomeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var result = await EquipmentEndpoints.ConfirmHomeAsync(telescope.Object, CancellationToken.None);

            Assert.That(result, Is.InstanceOf<NoContent>());
            telescope.Verify(t => t.ConfirmHomeAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Confirm_home_answers_409_with_the_sentence_when_the_mount_does_not_report_home() {
            // #2070 — a mount that is not connected or not at Home is the caller's state, not a daemon fault (500).
            var telescope = new Mock<ITelescopeService>();
            telescope.Setup(t => t.ConfirmHomeAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("the mount does not report Home"));

            var result = await EquipmentEndpoints.ConfirmHomeAsync(telescope.Object, CancellationToken.None);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That((result as ProblemHttpResult)?.ProblemDetails.Detail, Is.EqualTo("the mount does not report Home"));
        }

        [Test]
        public void A_forced_slew_and_a_sync_go_ahead_during_a_run() {
            Assert.That(MoveAdmission.MountHeldRefusal(RigOccupancy.Session, forced: true, MoveKind.Slew), Is.Null, "the caller was told and said yes");
            Assert.That(MoveAdmission.MountHeldRefusal("centering", forced: true, MoveKind.Slew), Is.Not.Null, "#2140 — force skips only a sequence");
        }
    }
}
