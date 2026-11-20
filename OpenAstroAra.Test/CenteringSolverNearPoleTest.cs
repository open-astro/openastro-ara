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
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2075 — near the pole, the centering re-slew (target + offset) can pass +90°, because
    /// <c>Coordinates.operator +</c> does not fold. The re-slew now folds it to the same sky point, so
    /// the mediator's range guard does not refuse it and the mount gets a valid Dec.</summary>
    [TestFixture]
    public class CenteringSolverNearPoleTest {

        [Test]
        public async Task A_near_pole_offset_reslew_is_folded_to_a_valid_dec() {
            // Target Dec 89.9°. The mount reads 89.95° and the solve finds 89.65°, so with sync off the
            // offset is +0.3° and the raw re-slew is Dec 90.2° at RA 0h: the same point as (12h, 89.8°).
            var target = new Coordinates(Angle.ByHours(0.0), Angle.ByDegree(89.9), Epoch.JNOW);
            var position = new Coordinates(Angle.ByHours(0.0), Angle.ByDegree(89.95), Epoch.JNOW);
            var solved = new Coordinates(Angle.ByHours(0.0), Angle.ByDegree(89.65), Epoch.JNOW);
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetCurrentPosition()).Returns(position);
            var slews = new List<Coordinates>();
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((c, _) => slews.Add(c))
                .ReturnsAsync(true);
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo());
            var dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo());
            var captureSolver = new Mock<ICaptureSolver>();
            captureSolver.SetupSequence(c => c.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = solved })
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = target });
            var solver = new CenteringSolver(Mock.Of<IPlateSolver>(), Mock.Of<IPlateSolver>(), Mock.Of<IImagingMediator>(),
                mount.Object, Mock.Of<IFilterWheelMediator>(), dome.Object, Mock.Of<IDomeFollower>()) {
                CaptureSolver = captureSolver.Object,
            };
            var parameter = new CenterSolveParameter { Coordinates = target, Threshold = 1.0, Attempts = 1, NoSync = true };

            var result = await solver.Center(new CaptureSequence(), parameter, null, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(slews, Has.Count.EqualTo(1));
            Assert.That(Math.Abs(slews[0].Dec), Is.LessThanOrEqualTo(90.0), "the mount refuses a Dec past ±90°");
            Assert.That(slews[0].Dec, Is.EqualTo(89.8).Within(1e-6));
            Assert.That(slews[0].RA, Is.EqualTo(12.0).Within(1e-6), "folded over the pole: RA + 12h");
        }
    }
}
