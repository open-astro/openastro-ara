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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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

        // ─── §36.8 slice 1: visibility window / transit / integration hours ───

        private static TonightSkyService.CatalogObject Obj(
                string id, double raDeg, double decDeg, double mag = 8.0, string type = "neb") =>
            new(id, id, type, mag, raDeg, decDeg);

        private static IProfileStore ProfileStore(SiteSettingsDto site) {
            var mock = new Mock<IProfileStore>();
            mock.Setup(p => p.GetSiteSettings()).Returns(site);
            return mock.Object;
        }

        [Test]
        public void Window_is_non_empty_for_an_object_high_at_local_midnight() {
            // Midnight at longitude 0 ≈ local solar midnight, so the sun is well down. Put the object on
            // the meridian (RA = LST) at dec = lat so it transits at the zenith in deep night.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var ranked = TonightSkyService.Rank(new[] { Obj("HIGH", ra, 40) }, site, at, limit: 10);

            Assert.That(ranked, Has.Count.EqualTo(1));
            var o = ranked[0];
            Assert.That(o.WindowStartUtc, Is.Not.Null);
            Assert.That(o.WindowEndUtc, Is.Not.Null);
            Assert.That(o.IntegrationHours, Is.GreaterThan(0));
            // Hours == window length (within the 5-min sample resolution / rounding).
            Assert.That(o.IntegrationHours,
                Is.EqualTo((o.WindowEndUtc!.Value - o.WindowStartUtc!.Value).TotalHours).Within(0.01));
            // The window must straddle the query instant (the object is up at atUtc).
            Assert.That(o.WindowStartUtc!.Value, Is.LessThanOrEqualTo(at));
            Assert.That(o.WindowEndUtc!.Value, Is.GreaterThanOrEqualTo(at));
        }

        [Test]
        public void An_object_below_the_horizon_now_is_excluded() {
            // Far-southern object from a northern site never clears the horizon → not in the list at all.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 50, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var ranked = TonightSkyService.Rank(new[] { Obj("SOUTH", ra, -70) }, site, at, limit: 10);
            Assert.That(ranked, Is.Empty);
        }

        [Test]
        public void A_daytime_only_object_has_an_empty_window() {
            // Noon at longitude 0. A southern, short-arc object placed at the sun's RA transits with the
            // sun, so its whole above-horizon arc falls in daylight → no dark window, zero hours, even
            // though it is above the horizon right now (so it is still listed).
            var at = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var (sunRa, _) = TonightSkyService.SunEquatorialDeg(at);
            var ranked = TonightSkyService.Rank(new[] { Obj("DAY", sunRa, -45) }, site, at, limit: 10);

            Assert.That(ranked, Has.Count.EqualTo(1), "above the horizon at noon, so it is listed");
            Assert.That(ranked[0].WindowStartUtc, Is.Null);
            Assert.That(ranked[0].WindowEndUtc, Is.Null);
            Assert.That(ranked[0].IntegrationHours, Is.EqualTo(0));
        }

        [Test]
        public void Transit_time_lands_at_the_max_altitude() {
            // At the reported transit instant the object sits on the meridian, so its altitude there must
            // equal its transit (max) altitude — an end-to-end check of the analytic transit solve.
            var at = new DateTimeOffset(2026, 12, 21, 2, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            // dec 30 (≠ lat) → transit altitude 80°, clear of the zenith asin singularity.
            var ranked = TonightSkyService.Rank(new[] { Obj("TR", ra + 20, 30) }, site, at, limit: 10);

            Assert.That(ranked, Has.Count.EqualTo(1));
            var o = ranked[0];
            Assert.That(o.TransitUtc, Is.Not.Null);
            // Transit within ±12 h of the query instant.
            Assert.That(Math.Abs((o.TransitUtc!.Value - at).TotalHours), Is.LessThanOrEqualTo(12.0));
            var lstAtTransit = TonightSkyService.LocalSiderealTimeDeg(o.TransitUtc!.Value, site.LongitudeDeg);
            var altAtTransit = TonightSkyService.AltitudeFromHourAngleDeg(
                decDeg: 30, latDeg: 40, hourAngleDeg: lstAtTransit - (ra + 20));
            Assert.That(altAtTransit, Is.EqualTo(o.MaxAltitudeDeg).Within(0.05));
        }

        [Test]
        public void New_size_fields_are_echoed_from_the_catalog() {
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var obj = new TonightSkyService.CatalogObject(
                "SZ", "Sized", "galaxy", 9.0, ra, 40,
                SizeMajArcmin: 12.3, SizeMinArcmin: 4.5, PosAngleDeg: 35, SurfaceBrightness: 22.1);
            var o = TonightSkyService.Rank(new[] { obj }, site, at, limit: 10).Single();

            Assert.That(o.SizeMajArcmin, Is.EqualTo(12.3));
            Assert.That(o.SizeMinArcmin, Is.EqualTo(4.5));
            Assert.That(o.PosAngleDeg, Is.EqualTo(35));
            Assert.That(o.SurfaceBrightness, Is.EqualTo(22.1));
        }

        [Test]
        public void No_catalog_falls_back_to_the_hardcoded_list() {
            // When the OpenNGC catalog isn't installed (GetAllDsos → null), GetTonight must still return
            // results from the hardcoded starter Catalog — identical to the static Rank over it.
            var at = new DateTimeOffset(2026, 12, 21, 4, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 10);
            var svc = new TonightSkyService(ProfileStore(site), new FakeCatalog(null));

            var ranked = svc.GetTonight(at, limit: 10);
            var expected = TonightSkyService.Rank(site, at, limit: 10);

            Assert.That(ranked.Select(o => o.Id), Is.EqualTo(expected.Select(o => o.Id)));
            // Every fallback object is one of the hardcoded starter ids.
            var hardcodedIds = TonightSkyService.Catalog.Select(c => c.Id).ToHashSet();
            Assert.That(ranked.Select(o => o.Id), Is.SubsetOf(hardcodedIds));
        }

        [Test]
        public void An_installed_catalog_supplies_the_candidates() {
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var dsos = new List<DsoEntryDto> {
                new("NGC0001", "Catalog Object", "G", ra, 40, 9.0, 5.0, 3.0, 90, 21.0),
                new("NGC0002", "Too Faint", "G", ra, 40, 15.0, null, null, null, null), // culled by mag floor
            };
            var svc = new TonightSkyService(ProfileStore(site), new FakeCatalog(dsos));

            var ranked = svc.GetTonight(at, limit: 10);
            Assert.That(ranked.Select(o => o.Id), Does.Contain("NGC0001"));
            Assert.That(ranked.Select(o => o.Id), Does.Not.Contain("NGC0002"), "mag 15 > the 12 cull bound");
            var o = ranked.Single(x => x.Id == "NGC0001");
            Assert.That(o.Name, Is.EqualTo("Catalog Object"));
            Assert.That(o.SizeMajArcmin, Is.EqualTo(5.0));
        }

        /// <summary>Minimal <see cref="ISkyCatalogService"/> test double — only GetAllDsos is exercised.</summary>
        private sealed class FakeCatalog : ISkyCatalogService {
            private readonly IReadOnlyList<DsoEntryDto>? _dsos;
            public FakeCatalog(IReadOnlyList<DsoEntryDto>? dsos) => _dsos = dsos;
            public IReadOnlyList<DsoEntryDto>? GetAllDsos(CancellationToken ct) => _dsos;
            public IReadOnlyList<CatalogInfoDto> List() => Array.Empty<CatalogInfoDto>();
            public IReadOnlyList<CatalogObjectDto>? GetObjects(string catalogId, int? limit, CancellationToken ct) => null;
        }
    }
}
