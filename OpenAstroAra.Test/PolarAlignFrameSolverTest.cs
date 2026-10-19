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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§45.4 — the real frame solver hands the capture binning to the plate solver, so a
    /// binned guide-camera frame is searched at its true scale (pixel size × binning).</summary>
    [TestFixture]
    [Category("IO")] // writes a small FITS for the loader
    public class PolarAlignFrameSolverTest {

        private string _dir = null!;

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), "ara-pa-solver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private static (PolarAlignFrameSolver Sut, Func<PlateSolveParameter?> Seen) NewSolver() {
            var store = new InMemoryProfileStore();
            store.PutPhd2Settings(store.GetPhd2Settings() with { GuideFocalLength = 200, GuidePixelSize = 3.75 });
            PlateSolveParameter? seen = null;
            var imageSolver = new Mock<IImageSolver>();
            imageSolver.Setup(s => s.Solve(It.IsAny<IImageData>(), It.IsAny<PlateSolveParameter>(),
                    It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .Callback<IImageData, PlateSolveParameter, IProgress<ApplicationStatus>?, CancellationToken>((_, p, _, _) => seen = p)
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetImageSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>())).Returns(imageSolver.Object);
            return (new PolarAlignFrameSolver(new HeadlessProfileService(), store, factory.Object), () => seen);
        }

        private string WriteFrame() {
            var path = Path.Combine(_dir, "live-1.fits");
            using (var fits = OpenAstroAra.Fits.FitsImage.Create(path, 8, 8, OpenAstroAra.Fits.FitsBitDepth.UnsignedShort)) {
                fits.WriteImageData(new ushort[64]);
                fits.Complete();
            }
            return path;
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task The_plate_solver_is_told_the_frames_binning_and_scale(int binning) {
            var (sut, seen) = NewSolver();

            await sut.SolveAsync(WriteFrame(), binning, null, null, CancellationToken.None).ConfigureAwait(false);

            var p = seen();
            Assert.That(p, Is.Not.Null, "the image solver was called");
            Assert.That(p!.Binning, Is.EqualTo(binning));
            Assert.That(p.FocalLength, Is.EqualTo(200), "the guide optics, not the main scope");
            Assert.That(p.PixelSize, Is.EqualTo(3.75 * binning).Within(1e-9),
                "PlateSolveParameter scales the pixel size by the binning — the number the solver's field of view is built from");
        }

        [TestCase(0)]
        [TestCase(-1)]
        public async Task A_binning_below_one_is_refused(int binning) {
            var (sut, _) = NewSolver();
            await Assert.ThatAsync(
                () => sut.SolveAsync(WriteFrame(), binning, null, null, CancellationToken.None),
                Throws.TypeOf<ArgumentOutOfRangeException>()).ConfigureAwait(false);
        }
    }
}
