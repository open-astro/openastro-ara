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
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The NOVAS bodies and the pure-C# SiteAstrometry must tell the same
    /// story. On rc91 they didn't: without libnovas31 + the JPL ephemeris
    /// file, NOVAS produced confidently WRONG positions (sun at -73° in
    /// mid-morning, "New Moon" during a waning gibbous) rather than failing
    /// cleanly. JPLEPH now ships with every publish; this pins that when the
    /// native stack is present it agrees with the independent Meeus-grade
    /// math — two implementations, one sky.
    ///
    /// Skips (never fails) where the natives aren't installed, so CI without
    /// them stays green while any dev who runs
    /// scripts/build-astrometry-natives.sh gets the real check.
    /// </summary>
    [TestFixture]
    public class NovasEphemerisAgreementTest {

        private static bool NativesAvailable() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
            return NativeLibrary.TryLoad("novas31", typeof(NOVAS).Assembly, null, out _);
        }

        [Test]
        public async Task Novas_and_SiteAstrometry_agree_on_the_sky() {
            if (!NativesAvailable()) {
                Assert.Ignore("libnovas31 not installed — run scripts/build-astrometry-natives.sh");
            }
            if (!File.Exists(NOVAS.EphemerisLocation)) {
                Assert.Ignore($"JPLEPH not found at {NOVAS.EphemerisLocation}");
            }

            // A fixed instant + site (rc91's), so a disagreement is a math
            // regression, not a flaky now().
            var at = new DateTimeOffset(2026, 8, 4, 16, 40, 0, TimeSpan.Zero);
            const double lat = 34.660965, lon = -106.783272, elev = 1470;

            var moon = new Astrometry.Body.Moon(at.UtcDateTime, lat, lon, elev);
            var sun = new Astrometry.Body.Sun(at.UtcDateTime, lat, lon, elev);
            await Task.WhenAll(moon.Calculate(), sun.Calculate());

            Assert.That(double.IsNaN(sun.Altitude), Is.False, "NOVAS sun position failed");
            Assert.That(double.IsNaN(moon.Altitude), Is.False, "NOVAS moon position failed");

            var lst = SiteAstrometry.LocalSiderealTimeDeg(at, lon);
            var (sunRa, sunDec) = SiteAstrometry.SunEquatorialDeg(at);
            var expectedSunAlt = SiteAstrometry.AltitudeFromHourAngleDeg(sunDec, lat, lst - sunRa);
            var (moonRa, moonDec) = SiteAstrometry.MoonEquatorialDeg(at);
            var expectedMoonAlt = SiteAstrometry.AltitudeFromHourAngleDeg(moonDec, lat, lst - moonRa);

            // SiteAstrometry is Meeus-grade (geocentric moon, ~1° of topocentric
            // parallax); 2° of slack separates "same sky" from "wrong sky" —
            // the rc91 failure mode was off by >120°.
            Assert.That(sun.Altitude, Is.EqualTo(expectedSunAlt).Within(2.0),
                "sun altitude: NOVAS vs SiteAstrometry");
            Assert.That(moon.Altitude, Is.EqualTo(expectedMoonAlt).Within(2.0),
                "moon altitude: NOVAS vs SiteAstrometry");

            var observer = new ObserverInfo { Latitude = lat, Longitude = lon, Elevation = elev };
            var novasIllum = AstroUtil.GetMoonIllumination(at.UtcDateTime, observer);
            var expectedIllum = SiteAstrometry.MoonIlluminatedFraction(at);
            Assert.That(novasIllum, Is.EqualTo(expectedIllum).Within(0.03),
                "moon illumination: NOVAS vs SiteAstrometry");
        }
    }
}
