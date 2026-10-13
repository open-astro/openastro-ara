#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§57.4 Verify-Position: one snapshot, solved, beside the mount's own figures.</summary>
    [TestFixture]
    public class VerifyPositionServiceTest {

        private static Coordinates C(double raHours, double decDeg) => new(Angle.ByHours(raHours), Angle.ByDegree(decDeg), Epoch.J2000);

        [Test]
        public void Describe_reports_the_solve_beside_the_mount_and_the_intended_target() {
            var solved = new PlateSolveResult { Success = true, Coordinates = C(9.93, 69.07) };
            var dto = VerifyPositionService.Describe(solved, mount: C(9.93, 69.57), intended: C(9.93, 69.07), exposure: 2);
            Assert.That(dto.Success, Is.True);
            Assert.That(dto.SolvedRaHours, Is.EqualTo(9.93).Within(1e-9));
            Assert.That(dto.MountDecDegrees, Is.EqualTo(69.57).Within(1e-9));
            Assert.That(dto.IntendedDecDegrees, Is.EqualTo(69.07).Within(1e-9));
            Assert.That(dto.SolvedToMountArcmin, Is.EqualTo(30.0).Within(0.05), "half a degree of Dec between solve and mount");
            Assert.That(dto.SolvedToIntendedArcmin, Is.EqualTo(0).Within(0.05));
            Assert.That(dto.Note, Is.Null);
            Assert.That(dto.ExposureSeconds, Is.EqualTo(2));
        }

        [Test]
        public void Describe_on_a_failed_solve_keeps_the_mount_figures_and_says_why() {
            var dto = VerifyPositionService.Describe(new PlateSolveResult { Success = false }, mount: C(1, 2), intended: null, exposure: 2);
            Assert.That(dto.Success, Is.False);
            Assert.That(dto.SolvedRaHours, Is.Null);
            Assert.That(dto.MountRaHours, Is.EqualTo(1).Within(1e-9));
            Assert.That(dto.IntendedRaHours, Is.Null);
            Assert.That(dto.SolvedToMountArcmin, Is.Null);
            Assert.That(dto.Note, Does.Contain("solve failed"));
            var noMount = VerifyPositionService.Describe(new PlateSolveResult { Success = true, Coordinates = null }, null, null, 2);
            Assert.That(noMount.Success, Is.False, "success without coordinates is a failure");
            Assert.That(noMount.Note, Does.Contain("not connected"));
        }

        [Test]
        public void Exposure_defaults_to_two_seconds_and_is_clamped() {
            Assert.That(VerifyPositionService.ClampExposure(null), Is.EqualTo(2.0));
            Assert.That(VerifyPositionService.ClampExposure(double.NaN), Is.EqualTo(2.0));
            Assert.That(VerifyPositionService.ClampExposure(0.01), Is.EqualTo(0.1));
            Assert.That(VerifyPositionService.ClampExposure(120), Is.EqualTo(30));
            Assert.That(VerifyPositionService.ClampExposure(1.5), Is.EqualTo(1.5));
        }

        [Test]
        public async Task Verify_takes_one_snapshot_with_the_mount_position_as_hint_and_never_slews() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.ExposureTime).Returns(5);
            settings.SetupGet(s => s.Gain).Returns(120);
            settings.SetupGet(s => s.Binning).Returns((short)2);
            settings.SetupGet(s => s.SearchRadius).Returns(15);
            settings.SetupGet(s => s.Regions).Returns(2);
            settings.SetupGet(s => s.DownSampleFactor).Returns(2);
            settings.SetupGet(s => s.MaxObjects).Returns(500);
            settings.SetupGet(s => s.NumberOfAttempts).Returns(3);
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(800);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);

            var mountInfo = new TelescopeInfo { Connected = true, Coordinates = C(9.93, 69.57), TargetCoordinates = C(9.93, 69.07) };
            var telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(mountInfo);

            CaptureSequence? seq = null;
            CaptureSolverParameter? param = null;
            var captureSolver = new Mock<ICaptureSolver>();
            captureSolver.Setup(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<CaptureSequence, CaptureSolverParameter, IProgress<PlateSolveProgress>?, IProgress<ApplicationStatus>?, CancellationToken>(
                    (s, p, _, _, _) => { seq = s; param = p; })
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = C(9.93, 69.07) });
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetCaptureSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(), It.IsAny<IImagingMediator>(), It.IsAny<IFilterWheelMediator>()))
                .Returns(captureSolver.Object);

            using var svc = new VerifyPositionService(profileService.Object, factory.Object, Mock.Of<IImagingMediator>(), telescope.Object, Mock.Of<IFilterWheelMediator>());
            var dto = await svc.VerifyAsync(null, CancellationToken.None);

            Assert.That(seq!.ExposureTime, Is.EqualTo(2.0), "the quick 2 s look, not the profile's 5 s solve exposure");
            Assert.That(seq.ImageType, Is.EqualTo(ImageTypes.SNAPSHOT), "never catalogued");
            Assert.That(seq.Binning.X, Is.EqualTo(2));
            Assert.That(param!.Attempts, Is.EqualTo(1), "one look");
            Assert.That(param.Coordinates!.Dec, Is.EqualTo(69.57).Within(1e-9), "the hint is where the mount thinks it is");
            Assert.That(param.FocalLength, Is.EqualTo(800));
            Assert.That(dto.Success, Is.True);
            Assert.That(dto.SolvedToMountArcmin, Is.EqualTo(30).Within(0.05));
            Assert.That(dto.SolvedToIntendedArcmin, Is.EqualTo(0).Within(0.05));
            factory.Verify(f => f.GetCenteringSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(), It.IsAny<IImagingMediator>(),
                It.IsAny<ITelescopeMediator>(), It.IsAny<IFilterWheelMediator>(), It.IsAny<IDomeMediator>(), It.IsAny<IDomeFollower>()), Times.Never,
                "verify only looks: no centering, no sync, no slew");
        }

        [Test]
        public async Task Verify_without_optics_is_a_configuration_error() {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(Mock.Of<IPlateSolveSettings>());
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(0);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);
            using var svc = new VerifyPositionService(profileService.Object, Mock.Of<IPlateSolverFactory>(), Mock.Of<IImagingMediator>(),
                Mock.Of<ITelescopeMediator>(), Mock.Of<IFilterWheelMediator>());
            await Assert.ThatAsync(() => svc.VerifyAsync(2, CancellationToken.None), Throws.TypeOf<PlateSolverConfigurationException>());
        }

        [Test]
        public async Task Endpoint_maps_configuration_to_422_and_no_camera_to_409() {
            var ok = new Mock<IVerifyPositionService>();
            ok.Setup(v => v.VerifyAsync(1.5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VerifyPositionResultDto(true, 1, 2, 1, 2, null, null, 0, null, 1.5, null));
            var result = await PlateSolveEndpoints.VerifyPositionAsync(new VerifyPositionRequestDto(1.5), ok.Object, CancellationToken.None);
            Assert.That(result, Is.InstanceOf<Ok<VerifyPositionResultDto>>());
            Assert.That(((Ok<VerifyPositionResultDto>)result).Value!.ExposureSeconds, Is.EqualTo(1.5));

            var misconfigured = new Mock<IVerifyPositionService>();
            misconfigured.Setup(v => v.VerifyAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PlateSolverConfigurationException("no optics"));
            var problem = await PlateSolveEndpoints.VerifyPositionAsync(null, misconfigured.Object, CancellationToken.None);
            Assert.That(((ProblemHttpResult)problem).StatusCode, Is.EqualTo(StatusCodes.Status422UnprocessableEntity));

            var noCamera = new Mock<IVerifyPositionService>();
            noCamera.Setup(v => v.VerifyAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("No camera connected"));
            var conflict = await PlateSolveEndpoints.VerifyPositionAsync(null, noCamera.Object, CancellationToken.None);
            Assert.That(((ProblemHttpResult)conflict).StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
        }
    }
}
