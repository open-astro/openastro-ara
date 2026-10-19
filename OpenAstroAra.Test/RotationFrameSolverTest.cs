using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1136 bot round 5 — the rotation-assist frame solve builds its PlateSolveParameter from the
    /// profile: the timeout (which used to be stored and never read) and the frame's binning reach it.</summary>
    [TestFixture]
    public class RotationFrameSolverTest {

        [Test]
        public async Task The_rotation_solve_carries_the_profiles_timeout_binning_and_hint() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(store.GetOpticsSettings() with { FocalLengthMm = 448, ReducerFactor = 1, PixelSizeUm = 3.76 });
            store.PutPlateSolveSettings(store.GetPlateSolveSettings() with { TimeoutSeconds = 45, SearchRadiusDeg = 12 });
            var profileService = new HeadlessProfileService();
            PlateSolveParameter? seen = null;
            var imageSolver = new Mock<IImageSolver>();
            imageSolver.Setup(s => s.Solve(It.IsAny<IImageData>(), It.IsAny<PlateSolveParameter>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .Callback<IImageData, PlateSolveParameter, IProgress<ApplicationStatus>?, CancellationToken>((_, p, _, _) => seen = p)
                .ReturnsAsync(new PlateSolveResult { Success = true, PositionAngle = 12.5, Pixscale = 1.73, Coordinates = new Coordinates(Angle.ByHours(21), Angle.ByDegree(44), Epoch.J2000) });
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetImageSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>())).Returns(imageSolver.Object);
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16 * 16], 16, 16, DateTimeOffset.UtcNow));
            var telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetCurrentPosition()).Returns(new Coordinates(Angle.ByHours(20.9), Angle.ByDegree(44.3), Epoch.J2000));
            var sut = new RotationFrameSolver(profileService, store, factory.Object, frames.Object, telescope.Object);

            var solve = await sut.SolvePositionAngleAsync(2.0, 2, CancellationToken.None);

            Assert.That(solve, Is.Not.Null);
            Assert.That(solve!.PositionAngleDeg, Is.EqualTo(12.5));
            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.Timeout, Is.EqualTo(TimeSpan.FromSeconds(45)), "the profile's timeout reaches the solver");
            Assert.That(seen.Binning, Is.EqualTo(2));
            Assert.That(seen.SearchRadius, Is.EqualTo(12));
            Assert.That(seen.FocalLength, Is.EqualTo(448));
            Assert.That(seen.Coordinates!.RA, Is.EqualTo(20.9).Within(1e-6), "the mount's position is the hint");
        }
    }
}
