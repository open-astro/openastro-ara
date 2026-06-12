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
    /// §18.I — <see cref="PlateSolverFactory"/>. ARA ships local solvers only; the cloud AstrometryNet solver
    /// was removed. A profile still carrying that setting — on the primary (<c>PlateSolverType</c>) path or the
    /// blind (<c>BlindSolverType</c>) path — must resolve to ASTAP (the shipped backend) rather than a removed/
    /// non-functional solver. Asserted by runtime type name (<c>GetType().Name</c>) because the concrete solver
    /// classes (<c>ASTAPSolver</c>, <c>LocalPlateSolver</c>) are <c>internal</c> to the PlateSolving assembly —
    /// so a rename of those classes won't break compilation here; update these literals if you rename them.
    /// </summary>
    [TestFixture]
    public class PlateSolverFactoryTest {

        private static IPlateSolveSettings Settings(BlindSolver blind = BlindSolver.ASTAP, PlateSolver primary = PlateSolver.ASTAP) {
            var s = new Mock<IPlateSolveSettings>();
            s.SetupGet(x => x.BlindSolverType).Returns(blind);
            s.SetupGet(x => x.PlateSolverType).Returns(primary);
            s.SetupGet(x => x.ASTAPLocation).Returns("astap_cli");
            s.SetupGet(x => x.CygwinLocation).Returns(string.Empty);
            return s.Object;
        }

        [Test]
        public void GetBlindSolver_substitutes_ASTAP_for_the_removed_AstrometryNet_blind_solver() {
            var solver = PlateSolverFactory.GetBlindSolver(Settings(blind: BlindSolver.AstrometryNet));
            Assert.That(solver.GetType().Name, Is.EqualTo("ASTAPSolver"));
        }

        [Test]
        public void GetPlateSolver_substitutes_ASTAP_for_the_removed_AstrometryNet_primary_solver() {
            // The same substitution on the primary path (PlateSolverType), which funnels through the same
            // private switch arm as the blind path.
            var solver = PlateSolverFactory.GetPlateSolver(Settings(primary: PlateSolver.AstrometryNet));
            Assert.That(solver.GetType().Name, Is.EqualTo("ASTAPSolver"));
        }

        [Test]
        public void GetBlindSolver_defaults_to_ASTAP() {
            // The default arm of the switch (any unmapped value) resolves to ASTAP.
            var solver = PlateSolverFactory.GetBlindSolver(Settings(blind: (BlindSolver)999));
            Assert.That(solver.GetType().Name, Is.EqualTo("ASTAPSolver"));
        }

        [Test]
        public void GetBlindSolver_still_honors_an_explicitly_configured_local_solver() {
            // Non-removed blind solvers are unchanged — a user who points at a local astrometry.net install
            // still gets it (this PR only fixes the silent AstrometryNet→ASTAP substitution).
            var solver = PlateSolverFactory.GetBlindSolver(Settings(blind: BlindSolver.LOCAL));
            Assert.That(solver.GetType().Name, Is.EqualTo("LocalPlateSolver"));
        }
    }
}
