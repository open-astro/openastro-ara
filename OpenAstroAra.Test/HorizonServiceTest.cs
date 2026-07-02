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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36 Planning horizon — the alt/az → equatorial projection that draws the local horizon on an
    /// equatorial sky chart. The trig is checked against exact invariants (no external almanac): every point on
    /// the returned curve reads back at exactly the horizon altitude (round-trip through the forward
    /// altitude formula); the zenith sits at dec = latitude, RA = local sidereal time; and at the
    /// equator the due-north horizon point is the north celestial pole.
    /// </summary>
    [TestFixture]
    public class HorizonServiceTest {

        private static SiteSettingsDto Site(double lat, double lon, double horizon, bool customHorizon = false) =>
            new(SiteName: "Test", LatitudeDeg: lat, LongitudeDeg: lon, ElevationM: 0, TimeZone: "UTC",
                UseCustomHorizon: customHorizon, DefaultHorizonAltitudeDeg: horizon, BortleClass: 4,
                TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        private static readonly DateTimeOffset At = new(2026, 6, 16, 4, 0, 0, TimeSpan.Zero);
        private static readonly string[] CardinalLabels = { "N", "E", "S", "W" };

        [Test]
        public void Every_horizon_point_reads_back_at_the_horizon_altitude() {
            // The forward altitude formula applied to each returned (RA, Dec) must give the horizon
            // altitude — the strongest end-to-end check that the inverse is the true inverse.
            const double horizon = 20.0, lat = 47.6, lon = -122.3;
            var dto = HorizonService.Compute(Site(lat, lon, horizon), At);

            foreach (var p in dto.Points) {
                var altBack = TonightSkyService.AltitudeFromHourAngleDeg(
                    p.DecDeg, lat, dto.LocalSiderealTimeDeg - p.RaDeg);
                Assert.That(altBack, Is.EqualTo(horizon).Within(1e-6),
                    $"az {p.AzimuthDeg}° projected off the horizon");
            }
        }

        [Test]
        public void The_curve_samples_every_two_degrees_and_closes() {
            var dto = HorizonService.Compute(Site(lat: 40, lon: 0, horizon: 20), At);
            Assert.That(dto.Points, Has.Count.EqualTo(181)); // 0..360 step 2, inclusive
            var first = dto.Points[0];
            var last = dto.Points[^1];
            // The final vertex wraps onto azimuth 0 so the polyline closes seamlessly.
            Assert.That(last.AzimuthDeg, Is.EqualTo(0.0));
            Assert.That(last.RaDeg, Is.EqualTo(first.RaDeg).Within(1e-9));
            Assert.That(last.DecDeg, Is.EqualTo(first.DecDeg).Within(1e-9));
        }

        [Test]
        public void Zenith_is_dec_equals_latitude_and_ra_equals_sidereal_time() {
            const double lat = 35.0, lon = 10.0;
            var dto = HorizonService.Compute(Site(lat, lon, horizon: 0), At);
            Assert.That(dto.Zenith.DecDeg, Is.EqualTo(lat).Within(1e-6));
            Assert.That(dto.Zenith.RaDeg, Is.EqualTo(dto.LocalSiderealTimeDeg).Within(1e-6));
        }

        [Test]
        public void At_the_equator_due_north_on_the_horizon_is_the_north_celestial_pole() {
            // lat 0, alt 0, az 0 (north): sin(dec) = 1 → the NCP. A clean closed-form invariant.
            var dto = HorizonService.Compute(Site(lat: 0, lon: 0, horizon: 0), At);
            var north = dto.Cardinals.Single(c => c.Label == "N");
            Assert.That(north.DecDeg, Is.EqualTo(90.0).Within(1e-6));
        }

        [Test]
        public void All_four_cardinals_are_present_and_sit_on_the_horizon() {
            const double horizon = 15.0, lat = 30.0, lon = -45.0;
            var dto = HorizonService.Compute(Site(lat, lon, horizon), At);
            Assert.That(dto.Cardinals.Select(c => c.Label), Is.EquivalentTo(CardinalLabels));
            foreach (var c in dto.Cardinals) {
                var altBack = TonightSkyService.AltitudeFromHourAngleDeg(
                    c.DecDeg, lat, dto.LocalSiderealTimeDeg - c.RaDeg);
                Assert.That(altBack, Is.EqualTo(horizon).Within(1e-6), $"cardinal {c.Label} off the horizon");
            }
        }

        [Test]
        public void Echoes_the_requested_time_and_horizon_altitude() {
            var dto = HorizonService.Compute(Site(lat: 12, lon: 34, horizon: 22.5), At);
            Assert.That(dto.AtUtc, Is.EqualTo(At));
            Assert.That(dto.HorizonAltitudeDeg, Is.EqualTo(22.5));
        }

        [Test]
        public void Due_east_is_east_of_the_meridian_and_due_west_is_its_mirror() {
            // The round-trip altitude test passes through cos(H) — an even function — so it can't catch an
            // East↔West RA sign flip (lstDeg + H instead of lstDeg − H). Pin the chirality directly: a
            // point due east (az 90°) has not yet transited, so its hour angle (H = LST − RA) is negative;
            // due west (az 270°) is the mirror (positive). A sign inversion can't satisfy both.
            var dto = HorizonService.Compute(Site(lat: 45, lon: 0, horizon: 10), At);
            var east = dto.Cardinals.Single(c => c.Label == "E");
            var west = dto.Cardinals.Single(c => c.Label == "W");
            Assert.That(SignedHourAngle(dto.LocalSiderealTimeDeg - east.RaDeg),
                Is.LessThan(0).And.GreaterThan(-180), "due east must sit east of the meridian (H < 0)");
            Assert.That(SignedHourAngle(dto.LocalSiderealTimeDeg - west.RaDeg),
                Is.GreaterThan(0).And.LessThan(180), "due west must sit west of the meridian (H > 0)");
        }

        // Normalise an angle difference to (−180, 180] so an hour-angle sign test is wrap-safe.
        private static double SignedHourAngle(double deg) {
            var x = deg % 360.0;
            if (x > 180.0) x -= 360.0;
            if (x <= -180.0) x += 360.0;
            return x;
        }

        [Test]
        public void Flags_custom_horizon_ignored_only_when_the_profile_wants_one() {
            // This flat-horizon slice never honours a custom terrain horizon, so it advertises when one
            // was requested (so a later slice can warn) and stays false otherwise.
            Assert.That(HorizonService.Compute(Site(40, 0, 20, customHorizon: true), At).CustomHorizonIgnored,
                Is.True);
            Assert.That(HorizonService.Compute(Site(40, 0, 20, customHorizon: false), At).CustomHorizonIgnored,
                Is.False);
        }
    }
}
