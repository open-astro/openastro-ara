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
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28 — <see cref="PlateSolveService"/>. Verifies the service assembles a <see cref="PlateSolveParameter"/>
    /// from the active profile and delegates to the factory-built <see cref="IImageSolver"/> (no real solver
    /// backend touched).
    /// </summary>
    [TestFixture]
    public class PlateSolveServiceTest {

        [Test]
        public async Task SolveImage_builds_parameter_from_profile_and_returns_the_solver_result() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.SearchRadius).Returns(15);
            settings.SetupGet(s => s.Regions).Returns(2);
            settings.SetupGet(s => s.DownSampleFactor).Returns(2);
            settings.SetupGet(s => s.MaxObjects).Returns(500);

            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(800);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);

            var expected = new PlateSolveResult { Success = true };
            PlateSolveParameter? captured = null;
            var imageSolver = new Mock<IImageSolver>();
            imageSolver
                .Setup(s => s.Solve(It.IsAny<IImageData>(), It.IsAny<PlateSolveParameter>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<IImageData, PlateSolveParameter, IProgress<ApplicationStatus>?, CancellationToken>((_, p, _, _) => captured = p)
                .ReturnsAsync(expected);

            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetImageSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>())).Returns(imageSolver.Object);

            var sut = new PlateSolveService(profileService.Object, factory.Object);
            var coords = new Coordinates(Angle.ByHours(3), Angle.ByDegree(10), Epoch.J2000);

            var result = await sut.SolveImage(Mock.Of<IImageData>(), coords, null, CancellationToken.None);

            Assert.That(result, Is.SameAs(expected));
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.FocalLength, Is.EqualTo(800));
            Assert.That(captured.PixelSize, Is.EqualTo(3.8));
            Assert.That(captured.SearchRadius, Is.EqualTo(15));
            Assert.That(captured.Regions, Is.EqualTo(2));
            Assert.That(captured.DownSampleFactor, Is.EqualTo(2));
            Assert.That(captured.MaxObjects, Is.EqualTo(500));
            Assert.That(captured.Binning, Is.EqualTo(1)); // set for a reason (solve at native scale), not a profile mirror
            // Seeds a near (non-blind) solve — the parameter clones/normalizes, so compare values not identity.
            Assert.That(captured.Coordinates, Is.Not.Null);
            Assert.That(captured.Coordinates!.RA, Is.EqualTo(coords.RA).Within(1e-6));
            Assert.That(captured.Coordinates.Dec, Is.EqualTo(coords.Dec).Within(1e-6));
        }

        [Test]
        public void SolveImage_throws_on_a_null_image() {
            var sut = new PlateSolveService(new Mock<IProfileService>().Object, new Mock<IPlateSolverFactory>().Object);
            Assert.Throws<ArgumentNullException>(
                () => sut.SolveImage(null!, null, null, CancellationToken.None));
        }

        [Test]
        public void SolveImage_throws_when_no_active_profile_is_loaded() {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns((IProfile)null!);
            var sut = new PlateSolveService(profileService.Object, new Mock<IPlateSolverFactory>().Object);
            Assert.Throws<PlateSolverConfigurationException>(
                () => sut.SolveImage(Mock.Of<IImageData>(), null, null, CancellationToken.None));
        }

        // A fresh profile leaves focal length unset (NaN) and either value can be 0 → degenerate FOV; fail fast.
        [Test]
        [TestCase(0.0, 3.8)]                       // focal length zero
        [TestCase(double.NaN, 3.8)]                // focal length NaN (the default)
        [TestCase(800.0, 0.0)]                     // pixel size zero
        [TestCase(800.0, double.NaN)]              // pixel size NaN
        public void SolveImage_throws_when_focal_length_or_pixel_size_is_unconfigured(double focalLength, double pixelSize) {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(new Mock<IPlateSolveSettings>().Object);
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(focalLength);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(pixelSize);
            var sut = new PlateSolveService(profileService.Object, new Mock<IPlateSolverFactory>().Object);
            Assert.Throws<PlateSolverConfigurationException>(
                () => sut.SolveImage(Mock.Of<IImageData>(), null, null, CancellationToken.None));
        }
    }
}
