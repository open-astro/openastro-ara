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
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1222 — the centering loop's sync carries the loop's own token, so a sequence Stop
    /// during the sync's equatorial-system read is observed. The mock answers only the two-argument
    /// overload: the old one-argument call would not reach it.</summary>
    [TestFixture]
    public class CenteringSolverSyncTokenTest {

        [Test]
        public async Task Center_syncs_with_its_own_token() {
            var target = new Coordinates(6.0, 45.0, Epoch.J2000, Coordinates.RAType.Hours);
            var farOff = new Coordinates(6.5, 45.0, Epoch.J2000, Coordinates.RAType.Hours);
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetCurrentPosition()).Returns(farOff);
            using var cts = new CancellationTokenSource();
            CancellationToken seen = default;
            mount.Setup(m => m.Sync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((_, token) => seen = token)
                .ThrowsAsync(new OperationCanceledException("a Stop during the sync's system read"));
            var captureSolver = new Mock<ICaptureSolver>();
            captureSolver.Setup(c => c.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = farOff });
            var solver = new CenteringSolver(Mock.Of<IPlateSolver>(), Mock.Of<IPlateSolver>(), Mock.Of<IImagingMediator>(),
                mount.Object, Mock.Of<IFilterWheelMediator>(), Mock.Of<IDomeMediator>(), Mock.Of<IDomeFollower>()) {
                CaptureSolver = captureSolver.Object,
            };
            var parameter = new CenterSolveParameter { Coordinates = target, Threshold = 1.0, Attempts = 1 };

            await Assert.ThatAsync(() => solver.Center(new CaptureSequence(), parameter, null, null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            mount.Verify(m => m.Sync(It.IsAny<Coordinates>(), cts.Token), Times.Once, "the sync is called with the centering loop's token");
            Assert.That(seen, Is.EqualTo(cts.Token));
            mount.Verify(m => m.Sync(It.IsAny<Coordinates>()), Times.Never, "not the token-less overload");
        }
    }
}
