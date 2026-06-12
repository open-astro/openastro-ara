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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — <see cref="PlateSolverFactory.GetBlindSolver"/>. ARA ships local solvers only; the cloud
    /// AstrometryNet blind solver was removed. A profile still carrying that setting must resolve to ASTAP
    /// (the shipped backend) rather than a removed/non-functional solver. Asserted by runtime type name since
    /// the concrete solvers are internal to the PlateSolving assembly.
    /// </summary>
    [TestFixture]
    public class PlateSolverFactoryTest {

        private static IPlateSolveSettings Settings(BlindSolver blind) {
            var s = new Mock<IPlateSolveSettings>();
            s.SetupGet(x => x.BlindSolverType).Returns(blind);
            s.SetupGet(x => x.ASTAPLocation).Returns("astap_cli");
            s.SetupGet(x => x.CygwinLocation).Returns(string.Empty);
            return s.Object;
        }

        [Test]
        public void GetBlindSolver_substitutes_ASTAP_for_the_removed_AstrometryNet_blind_solver() {
            var solver = PlateSolverFactory.GetBlindSolver(Settings(BlindSolver.AstrometryNet));
            Assert.That(solver.GetType().Name, Is.EqualTo("ASTAPSolver"));
        }

        [Test]
        public void GetBlindSolver_defaults_to_ASTAP() {
            // The default arm of the switch (any unmapped value) resolves to ASTAP.
            var solver = PlateSolverFactory.GetBlindSolver(Settings((BlindSolver)999));
            Assert.That(solver.GetType().Name, Is.EqualTo("ASTAPSolver"));
        }

        [Test]
        public void GetBlindSolver_still_honors_an_explicitly_configured_local_solver() {
            // Non-removed blind solvers are unchanged — a user who points at a local astrometry.net install
            // still gets it (this PR only fixes the silent AstrometryNet→ASTAP substitution).
            var solver = PlateSolverFactory.GetBlindSolver(Settings(BlindSolver.LOCAL));
            Assert.That(solver.GetType().Name, Is.EqualTo("LocalPlateSolver"));
        }
    }
}
