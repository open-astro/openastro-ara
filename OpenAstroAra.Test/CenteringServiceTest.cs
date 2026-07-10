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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28 — <see cref="CenteringService"/>. Verifies it assembles the <see cref="CenterSolveParameter"/> +
    /// capture sequence from the active profile and delegates to the factory-built centering solver (no real
    /// equipment or solver touched).
    /// </summary>
    [TestFixture]
    public class CenteringServiceTest {

        private static CenteringService CreateSUT(IProfileService profileService, IPlateSolverFactory factory) =>
            new(profileService, factory, Mock.Of<IImagingMediator>(), Mock.Of<ITelescopeMediator>(),
                Mock.Of<IFilterWheelMediator>(), Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>());

        [Test]
        public async Task CenterOnTarget_builds_param_from_profile_and_delegates_to_the_centering_solver() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.ExposureTime).Returns(5);
            settings.SetupGet(s => s.Gain).Returns(120);
            settings.SetupGet(s => s.Binning).Returns((short)1);
            settings.SetupGet(s => s.Threshold).Returns(2.0);
            settings.SetupGet(s => s.SearchRadius).Returns(15);
            settings.SetupGet(s => s.Regions).Returns(2);
            settings.SetupGet(s => s.DownSampleFactor).Returns(2);
            settings.SetupGet(s => s.MaxObjects).Returns(500);
            settings.SetupGet(s => s.NumberOfAttempts).Returns(3);
            settings.SetupGet(s => s.ReattemptDelay).Returns(1.0);

            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(800);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);

            var expected = new PlateSolveResult { Success = true };
            CenterSolveParameter? captured = null;
            CaptureSequence? capturedSeq = null;
            var centeringSolver = new Mock<ICenteringSolver>();
            centeringSolver
                .Setup(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<CaptureSequence, CenterSolveParameter, IProgress<PlateSolveProgress>?, IProgress<ApplicationStatus>?, CancellationToken>(
                    (seq, p, _, _, _) => { capturedSeq = seq; captured = p; })
                .ReturnsAsync(expected);

            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetCenteringSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(),
                    It.IsAny<IImagingMediator>(), It.IsAny<ITelescopeMediator>(), It.IsAny<IFilterWheelMediator>(),
                    It.IsAny<IDomeMediator>(), It.IsAny<IDomeFollower>()))
                .Returns(centeringSolver.Object);

            using var sut = CreateSUT(profileService.Object, factory.Object);
            var target = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);

            var result = await sut.CenterOnTarget(target, null, null, CancellationToken.None);

            Assert.That(result, Is.SameAs(expected));
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.FocalLength, Is.EqualTo(800));
            Assert.That(captured.PixelSize, Is.EqualTo(3.8));
            Assert.That(captured.Threshold, Is.EqualTo(2.0));
            Assert.That(captured.Attempts, Is.EqualTo(3));
            Assert.That(captured.ReattemptDelay, Is.EqualTo(TimeSpan.FromMinutes(1))); // ReattemptDelay is minutes
            Assert.That(captured.Regions, Is.EqualTo(2));
            Assert.That(captured.DownSampleFactor, Is.EqualTo(2));
            Assert.That(captured.MaxObjects, Is.EqualTo(500));
            Assert.That(captured.Coordinates!.RA, Is.EqualTo(5).Within(1e-6)); // target threaded through
            // The solve exposure is built from the profile too.
            Assert.That(capturedSeq, Is.Not.Null);
            Assert.That(capturedSeq!.ExposureTime, Is.EqualTo(5));
            Assert.That(capturedSeq.Gain, Is.EqualTo(120));
            Assert.That(capturedSeq.Binning.X, Is.EqualTo(1));
        }

        [Test]
        public void CenterOnTarget_throws_on_a_null_target() {
            using var sut = CreateSUT(new Mock<IProfileService>().Object, new Mock<IPlateSolverFactory>().Object);
            Assert.ThrowsAsync<ArgumentNullException>(
                () => sut.CenterOnTarget(null!, null, null, CancellationToken.None));
        }

        [Test]
        public void CenterOnTarget_throws_when_no_active_profile_is_loaded() {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns((IProfile)null!);
            using var sut = CreateSUT(profileService.Object, new Mock<IPlateSolverFactory>().Object);
            var target = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            Assert.ThrowsAsync<PlateSolverConfigurationException>(
                () => sut.CenterOnTarget(target, null, null, CancellationToken.None));
        }

        [Test]
        [TestCase(0.0, 3.8)]          // focal length zero
        [TestCase(double.NaN, 3.8)]   // focal length NaN (default)
        [TestCase(800.0, 0.0)]        // pixel size zero
        [TestCase(800.0, double.NaN)] // pixel size NaN
        public void CenterOnTarget_throws_when_focal_length_or_pixel_size_is_unconfigured(double focalLength, double pixelSize) {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(new Mock<IPlateSolveSettings>().Object);
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(focalLength);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(pixelSize);
            using var sut = CreateSUT(profileService.Object, new Mock<IPlateSolverFactory>().Object);
            var target = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            Assert.ThrowsAsync<PlateSolverConfigurationException>(
                () => sut.CenterOnTarget(target, null, null, CancellationToken.None));
        }

        // ─── §38 CenterAndRotateAsync — the rotate-then-centre executor path ───

        private static readonly Coordinates Target = new(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private sealed class RotateHarness {
            public Mock<IProfileService> ProfileService { get; } = new();
            public Mock<IPlateSolverFactory> Factory { get; } = new();
            public Mock<ICaptureSolver> CaptureSolver { get; } = new();
            public Mock<ICenteringSolver> CenteringSolver { get; } = new();
            public Mock<IRotatorMediator> Rotator { get; } = new();

            // solvedAngles yields each rotation solve's PositionAngle in turn (the last repeats).
            public RotateHarness(double rotationTolerance, params double[] solvedAngles) {
                var settings = new Mock<IPlateSolveSettings>();
                settings.SetupGet(s => s.ExposureTime).Returns(5);
                settings.SetupGet(s => s.Binning).Returns((short)1);
                settings.SetupGet(s => s.NumberOfAttempts).Returns(1); // deliberately low — must NOT bound rotation moves
                settings.SetupGet(s => s.RotationTolerance).Returns(rotationTolerance);
                ProfileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
                ProfileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(800);
                ProfileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);

                var call = 0;
                CaptureSolver
                    .Setup(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                        It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new PlateSolveResult {
                        Success = true,
                        PositionAngle = solvedAngles[Math.Min(call++, solvedAngles.Length - 1)],
                    });
                CenteringSolver
                    .Setup(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(),
                        It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PlateSolveResult { Success = true });

                Factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
                Factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
                Factory.Setup(f => f.GetCaptureSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(),
                        It.IsAny<IImagingMediator>(), It.IsAny<IFilterWheelMediator>()))
                    .Returns(CaptureSolver.Object);
                Factory.Setup(f => f.GetCenteringSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(),
                        It.IsAny<IImagingMediator>(), It.IsAny<ITelescopeMediator>(), It.IsAny<IFilterWheelMediator>(),
                        It.IsAny<IDomeMediator>(), It.IsAny<IDomeFollower>()))
                    .Returns(CenteringSolver.Object);
            }

            public CenteringService CreateSUT() =>
                new(ProfileService.Object, Factory.Object, Mock.Of<IImagingMediator>(), Mock.Of<ITelescopeMediator>(),
                    Mock.Of<IFilterWheelMediator>(), Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>(), Rotator.Object);
        }

        [Test]
        public async Task CenterAndRotate_already_at_angle_syncs_but_never_moves_then_centres() {
            var h = new RotateHarness(rotationTolerance: 0.5, solvedAngles: 137.6);
            using var sut = h.CreateSUT();

            var ok = await sut.CenterAndRotateAsync(Target, 137.5, NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            h.Rotator.Verify(r => r.Sync(137.6f), Times.Once, "the solved angle feeds the rotator's sky frame");
            h.Rotator.Verify(r => r.MoveRelative(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
            h.CenteringSolver.Verify(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(),
                It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Once, "the centre half still runs");
        }

        [Test]
        public async Task CenterAndRotate_moves_by_the_folded_delta_and_verifies_before_centering() {
            // Solved 350°, target 10° → the short way across zero is +20°, not -340°.
            var h = new RotateHarness(rotationTolerance: 0.5, 350.0, 10.0);
            using var sut = h.CreateSUT();

            var ok = await sut.CenterAndRotateAsync(Target, 10.0, NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            h.Rotator.Verify(r => r.MoveRelative(It.Is<float>(d => Math.Abs(d - 20f) < 1e-3), It.IsAny<CancellationToken>()),
                Times.Once);
            h.CaptureSolver.Verify(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2), "the move is verified by a fresh solve before centering");
        }

        [Test]
        public async Task CenterAndRotate_that_never_converges_fails_without_centering_bounded_independently_of_solve_retries() {
            // Every solve reads 90° off target; NumberOfAttempts is 1 in the harness — the
            // rotation loop's own bound (10 moves) must apply, not the solve-retry setting.
            var h = new RotateHarness(rotationTolerance: 0.5, solvedAngles: 90.0);
            using var sut = h.CreateSUT();

            var ok = await sut.CenterAndRotateAsync(Target, 0.0, NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            h.Rotator.Verify(r => r.MoveRelative(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Exactly(10),
                "exactly MaxRotationMoves moves, the last solve verify-only");
            h.CenteringSolver.Verify(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(),
                It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never, "an un-rotated field must not be centred and reported converged");
        }

        [Test]
        public async Task CenterAndRotate_fails_when_the_rotation_solve_fails() {
            var h = new RotateHarness(rotationTolerance: 0.5, solvedAngles: 0.0);
            h.CaptureSolver
                .Setup(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            using var sut = h.CreateSUT();

            var ok = await sut.CenterAndRotateAsync(Target, 0.0, NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            h.Rotator.Verify(r => r.MoveRelative(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never,
                "an unverifiable position angle must never drive a move");
        }

        [Test]
        public void CenterAndRotate_throws_when_no_rotator_is_wired() {
            using var sut = CreateSUT(new Mock<IProfileService>().Object, new Mock<IPlateSolverFactory>().Object);
            Assert.ThrowsAsync<PlateSolverConfigurationException>(
                () => sut.CenterAndRotateAsync(Target, 0.0, NoProgress, CancellationToken.None));
        }
    }
}
