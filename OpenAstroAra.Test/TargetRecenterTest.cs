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
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The ONE shared plate-solve-and-re-center: horizon guard first, then a bounded
    /// CenterOnTarget whose every rig fault maps to an outcome rather than an exception.</summary>
    [TestFixture]
    public class TargetRecenterTest {

        private static readonly Coordinates Target = new(ra: 83.8, dec: 0.0, Epoch.J2000, Coordinates.RAType.Degrees);

        private static Mock<ICenteringService> Centering(Func<CancellationToken, Task<PlateSolveResult>> behaviour) {
            var centering = new Mock<ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .Returns((Coordinates _, IProgress<PlateSolveProgress>? _, IProgress<ApplicationStatus>? _, CancellationToken ct) => behaviour(ct));
            return centering;
        }

        /// <summary>A near-pole site: the dec-0° target hugs the horizon, so the flat floor decides.</summary>
        private static Mock<IProfileStore> PolarSite(double floorDeg) {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", false, floorDeg, 4, 2.0, "astronomical"));
            return store;
        }

        [Test]
        public async Task A_successful_solve_is_Recentered() {
            var centering = Centering(_ => Task.FromResult(new PlateSolveResult()));
            var recenter = new TargetRecenter(() => centering.Object);
            var result = await recenter.RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(TargetRecenterOutcome.Recentered));
        }

        [Test]
        public async Task A_target_below_the_horizon_limit_is_never_slewed_to() {
            var centering = Centering(_ => Task.FromResult(new PlateSolveResult()));
            var recenter = new TargetRecenter(() => centering.Object, PolarSite(floorDeg: 20).Object);
            var result = await recenter.RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(TargetRecenterOutcome.TargetTooLow));
            Assert.That(result.LimitDeg, Is.EqualTo(20));
            centering.VerifyNoOtherCalls();

            var clears = new TargetRecenter(() => centering.Object, PolarSite(floorDeg: -20).Object);
            Assert.That((await clears.RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.Recentered));
        }

        [Test]
        public async Task No_centering_service_and_no_solver_are_told_apart() {
            var none = new TargetRecenter(() => null);
            Assert.That((await none.RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.NoCenteringService));

            var unconfigured = Centering(_ => throw new PlateSolverConfigurationException("no solver"));
            var recenter = new TargetRecenter(() => unconfigured.Object);
            Assert.That((await recenter.RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.Unconfigured));
        }

        [Test]
        public async Task The_bound_expiring_is_Timeout_and_the_callers_token_is_Cancelled() {
            var centering = Centering(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new PlateSolveResult(); });
            var recenter = new TargetRecenter(() => centering.Object);
            Assert.That((await recenter.RecenterAsync(Target, TimeSpan.FromMilliseconds(50), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.Timeout));

            using var cts = new CancellationTokenSource(50);
            Assert.That((await recenter.RecenterAsync(Target, TimeSpan.FromSeconds(30), cts.Token)).Outcome, Is.EqualTo(TargetRecenterOutcome.Cancelled));
        }

        [Test]
        public async Task A_failed_solve_or_a_throwing_rig_is_Failed() {
            var failed = Centering(_ => Task.FromResult(new PlateSolveResult { Success = false }));
            Assert.That((await new TargetRecenter(() => failed.Object).RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.Failed));

            var throwing = Centering(_ => throw new InvalidOperationException("camera busy"));
            Assert.That((await new TargetRecenter(() => throwing.Object).RecenterAsync(Target, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome, Is.EqualTo(TargetRecenterOutcome.Failed));
        }

        [Test]
        public async Task The_first_run_with_a_coordinate_target_is_the_one_centered() {
            var centering = Centering(_ => Task.FromResult(new PlateSolveResult()));
            var sequencer = new Mock<ISequencerService>();
            var none = Guid.NewGuid();
            var withTarget = Guid.NewGuid();
            sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(none, It.IsAny<CancellationToken>())).ReturnsAsync((Coordinates?) null);
            sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(withTarget, It.IsAny<CancellationToken>())).ReturnsAsync(Target);
            var recenter = new TargetRecenter(() => centering.Object);

            var result = await recenter.RecenterActiveTargetAsync(sequencer.Object, new List<Guid> { none, withTarget }, TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(TargetRecenterOutcome.Recentered));
            centering.Verify(c => c.CenterOnTarget(It.Is<Coordinates>(x => x.RADegrees == Target.RADegrees), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()), Times.Once);

            Assert.That((await recenter.RecenterActiveTargetAsync(sequencer.Object, new List<Guid> { none }, TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome,
                Is.EqualTo(TargetRecenterOutcome.NotAttempted), "a plan without a coordinate target has nothing to center on");
        }
    }
}
