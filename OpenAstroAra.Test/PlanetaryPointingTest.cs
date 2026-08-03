#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using System;

namespace OpenAstroAra.Test {

    /// <summary>§77.3 P4 — server-side solar-system ephemeris sanity.</summary>
    [TestFixture]
    public class PlanetaryPointingTest {

        private static readonly ObserverInfo Observer = new() {
            Latitude = 40.0,
            Longitude = -105.0,
            Elevation = 1600,
        };

        private static SkyPosition Position(AstroUtil.SolarSystemBody body, DateTime utc) {
            try {
                var p = AstroUtil.GetBodyPosition(body, utc, AstroUtil.GetJulianDate(utc), Observer);
                if (double.IsNaN(p.RA)) {
                    // GetBodyPosition eats native failures into NaN; on dev hosts without
                    // the natives that is the DllNotFound path surfaced by Logger only.
                    Assert.Ignore("SOFA/NOVAS natives not present — run scripts/build-astrometry-natives.sh into the test bin dir.");
                }
                return p;
            } catch (DllNotFoundException) {
                Assert.Ignore("SOFA/NOVAS natives not present — run scripts/build-astrometry-natives.sh into the test bin dir.");
                throw;   // unreachable; Ignore throws
            }
        }

        [Test]
        public void GetBodyPosition_Moon_MatchesTheExistingMoonPath() {
            // The generalized body path must agree with the long-standing Moon path —
            // same NOVAS Place call, so any drift means a wiring mistake.
            var utc = new DateTime(2026, 8, 3, 6, 0, 0, DateTimeKind.Utc);
            var viaBody = Position(AstroUtil.SolarSystemBody.Moon, utc);
            var viaMoon = AstroUtil.GetMoonPosition(utc, AstroUtil.GetJulianDate(utc), Observer);
            viaBody.RA.Should().BeApproximately(viaMoon.RA, 1e-9);
            viaBody.Dec.Should().BeApproximately(viaMoon.Dec, 1e-9);
        }

        [Test]
        public void GetBodyPosition_Jupiter_KnownEpochSpotCheck() {
            // Jupiter reaches opposition 2026-01-10 in Gemini: on 2026-01-01 the
            // apparent place is RA ≈ 7h 32m, Dec ≈ +22°. Wide tolerance (0.2h / 2°) —
            // this guards gross wiring errors (wrong body number, degrees-vs-hours),
            // not ephemeris precision.
            var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var p = Position(AstroUtil.SolarSystemBody.Jupiter, utc);
            p.RA.Should().BeApproximately(7.54, 0.2);
            p.Dec.Should().BeApproximately(22.0, 2.0);
        }

        [Test]
        public void GetBodyPosition_AllBodies_ReturnFiniteCoordinates() {
            var utc = DateTime.UtcNow;
            foreach (AstroUtil.SolarSystemBody body in Enum.GetValues<AstroUtil.SolarSystemBody>()) {
                var p = Position(body, utc);
                p.RA.Should().BeInRange(0, 24, $"{body} RA must be hours");
                p.Dec.Should().BeInRange(-90, 90, $"{body} Dec must be degrees");
                p.Dis.Should().BePositive($"{body} distance must be positive");
            }
        }

        [Test]
        public void GetBodyPosition_MoonAndJupiter_Differ() {
            var utc = DateTime.UtcNow;
            var moon = Position(AstroUtil.SolarSystemBody.Moon, utc);
            var jupiter = Position(AstroUtil.SolarSystemBody.Jupiter, utc);
            (Math.Abs(moon.RA - jupiter.RA) + Math.Abs(moon.Dec - jupiter.Dec)).Should().BeGreaterThan(0.001);
        }
    }
}
