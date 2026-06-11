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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free coverage for the §63 guider-a <see cref="GuiderService"/>: the not-connected and
    /// post-dispose contracts that don't need a live PHD2 server. The live connect→guide→disconnect
    /// path is exercised by an integration test against a real openastro-phd2 instance.
    /// </summary>
    [TestFixture]
    public class GuiderServiceTest {

        private static GuiderService NewService() =>
            new(new HeadlessProfileService(), NewRecovery(), NullLogger<GuiderService>.Instance);

        // These tests never trigger a connection drop, so recovery is wired but never runs — inert
        // mocks suffice. The recovery decision tree itself is covered by GuiderRecoveryCoordinatorTest.
        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        [Test]
        public async System.Threading.Tasks.Task GetAsync_returns_null_before_connect() {
            using var svc = NewService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public void StartGuidingAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.StartGuidingAsync(null, CancellationToken.None); });
        }

        [Test]
        public void StopGuidingAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.StopGuidingAsync(null, CancellationToken.None); });
        }

        [Test]
        public void DitherAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.DitherAsync(3.0, null, CancellationToken.None); });
        }

        [Test]
        public async System.Threading.Tasks.Task DisconnectAsync_is_an_accepted_no_op_when_not_connected() {
            using var svc = NewService();
            var accepted = await svc.DisconnectAsync("idem-1", CancellationToken.None);
            Assert.That(accepted.OperationType, Is.EqualTo("guider.disconnect"));
            Assert.That(accepted.IdempotencyKey, Is.EqualTo("idem-1"));
        }

        [Test]
        public void ComputeRms_returns_null_for_empty_window() {
            var (total, ra, dec) = GuiderService.ComputeRms(System.Array.Empty<(double, double)>());
            Assert.That(total, Is.Null);
            Assert.That(ra, Is.Null);
            Assert.That(dec, Is.Null);
        }

        [Test]
        public void ComputeRms_is_root_mean_square_over_the_window() {
            // RA errors {3,-3} → rmsRa=3; Dec errors {4,-4} → rmsDec=4; total=sqrt((9+9+16+16)/2)=5.
            var steps = new[] { (3.0, 4.0), (-3.0, -4.0) };
            var (total, ra, dec) = GuiderService.ComputeRms(steps);
            Assert.That(ra!.Value, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(dec!.Value, Is.EqualTo(4.0).Within(1e-9));
            Assert.That(total!.Value, Is.EqualTo(5.0).Within(1e-9));
        }

        [Test]
        public void Mediator_GetInfo_reports_disconnected_before_connect() {
            using var svc = NewService();
            // GuiderService also serves IGuiderMediator (§63 guider-c); GetInfo is the mediator member.
            Assert.That(svc.GetInfo().Connected, Is.False);
        }

        [Test]
        public async System.Threading.Tasks.Task Mediator_guide_ops_are_noop_false_when_not_connected() {
            using var svc = NewService();
            // Unlike the REST StartGuidingAsync (which throws), the mediator path returns false so the
            // sequencer's attempt policy handles a not-connected guider gracefully.
            Assert.That(await svc.StartGuiding(false, null!, CancellationToken.None), Is.False);
            Assert.That(await svc.StopGuiding(CancellationToken.None), Is.False);
            Assert.That(await svc.Dither(CancellationToken.None), Is.False);
        }

        [Test]
        public void Ops_after_Dispose_throw_ObjectDisposed() {
            var svc = NewService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.GetAsync(CancellationToken.None); });
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new GuiderConnectRequestDto(), null, CancellationToken.None); });
        }
    }
}
