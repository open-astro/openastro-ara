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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §58.4 — <see cref="MeridianFlipExecutor"/>. Verifies the post-flip recovery orchestration (stop guiding →
    /// pass meridian → flip slew → recenter → resume guiding → settle → side-of-pier) over fully mocked
    /// mediators + a mocked centering service. No real equipment is touched; SettleTime + timeToFlip are 0 so
    /// the wait loops are skipped and the tests run instantly.
    /// </summary>
    [TestFixture]
    public class MeridianFlipExecutorTest {

        private Mock<IProfileService> profileService = null!;
        private Mock<ITelescopeMediator> telescope = null!;
        private Mock<IGuiderMediator> guider = null!;
        private Mock<ICenteringService> centering = null!;
        private Mock<IDomeMediator> dome = null!;
        private Mock<IDomeFollower> domeFollower = null!;
        private List<string> callLog = null!;

        private static readonly Coordinates Target = new(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);

        private static readonly string[] FullSequenceWithGuider = {
            "StopGuiding",
            "SetTracking(False)",
            "SetTracking(True)",
            "Flip",
            "Recenter",
            "AutoSelectGuideStar",
            "StartGuiding",
        };

        private void SetupProfile(bool recenter = true, bool autoFocus = false, int settleTime = 0) {
            var settings = new Mock<IMeridianFlipSettings>();
            settings.SetupGet(s => s.Recenter).Returns(recenter);
            settings.SetupGet(s => s.AutoFocusAfterFlip).Returns(autoFocus);
            settings.SetupGet(s => s.SettleTime).Returns(settleTime);
            profileService.SetupGet(p => p.ActiveProfile.MeridianFlipSettings).Returns(settings.Object);
        }

        [SetUp]
        public void SetUp() {
            callLog = new List<string>();
            profileService = new Mock<IProfileService>();

            telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo { Connected = true, SideOfPier = PierSide.pierEast });
            telescope.Setup(t => t.SetTrackingEnabled(It.IsAny<bool>()))
                .Callback<bool>(on => callLog.Add($"SetTracking({on})")).Returns(true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("Flip")).ReturnsAsync(true);

            guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("StopGuiding")).ReturnsAsync(true);
            guider.Setup(g => g.AutoSelectGuideStar(It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("AutoSelectGuideStar")).ReturnsAsync(true);
            guider.Setup(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("StartGuiding")).ReturnsAsync(true);

            centering = new Mock<ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("Recenter")).ReturnsAsync(new PlateSolveResult { Success = true });

            dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = false });
            domeFollower = new Mock<IDomeFollower>();
        }

        private MeridianFlipExecutor CreateSUT() =>
            new(profileService.Object, telescope.Object, guider.Object, centering.Object, dome.Object, domeFollower.Object);

        private static IProgress<ApplicationStatus> Progress => new Progress<ApplicationStatus>();

        [Test]
        public async Task MeridianFlip_runs_the_full_recovery_sequence_in_order_when_a_guider_is_connected() {
            SetupProfile(recenter: true);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // Stop guiding → pass meridian (tracking off then on) → flip → recenter → select+resume guiding.
            Assert.That(callLog, Is.EqualTo(FullSequenceWithGuider));
        }

        [Test]
        public async Task MeridianFlip_skips_all_guider_steps_when_no_guider_is_connected() {
            SetupProfile(recenter: true);
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = false });
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.AutoSelectGuideStar(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            // The flip + recenter still run.
            Assert.That(callLog, Does.Contain("Flip"));
            Assert.That(callLog, Does.Contain("Recenter"));
        }

        [Test]
        public async Task MeridianFlip_does_not_recenter_when_recenter_is_disabled() {
            SetupProfile(recenter: false);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task MeridianFlip_succeeds_even_when_the_recenter_solve_fails() {
            SetupProfile(recenter: true);
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSUT();

            // Recenter is best-effort — a failed solve logs + continues, the flip still succeeds.
            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
        }

        [Test]
        public async Task MeridianFlip_does_not_throw_when_autofocus_after_flip_is_enabled() {
            SetupProfile(recenter: true, autoFocus: true);
            var sut = CreateSUT();

            // The live AF sweep is unbuilt; the executor logs the skip rather than failing.
            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
        }

        [Test]
        public async Task MeridianFlip_fails_and_restores_tracking_when_the_mount_reports_the_flip_did_not_succeed() {
            SetupProfile(recenter: true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            // No recenter once the flip failed.
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            // Failure path re-enables tracking + resumes the guider.
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.AtLeastOnce);
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task MeridianFlip_fails_and_restores_tracking_when_the_flip_slew_throws() {
            SetupProfile(recenter: true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("mount fault"));
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.AtLeastOnce);
        }

        [Test]
        public void MeridianFlip_propagates_cancellation_and_restores_tracking() {
            SetupProfile(recenter: true);
            using var cts = new CancellationTokenSource();
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => cts.Cancel())
                .ThrowsAsync(new OperationCanceledException());
            var sut = CreateSUT();

            Assert.ThrowsAsync<OperationCanceledException>(
                () => sut.MeridianFlip(Target, TimeSpan.Zero, Progress, cts.Token));
            // Cancellation still restores tracking before propagating.
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.AtLeastOnce);
        }

        [Test]
        public void MeridianFlip_throws_on_a_null_target() {
            SetupProfile();
            var sut = CreateSUT();
            Assert.ThrowsAsync<ArgumentNullException>(
                () => sut.MeridianFlip(null!, TimeSpan.Zero, Progress, CancellationToken.None));
        }

        [Test]
        public async Task MeridianFlip_synchronizes_a_connected_following_dome_after_the_flip() {
            SetupProfile(recenter: false);
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetAzimuth = true });
            domeFollower.SetupGet(d => d.IsFollowing).Returns(true);
            domeFollower.Setup(d => d.WaitForDomeSynchronization(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask).Verifiable();
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            domeFollower.Verify(d => d.WaitForDomeSynchronization(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
