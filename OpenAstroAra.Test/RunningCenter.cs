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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2142 — a real <see cref="CenteringService"/> with a Center under way: its centering solver waits for the next
    /// attempt (the reattempt delay), then re-slews through the mediator, as the ported loop does. A stop ladder runs while
    /// it waits; <see cref="ReleaseNextAttemptAsync"/> lets the attempt go and <see cref="Mediator"/> shows whether it slewed.
    /// </summary>
    internal sealed class RunningCenter : IDisposable {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _nextAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _run;

        public Mock<ITelescopeMediator> Mediator { get; } = new();
        public CenteringService Service { get; }

        private RunningCenter() {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.ExposureTime).Returns(1);
            settings.SetupGet(s => s.Binning).Returns((short)1);
            var profile = new Mock<IProfileService>();
            profile.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            profile.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(800);
            profile.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(3.8);
            Mediator.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<MoveAsker>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var solver = new Mock<ICenteringSolver>();
            solver.Setup(s => s.Center(It.IsAny<CaptureSequence>(), It.IsAny<CenterSolveParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns<CaptureSequence, CenterSolveParameter, IProgress<PlateSolveProgress>?, IProgress<ApplicationStatus>?, CancellationToken>(
                    async (_, parameter, _, _, ct) => {
                        _entered.TrySetResult();
                        await _nextAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
                        await Mediator.Object.SlewToCoordinatesAsync(parameter.Coordinates!, MoveAsker.Session, ct).ConfigureAwait(false);
                        return new PlateSolveResult { Success = true };
                    });
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetCenteringSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(),
                    It.IsAny<IImagingMediator>(), It.IsAny<ITelescopeMediator>(), It.IsAny<IFilterWheelMediator>(),
                    It.IsAny<IDomeMediator>(), It.IsAny<IDomeFollower>()))
                .Returns(solver.Object);
            Service = new CenteringService(profile.Object, factory.Object, Mock.Of<IImagingMediator>(), Mediator.Object,
                Mock.Of<IFilterWheelMediator>(), Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>());
        }

        /// <summary>Starts a Center (as the REST batch job does: no token the ladders know) and waits until it is under way.</summary>
        public static async Task<RunningCenter> StartAsync() {
            var center = new RunningCenter();
            center._run = center.Service.CenterOnTarget(
                new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000), null, null, CancellationToken.None);
            await center._entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return center;
        }

        /// <summary>The next attempt's delay ends; waits for the Center to end, however it ends.</summary>
        public async Task ReleaseNextAttemptAsync() {
            _nextAttempt.TrySetResult();
            try {
                await _run!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // A stopped Center ends cancelled.
            }
        }

        /// <summary>Whether the Center slewed the mount after it started.</summary>
        public bool Slewed() =>
            Mediator.Invocations.Any(i => i.Method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync));

        public void Dispose() {
            ReleaseNextAttemptAsync().GetAwaiter().GetResult();
            Service.Dispose();
        }
    }
}
