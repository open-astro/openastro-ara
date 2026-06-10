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
using OpenAstroAra.Astrometry;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — exercises the cross-epoch coordinate transform against the REAL SOFA/NOVAS natives
    /// (built by <c>scripts/build-astrometry-natives.sh</c> and staged next to the test binary by
    /// the analyzer-gate CI job). Self-ignores when the natives are absent (e.g. a dev box that
    /// hasn't built them) so the sim-free suite stays green everywhere while CI proves the
    /// resolver + native build actually work.
    /// </summary>
    [TestFixture]
    public class AstrometryNativeTransformTest {

        [Test]
        public void Transform_J2000_to_JNOW_applies_precession() {
            var j2000 = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(45.0), Epoch.J2000);
            Coordinates jnow;
            try {
                jnow = j2000.Transform(Epoch.JNOW);
            } catch (DllNotFoundException) {
                Assert.Ignore("SOFA/NOVAS natives not present — run scripts/build-astrometry-natives.sh into the test bin dir to exercise this.");
                return;
            }

            Assert.That(jnow.Epoch, Is.EqualTo(Epoch.JNOW));
            // ~26 years of precession since J2000.0 moves a mid-declination point by arcminutes:
            // the transform must produce a real, small shift — neither an identity copy nor garbage.
            var raShiftDeg = Math.Abs(jnow.RADegrees - j2000.RADegrees);
            var decShiftDeg = Math.Abs(jnow.Dec - j2000.Dec);
            Assert.That(raShiftDeg + decShiftDeg, Is.GreaterThan(0.001), "transform was an identity — natives not actually applied");
            Assert.That(raShiftDeg, Is.LessThan(1.0), "RA shift implausibly large for ~26y of precession");
            Assert.That(decShiftDeg, Is.LessThan(1.0), "Dec shift implausibly large for ~26y of precession");
        }

        [Test]
        public void Transform_roundtrip_returns_to_J2000_within_tolerance() {
            var original = new Coordinates(Angle.ByHours(12.5), Angle.ByDegree(-30.0), Epoch.J2000);
            Coordinates roundTripped;
            try {
                roundTripped = original.Transform(Epoch.JNOW).Transform(Epoch.J2000);
            } catch (DllNotFoundException) {
                Assert.Ignore("SOFA/NOVAS natives not present — run scripts/build-astrometry-natives.sh into the test bin dir to exercise this.");
                return;
            }

            // Round-trip through JNOW and back should land within ~arcsecond numerics.
            Assert.That(roundTripped.RADegrees, Is.EqualTo(original.RADegrees).Within(0.001));
            Assert.That(roundTripped.Dec, Is.EqualTo(original.Dec).Within(0.001));
        }
    }
}
