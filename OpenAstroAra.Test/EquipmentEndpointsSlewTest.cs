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
        private static readonly IReadOnlyList<Guid> None = Array.Empty<Guid>();
        private static readonly IReadOnlyList<Guid> One = [Guid.NewGuid()];

        private static Mock<ISequencerService> Runs(IReadOnlyList<Guid> runs) {
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(runs);
            return sequencer;
        }

        private static Mock<IPolarAlignService> PolarAlign(bool movingMount) {
            var polarAlign = new Mock<IPolarAlignService>();
            polarAlign.Setup(p => p.IsMovingMount).Returns(movingMount);
            return polarAlign;
        }

        [Test]
        public void A_slew_with_no_run_in_progress_goes_ahead() {
            Assert.That(MoveAdmission.RunActiveRefusal(new SlewRequestDto(5.5, -12.25), 0), Is.Null);
        }

        [Test]
        public void A_slew_during_a_run_is_refused_with_the_reason_and_the_way_out() {
            var refusal = MoveAdmission.RunActiveRefusal(new SlewRequestDto(5.5, -12.25), 1);
            Assert.That(refusal, Does.Contain("run is in progress").And.Contain("stops guiding").And.Contain("force"));
            Assert.That(MoveAdmission.RunActiveRefusal(new SlewRequestDto(5.5, -12.25), 2), Does.Contain("2 sequence runs"));
        }

        [Test]
        public async Task The_endpoint_answers_409_run_in_progress_during_a_run_and_202_when_forced_or_idle() {
            // #2139 — the wiring through the service seam: the Telescope service asks the sequencer and the endpoint answers
            // the problem title the client keys on; a forced request (and an idle sequencer) reach the mount.
            using (var busy = await FakeMountRig.ConnectAsync(sequencer: Runs(One).Object)) {
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
            using var idle = await FakeMountRig.ConnectAsync(sequencer: Runs(None).Object);
            var quiet = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, idle.Service, CancellationToken.None);
            Assert.That(quiet, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            await idle.SlewedAsync();
        }

        // #2066 — a GoTo while Polar Align turns the mount would drive the motors twice at once: refused before the mount,
        // force or not. A sync is refused too: it shifts the RA reading the turn steers by, which can reverse the motor.
        [Test]
        public async Task A_goto_or_sync_while_polar_align_turns_the_mount_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(sequencer: Runs(None).Object, polarAlign: PolarAlign(movingMount: true).Object);

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
            using var rig = await FakeMountRig.ConnectAsync(sequencer: Runs(None).Object, polarAlign: PolarAlign(movingMount: false).Object);
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
            Assert.That(MoveAdmission.RunActiveRefusal(new SlewRequestDto(5.5, -12.25, Force: true), 1), Is.Null, "the caller was told and said yes");
            Assert.That(MoveAdmission.RunActiveRefusal(new SlewRequestDto(5.5, -12.25, Sync: true), 1), Is.Null, "a sync does not move the mount");
        }
    }
}
