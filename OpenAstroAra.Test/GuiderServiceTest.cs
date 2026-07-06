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
            new(new HeadlessProfileService(), NewRecovery(), NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

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
        public void RmsArcsec_scales_by_pixel_scale_and_never_fakes_a_zero() {
            // A 0.5 px RMS at 1.62 ″/px (a typical guide scope) reads 0.81″.
            Assert.That(GuiderService.RmsArcsec(0.5, 1.62), Is.EqualTo(0.81).Within(1e-9));
            Assert.That(GuiderService.RmsArcsec(null, 1.62), Is.Null, "no pixel RMS → no arcsec");
            Assert.That(GuiderService.RmsArcsec(0.5, 0), Is.Null,
                "PHD2 hasn't reported a scale yet — unknown, never a fake 0 arcsec");
            Assert.That(GuiderService.RmsArcsec(0.5, -1), Is.Null);
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

        // ── §63.6 guider-e-4b-2: dark-library build dispatch ──

        [Test]
        public void BuildDarkLibraryAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            // A valid request still can't be accepted with no guider connected.
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None); });
        }

        [Test]
        public void BuildDarkLibraryAsync_validates_before_the_connection_check() {
            using var svc = NewService();
            // Validation runs first, so a bad request surfaces as ArgumentException even while disconnected
            // (a 400, not a 409) — the endpoint relies on this ordering.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 0), null, CancellationToken.None); });
            Assert.Throws<ArgumentException>(
                () => { _ = svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(MinExposureMs: 5000, MaxExposureMs: 1000), null, CancellationToken.None); });
        }

        [Test]
        public async System.Threading.Tasks.Task GetCalibrationFilesStatusAsync_returns_null_before_connect() {
            using var svc = NewService();
            Assert.That(await svc.GetCalibrationFilesStatusAsync(CancellationToken.None), Is.Null);
        }

        // ── §63.6 guider-e-4c-b-2: defect-map build dispatch (mirrors the dark-library dispatch) ──

        [Test]
        public void BuildDefectMapDarksAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.BuildDefectMapDarksAsync(new BuildDefectMapDarksRequestDto(), null, CancellationToken.None); });
        }

        [Test]
        public void SetDarkLibraryEnabledAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.SetDarkLibraryEnabledAsync(true, CancellationToken.None); });
        }

        [Test]
        public void SetDefectMapEnabledAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = NewService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.SetDefectMapEnabledAsync(false, CancellationToken.None); });
        }

        [Test]
        public void BuildDefectMapDarksAsync_validates_before_the_connection_check() {
            using var svc = NewService();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.BuildDefectMapDarksAsync(new BuildDefectMapDarksRequestDto(FrameCount: 0), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.BuildDefectMapDarksAsync(new BuildDefectMapDarksRequestDto(ExposureMs: 0), null, CancellationToken.None); });
        }

        // The concurrent-build gate sits behind RequireConnectedGuider (needs a live daemon), so its decision is
        // factored into a pure helper that's tested here directly — the 409 / idempotent-202 / start branches.
        private const string DarkOp = "guider.dark_library.build";
        private const string DefectOp = "guider.defect_map.build";

        [Test]
        public void ResolveBuildAdmission_starts_when_no_build_in_flight() {
            Assert.That(GuiderService.ResolveBuildAdmission(false, inFlightKey: "k1", inFlightOp: DarkOp, requestKey: "k2", requestOp: DefectOp),
                Is.EqualTo(GuiderService.BuildAdmission.Start));
        }

        [Test]
        public void ResolveBuildAdmission_idempotent_accepts_same_non_null_key_and_same_op() {
            Assert.That(GuiderService.ResolveBuildAdmission(true, inFlightKey: "k1", inFlightOp: DarkOp, requestKey: "k1", requestOp: DarkOp),
                Is.EqualTo(GuiderService.BuildAdmission.IdempotentAccept));
        }

        [Test]
        public void ResolveBuildAdmission_rejects_different_key_keyless_or_cross_op_concurrent_build() {
            Assert.That(GuiderService.ResolveBuildAdmission(true, inFlightKey: "k1", inFlightOp: DarkOp, requestKey: "k2", requestOp: DarkOp),
                Is.EqualTo(GuiderService.BuildAdmission.Reject));
            // A null request key must never collapse onto an in-flight null key as "idempotent".
            Assert.That(GuiderService.ResolveBuildAdmission(true, inFlightKey: null, inFlightOp: DarkOp, requestKey: null, requestOp: DarkOp),
                Is.EqualTo(GuiderService.BuildAdmission.Reject));
            // Same key but a DIFFERENT op must reject — else the caller gets a 202 for an op whose WS events
            // (the in-flight op's) never fire for it, and it waits forever.
            Assert.That(GuiderService.ResolveBuildAdmission(true, inFlightKey: "k1", inFlightOp: DarkOp, requestKey: "k1", requestOp: DefectOp),
                Is.EqualTo(GuiderService.BuildAdmission.Reject));
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
