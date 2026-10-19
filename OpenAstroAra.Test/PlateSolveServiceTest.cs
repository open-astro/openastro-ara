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
        public void A_solved_scale_far_from_the_profile_rewrites_the_focal_length_reducer_factored_out() {
            // #1136 — profile says 530 mm with a 0.8x reducer (424 mm effective); the sky says 1.0"/px at
            // 3.76 µm, i.e. 775 mm effective → the native focal length becomes 775 / 0.8 ≈ 969 mm.
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(store.GetOpticsSettings() with { FocalLengthMm = 530, ReducerFactor = 0.8, PixelSizeUm = 3.76 });
            var sut = new PlateSolveService(Mock.Of<IProfileService>(), Mock.Of<IPlateSolverFactory>(), store);
            var parameter = new PlateSolveParameter { FocalLength = 424, PixelSize = 3.76, Binning = 1 };

            sut.LearnFocalLength(parameter, new PlateSolveResult { Success = true, Pixscale = 1.0 });

            Assert.That(store.GetOpticsSettings().FocalLengthMm, Is.EqualTo(969.5).Within(0.5));
            Assert.That(store.GetOpticsSettings().ReducerFactor, Is.EqualTo(0.8), "the reducer entry is the user's, untouched");
        }

        [Test]
        public void A_solved_scale_within_tolerance_leaves_the_profile_alone() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(store.GetOpticsSettings() with { FocalLengthMm = 530, ReducerFactor = 1, PixelSizeUm = 3.76 });
            var sut = new PlateSolveService(Mock.Of<IProfileService>(), Mock.Of<IPlateSolverFactory>(), store);
            var parameter = new PlateSolveParameter { FocalLength = 530, PixelSize = 3.76, Binning = 1 };
            var scale = AstroUtil.ArcsecPerPixel(3.76, 530);

            sut.LearnFocalLength(parameter, new PlateSolveResult { Success = true, Pixscale = scale * 1.04 });

            Assert.That(store.GetOpticsSettings().FocalLengthMm, Is.EqualTo(530));
        }

        [Test]
        public void Binning_comes_from_the_frame_metadata() {
            var meta = new OpenAstroAra.Image.ImageData.ImageMetaData();
            meta.Camera.BinX = 2;
            var image = new Mock<IImageData>();
            image.SetupGet(i => i.MetaData).Returns(meta);
            Assert.That(PlateSolveService.BinningOf(image.Object), Is.EqualTo(2));
            Assert.That(PlateSolveService.BinningOf(Mock.Of<IImageData>()), Is.EqualTo(1));
        }

        [Test]
        public async Task SolveImage_builds_parameter_from_profile_and_returns_the_solver_result() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.SearchRadius).Returns(15);
            settings.SetupGet(s => s.Regions).Returns(2);
            settings.SetupGet(s => s.DownSampleFactor).Returns(2);
            settings.SetupGet(s => s.MaxObjects).Returns(500);
            settings.SetupGet(s => s.SolveTimeoutSeconds).Returns(45);

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
            Assert.That(captured.Binning, Is.EqualTo(1)); // the frame carries no binning → native scale
            Assert.That(captured.Timeout, Is.EqualTo(TimeSpan.FromSeconds(45))); // #1136 — the store's timeout reaches the solver
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

        // ── ARA-store optics precedence (the dual-profile-systems fix): the user configures
        // optics in the ARA profile's §36 section; the legacy Equipment-layer profile stays
        // NaN forever, and the solve must not read it when the ARA section is set. ──

        [Test]
        public void Solve_geometry_prefers_the_ara_optics_with_reducer_folded_in() {
            var optics = new OpenAstroAra.Server.Contracts.OpticsSettingsDto(
                FocalLengthMm: 448, ReducerFactor: 0.8, SensorWidthPx: 0, SensorHeightPx: 0,
                PixelSizeUm: 3.76, ApertureMm: 91);
            var (focal, pixel) = PlateSolveService.ResolveSolveGeometry(optics, double.NaN, 3.8);
            Assert.That(focal, Is.EqualTo(448 * 0.8).Within(1e-9),
                "the effective focal length folds the reducer in");
            Assert.That(pixel, Is.EqualTo(3.76).Within(1e-9));
        }

        [Test]
        public void Solve_geometry_falls_back_to_legacy_values_per_field() {
            // Unset ARA optics (0 = unset) → legacy values field-by-field; null store → all legacy.
            var unset = new OpenAstroAra.Server.Contracts.OpticsSettingsDto(
                FocalLengthMm: 0, ReducerFactor: 1, SensorWidthPx: 0, SensorHeightPx: 0,
                PixelSizeUm: 0, ApertureMm: 0);
            var (focal, pixel) = PlateSolveService.ResolveSolveGeometry(unset, 750, 2.4);
            Assert.That(focal, Is.EqualTo(750));
            Assert.That(pixel, Is.EqualTo(2.4));
            var (nf, np) = PlateSolveService.ResolveSolveGeometry(null, 750, 2.4);
            Assert.That((nf, np), Is.EqualTo((750.0, 2.4)));
        }
    }
}
