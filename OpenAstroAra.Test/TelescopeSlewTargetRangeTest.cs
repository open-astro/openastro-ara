#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#2075 — the pure parts of the mediator's slew-target guard: the range refusal, the RA
    /// wrap, and the fold-aware arrival check.</summary>
    [TestFixture]
    public class TelescopeSlewTargetRangeTest {

        [TestCase(0.0, 90.61)]
        [TestCase(12.0, -90.5)]
        [TestCase(6.0, double.NaN)]
        [TestCase(double.NaN, 45.0)]
        public void SlewTargetRefusal_refuses_a_dec_past_the_pole_or_a_non_finite_value(double raHours, double decDeg) {
            var refusal = TelescopeService.SlewTargetRefusal(raHours, decDeg);
            Assert.That(refusal, Is.Not.Null);
            Assert.That(refusal, Does.StartWith("telescope.slew refused"));
            Assert.That(refusal, Does.Contain("Nothing was sent to the mount"));
        }

        [TestCase(0.0, 90.0)]
        [TestCase(0.0, -90.0)]
        [TestCase(24.3, 45.0)]
        [TestCase(-0.5, 45.0)]
        public void SlewTargetRefusal_accepts_the_poles_and_wraps_ra(double raHours, double decDeg) {
            Assert.That(TelescopeService.SlewTargetRefusal(raHours, decDeg), Is.Null);
        }

        [TestCase(24.3, 0.3)]
        [TestCase(-0.5, 23.5)]
        [TestCase(48.0, 0.0)]
        [TestCase(-1e-17, 0.0)]
        public void WrapRaHours_wraps_into_0_to_24(double raHours, double expected) {
            var wrapped = TelescopeService.WrapRaHours(raHours);
            Assert.That(wrapped, Is.EqualTo(expected).Within(1e-9));
            Assert.That(wrapped, Is.GreaterThanOrEqualTo(0.0).And.LessThan(24.0));
        }

        [Test]
        public void PointingNear_matches_a_reading_over_the_pole_to_its_folded_target() {
            // The mount reports (RA 6h, Dec 90.6°) after a goto to the folded (18h, 89.4°): the same point.
            Assert.That(TelescopeService.PointingNear(6.0, 90.6, 18.0, 89.4), Is.True);
            Assert.That(TelescopeService.PointingNear(18.0, 89.4, 6.0, 90.6), Is.True);
            Assert.That(TelescopeService.PointingNear(6.0, 45.0, 6.1, 45.5), Is.True);
            Assert.That(TelescopeService.PointingNear(6.0, 45.0, 18.0, 45.0), Is.False);
        }

        [Test]
        public void PointingNear_has_no_seam_at_the_pole() {
            // A goto to (6h, 89.99°) on a mount that then reads (6h, 90.001°): 0.011° apart. Folding the
            // reading alone gives (18h, 89.999°), 180° off in RA, and the settle would end in a stall fault.
            Assert.That(TelescopeService.PointingNear(6.0, 90.001, 6.0, 89.99), Is.True);
            // Near the pole a large RA difference is a small distance: (6h, 89.9°) and (7h, 89.9°) are 0.03° apart.
            Assert.That(TelescopeService.PointingNear(7.0, 89.9, 6.0, 89.9), Is.True);
            Assert.That(TelescopeService.PointingNear(double.NaN, 45.0, 6.0, 45.0), Is.False);
            Assert.That(TelescopeService.PointingNear(6.0, 80.0, 6.0, 89.0), Is.False);
        }
    }
}
