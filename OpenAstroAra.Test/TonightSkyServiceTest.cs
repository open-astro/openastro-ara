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

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36/§25.5 Tonight's Sky — the altitude math + ranking. The trig is checked against exact
    /// invariants (no external almanac): a celestial-pole object sits at altitude = latitude for any
    /// time/longitude; an object at the meridian (hour angle 0) reaches its transit altitude; and
    /// nothing ever exceeds its transit altitude.
    /// </summary>
    [TestFixture]
    public class TonightSkyServiceTest {

        private static SiteSettingsDto Site(double lat, double lon, double horizon = 0) =>
            new(SiteName: "Test", LatitudeDeg: lat, LongitudeDeg: lon, ElevationM: 0, TimeZone: "UTC",
                UseCustomHorizon: false, DefaultHorizonAltitudeDeg: horizon, BortleClass: 4,
                TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        [Test]
        public void Hour_angle_zero_gives_the_transit_altitude() {
            // On the meridian, altitude = 90 − |φ − δ|.
            Assert.That(TonightSkyService.AltitudeFromHourAngleDeg(decDeg: 40, latDeg: 40, hourAngleDeg: 0),
                Is.EqualTo(90).Within(1e-6), "δ = φ transits at the zenith (asin(1) singularity → 1e-6)");
            Assert.That(TonightSkyService.AltitudeFromHourAngleDeg(decDeg: 0, latDeg: 40, hourAngleDeg: 0),
                Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void An_object_on_the_horizon_circle_reads_zero_altitude() {
            // Equator observer, equatorial object, 6h hour angle (90°) → on the horizon.
            Assert.That(TonightSkyService.AltitudeFromHourAngleDeg(decDeg: 0, latDeg: 0, hourAngleDeg: 90),
                Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void A_pole_object_sits_at_altitude_equal_to_latitude_any_time() {
            // The north celestial pole's altitude equals the observer's latitude, independent of
            // sidereal time / longitude — a clean end-to-end check of the LST→hour-angle→altitude path.
            const double lat = 47.6;
            foreach (var hours in new[] { 0.0, 6.0, 13.3, 21.7 }) {
                var utc = new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero).AddHours(hours);
                // RA is irrelevant at δ=90 (the cos H term is multiplied by cos δ = 0), so any hour angle
                // works — using LST − 0 here just exercises the full LST→hour-angle path.
                var alt = TonightSkyService.AltitudeFromHourAngleDeg(
                    decDeg: 90, latDeg: lat,
                    hourAngleDeg: TonightSkyService.LocalSiderealTimeDeg(utc, longitudeDeg: -122.3) - 0);
                Assert.That(alt, Is.EqualTo(lat).Within(1e-6), "pole altitude == latitude at h=$hours");
            }
        }

        [Test]
        public void Max_altitude_never_below_the_instantaneous_altitude() {
            // Sweep an object across hour angles; its altitude must never exceed the transit max.
            const double lat = 35, dec = 10;
            var max = TonightSkyService.MaxAltitudeDeg(dec, lat);
            for (var ha = -180.0; ha <= 180.0; ha += 15.0) {
                var alt = TonightSkyService.AltitudeFromHourAngleDeg(dec, lat, ha);
                Assert.That(alt, Is.LessThanOrEqualTo(max + 1e-9));
            }
        }

        [Test]
        public void Rank_returns_only_objects_above_the_horizon_highest_first() {
            var site = Site(lat: 40, lon: 0, horizon: 20);
            var at = new DateTimeOffset(2026, 6, 16, 4, 0, 0, TimeSpan.Zero);
            var ranked = TonightSkyService.Rank(site, at, limit: 100);

            Assert.That(ranked, Is.Not.Empty, "something is always up from a mid-latitude site");
            // Every result clears the horizon, and the list is altitude-descending.
            for (var i = 0; i < ranked.Count; i++) {
                Assert.That(ranked[i].AltitudeDeg, Is.GreaterThanOrEqualTo(20));
                if (i > 0) {
                    Assert.That(ranked[i - 1].AltitudeDeg, Is.GreaterThanOrEqualTo(ranked[i].AltitudeDeg));
                }
            }
        }

        [Test]
        public void Rank_respects_the_limit() {
            var site = Site(lat: 40, lon: 0);
            var at = new DateTimeOffset(2026, 6, 16, 4, 0, 0, TimeSpan.Zero);
            Assert.That(TonightSkyService.Rank(site, at, limit: 3), Has.Count.LessThanOrEqualTo(3));
        }

        [Test]
        public void A_high_horizon_can_filter_everything_out() {
            // No catalog object can be above 90°, so an 89° horizon yields an empty list, not a throw.
            var site = Site(lat: 40, lon: 0, horizon: 89.5);
            var at = new DateTimeOffset(2026, 6, 16, 4, 0, 0, TimeSpan.Zero);
            Assert.That(TonightSkyService.Rank(site, at, limit: 10), Is.Empty);
        }
    }
}
