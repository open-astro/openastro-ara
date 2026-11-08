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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 coarse stage — the real <see cref="MainCameraPointingSolver"/> behind the rough step, where
    /// <c>PolarAlignServiceTest</c> drives a fake. The preflight (<c>UnavailableReasonAsync</c>) is what lets a
    /// coarse Start refuse before the mount homes and slews; the live loop's one near look is what keeps a
    /// failed rough frame from a minutes-long blind search.
    /// </summary>
    [TestFixture]
    public class MainCameraPointingSolverTest {

        private static Mock<IProfileService> ProfileWithOptics(double focalLength, double pixelSize) {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(Mock.Of<IPlateSolveSettings>());
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(focalLength);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(pixelSize);
            return profileService;
        }

        private static Mock<ICameraService> Camera(EquipmentConnectionState state, bool free) {
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CameraDto("cam", "Main camera", state, null, null!));
            camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(free);
            return camera;
        }

        private static MainCameraPointingSolver Solver(Mock<IProfileService> profileService, ICameraService? camera, IPlateSolverFactory? factory = null) =>
            new(profileService.Object, factory ?? new Mock<IPlateSolverFactory>(MockBehavior.Strict).Object, Mock.Of<IImagingMediator>(),
                Mock.Of<ITelescopeMediator>(), Mock.Of<IFilterWheelMediator>(), profileStore: null, camera: camera);

        [Test]
        public async Task Preflight_names_a_camera_that_is_not_connected() {
            var camera = Camera(EquipmentConnectionState.Disconnected, free: true);
            var solver = Solver(ProfileWithOptics(800, 3.8), camera.Object);

            var reason = await solver.UnavailableReasonAsync(CancellationToken.None);

            Assert.That(reason, Does.Contain("not connected"));
            Assert.That(reason, Does.Contain("fine stage"), "the way out is named");
        }

        [Test]
        public async Task Preflight_names_a_camera_another_exposure_holds() {
            var camera = Camera(EquipmentConnectionState.Connected, free: false);
            var solver = Solver(ProfileWithOptics(800, 3.8), camera.Object);

            var reason = await solver.UnavailableReasonAsync(CancellationToken.None);

            Assert.That(reason, Does.Contain("busy"));
        }

        [TestCase(0.0, 3.8)]
        [TestCase(800.0, 0.0)]
        public async Task Preflight_names_unset_optics_before_it_asks_the_camera(double focalLength, double pixelSize) {
            // Strict: the optics check comes first, so a camera service is never consulted for a rig that
            // cannot solve anyway.
            var camera = new Mock<ICameraService>(MockBehavior.Strict);
            var solver = Solver(ProfileWithOptics(focalLength, pixelSize), camera.Object);

            var reason = await solver.UnavailableReasonAsync(CancellationToken.None);

            Assert.That(reason, Does.Contain("focal length"));
            Assert.That(reason, Does.Contain("Optics"), "the setting to change is named");
        }

        [Test]
        public async Task Preflight_is_clear_with_optics_set_and_a_free_connected_camera() {
            var camera = Camera(EquipmentConnectionState.Connected, free: true);
            var solver = Solver(ProfileWithOptics(800, 3.8), camera.Object);

            Assert.That(await solver.UnavailableReasonAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Preflight_without_a_camera_service_has_nothing_to_refuse_on() {
            var solver = Solver(ProfileWithOptics(800, 3.8), camera: null);

            Assert.That(await solver.UnavailableReasonAsync(CancellationToken.None), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task A_frame_passes_the_callers_blind_failover_to_both_search_flags(bool blindFailover) {
            // The live loop asks for one near look (false): no staged widening and no blind solver, so a
            // failed rough frame costs one exposure, not minutes (#1136). A seed frame keeps both (true).
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.Binning).Returns((short)1);
            settings.SetupGet(s => s.SearchRadius).Returns(15);
            settings.SetupGet(s => s.SolveTimeoutSeconds).Returns(30);
            var profileService = ProfileWithOptics(800, 3.8);
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);

            CaptureSolverParameter? param = null;
            var captureSolver = new Mock<ICaptureSolver>();
            captureSolver.Setup(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<CaptureSequence, CaptureSolverParameter, IProgress<PlateSolveProgress>?, IProgress<ApplicationStatus>?, CancellationToken>(
                    (_, p, _, _, _) => param = p)
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = new Coordinates(45.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees) });
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetCaptureSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(), It.IsAny<IImagingMediator>(), It.IsAny<IFilterWheelMediator>()))
                .Returns(captureSolver.Object);
            var solver = Solver(profileService, Camera(EquipmentConnectionState.Connected, free: true).Object, factory.Object);

            var outcome = await solver.CaptureAndSolveAsync(3.0, (45.0, 85.0), blindFailover, CancellationToken.None);

            Assert.That(outcome.Success, Is.True);
            Assert.That(param, Is.Not.Null);
            Assert.That(param!.BlindFailoverEnabled, Is.EqualTo(blindFailover));
            Assert.That(param.StagedSearchEnabled, Is.EqualTo(blindFailover));
            Assert.That(param.Attempts, Is.EqualTo(1), "one look; the caller decides what to do next");
            // The hint reaches the solver in its own epoch (J2000); the caller's apparent Dec 85° is ~84.89° there.
            var expectedHint = new Coordinates(45.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees).Transform(Epoch.J2000);
            Assert.That(param.Coordinates!.Dec, Is.EqualTo(expectedHint.Dec).Within(1e-6), "the caller's hint, not the mount's reading");
        }
    }
}
