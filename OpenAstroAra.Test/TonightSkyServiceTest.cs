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
        public void Rank_lists_objects_with_a_window_tonight_score_descending() {
            var site = Site(lat: 40, lon: 0, horizon: 20);
            var at = new DateTimeOffset(2026, 6, 16, 4, 0, 0, TimeSpan.Zero);
            var ranked = TonightSkyService.Rank(site, at, limit: 100);

            Assert.That(ranked, Is.Not.Empty, "something is always up in the dark from a mid-latitude site");
            // Every listed object has a real dark window tonight, and the list is score-descending.
            for (var i = 0; i < ranked.Count; i++) {
                Assert.That(ranked[i].WindowStartUtc, Is.Not.Null, "listed → has a window (the slice-2 gate)");
                Assert.That(ranked[i].Score, Is.InRange(0.0, 100.0));
                if (i > 0) {
                    Assert.That(ranked[i - 1].Score, Is.GreaterThanOrEqualTo(ranked[i].Score));
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

        // A representative full-frame rig at ~530 mm — only the slice-2 framing tests that build their own
        // optics care; the timing/window tests are framing-agnostic (their objects carry no size).
        private static OpticsSettingsDto Optics(double focalLengthMm = 530, double reducer = 1.0,
                int wPx = 6000, int hPx = 4000, double pixelUm = 3.76) =>
            new(focalLengthMm, reducer, wPx, hPx, pixelUm);

        private static IProfileStore ProfileStore(SiteSettingsDto site, OpticsSettingsDto? optics = null) {
            var mock = new Mock<IProfileStore>();
            mock.Setup(p => p.GetSiteSettings()).Returns(site);
            mock.Setup(p => p.GetOpticsSettings()).Returns(optics ?? Optics());
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
        public void An_object_that_never_clears_the_horizon_is_excluded() {
            // Far-southern object from a northern site never clears the horizon (peak alt −30°) → dropped by
            // the MaxAltitude pre-filter (slice-2 gate is window-based, not "above the horizon right now").
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 50, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var ranked = TonightSkyService.Rank(new[] { Obj("SOUTH", ra, -70) }, site, at, limit: 10);
            Assert.That(ranked, Is.Empty);
        }

        [Test]
        public void A_daytime_only_object_is_dropped_by_the_window_gate() {
            // Noon at longitude 0. A southern, short-arc object placed at the sun's RA transits with the
            // sun, so its whole above-horizon arc falls in daylight → no dark window anywhere in the night.
            // Under the slice-2 window gate (was: above-horizon-now) it is therefore dropped, not listed.
            var at = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var (sunRa, _) = TonightSkyService.SunEquatorialDeg(at);
            var ranked = TonightSkyService.Rank(new[] { Obj("DAY", sunRa, -45) }, site, at, limit: 10);

            Assert.That(ranked, Is.Empty, "no dark window tonight → dropped");
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
        public void Southern_site_transit_and_window_are_correct() {
            // Negative-latitude end-to-end: the transit solve, MaxAltitudeDeg (90−|φ−δ|), and the
            // window/sun math must all hold below the equator, not just for the northern fixtures.
            var at = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero); // ~local midnight at lon 0 → dark
            var site = Site(lat: -33, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            // dec −20 from lat −33 → transit altitude 90 − |−33 − (−20)| = 77°, clear of the zenith asin singularity.
            var ranked = TonightSkyService.Rank(new[] { Obj("S", ra, -20) }, site, at, limit: 10);

            Assert.That(ranked, Has.Count.EqualTo(1));
            var o = ranked[0];
            Assert.That(o.MaxAltitudeDeg, Is.EqualTo(77.0).Within(0.05));
            Assert.That(o.TransitUtc, Is.Not.Null);
            var lstAtTransit = TonightSkyService.LocalSiderealTimeDeg(o.TransitUtc!.Value, site.LongitudeDeg);
            var altAtTransit = TonightSkyService.AltitudeFromHourAngleDeg(
                decDeg: -20, latDeg: -33, hourAngleDeg: lstAtTransit - ra);
            Assert.That(altAtTransit, Is.EqualTo(o.MaxAltitudeDeg).Within(0.05));
            // On the meridian in deep southern night → a real dark window.
            Assert.That(o.WindowStartUtc, Is.Not.Null);
            Assert.That(o.IntegrationHours, Is.GreaterThan(0));
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

        // ─── §36.8 slice 2: framing fit + equipment-aware worth score ───

        private static TonightSkyService.CatalogObject SizedObj(
                string id, double raDeg, double decDeg, double majArcmin, double mag = 6.0,
                double? surfaceBrightness = null) =>
            new(id, id, "neb", mag, raDeg, decDeg,
                SizeMajArcmin: majArcmin, SizeMinArcmin: majArcmin, SurfaceBrightness: surfaceBrightness);

        [Test]
        public void Fov_shrinks_with_focal_length() {
            // The same sensor frames a wider field at a short focal length than a long one.
            var (wShort, hShort) = TonightSkyService.FovArcmin(Optics(focalLengthMm: 448), 1, 1);
            var (wLong, hLong) = TonightSkyService.FovArcmin(Optics(focalLengthMm: 3000), 1, 1);
            Assert.That(wShort, Is.GreaterThan(wLong));
            Assert.That(hShort, Is.GreaterThan(hLong));
            // An unconfigured train can't frame anything → NaN, so framing reports Unknown.
            var (wNone, _) = TonightSkyService.FovArcmin(new OpticsSettingsDto(0, 0, 0, 0, 0), 1, 1);
            Assert.That(double.IsNaN(wNone), Is.True);
        }

        [Test]
        public void A_wide_nebula_frames_good_at_short_focal_length_too_big_at_long() {
            // The same 60′ nebula: a healthy fraction of the frame at 448 mm, but it overflows at 3000 mm.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var neb = new[] { SizedObj("NEB", ra, 40, majArcmin: 60) };

            var atShort = TonightSkyService.Rank(neb, site, Optics(focalLengthMm: 448), at, limit: 10).Single();
            var atLong = TonightSkyService.Rank(neb, site, Optics(focalLengthMm: 3000), at, limit: 10).Single();

            Assert.That(atShort.Framing, Is.EqualTo(FramingFit.Good));
            Assert.That(atLong.Framing, Is.EqualTo(FramingFit.TooBig));
            // Better framing → a higher worth at the same timing/altitude.
            Assert.That(atShort.Score, Is.GreaterThan(atLong.Score));
        }

        [Test]
        public void Focal_length_reorders_the_same_candidate_set() {
            // A wide nebula and a small galaxy on the same meridian at the same dark instant — only framing
            // separates them. At 448 mm the nebula fills the frame (galaxy too small); at 3000 mm the galaxy
            // fits (nebula overflows). The two optics must therefore disagree on which leads the list.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var candidates = new[] {
                SizedObj("WIDE", ra, 40, majArcmin: 60),   // wide nebula
                SizedObj("SMALL", ra, 40, majArcmin: 8),    // small galaxy
            };

            var shortFl = TonightSkyService.Rank(candidates, site, Optics(focalLengthMm: 448), at, limit: 10);
            var longFl = TonightSkyService.Rank(candidates, site, Optics(focalLengthMm: 3000), at, limit: 10);

            Assert.That(shortFl[0].Id, Is.EqualTo("WIDE"), "448 mm: the wide nebula frames best");
            Assert.That(longFl[0].Id, Is.EqualTo("SMALL"), "3000 mm: the small galaxy frames best");
            Assert.That(shortFl.Select(o => o.Id), Is.Not.EqualTo(longFl.Select(o => o.Id)),
                "different optics → different ordering of the same candidate set");
        }

        [Test]
        public void A_low_but_well_framed_bright_target_is_kept_and_scores_respectably() {
            // The "Dragons at 11°" case: a bright, well-framed nebula that only ever reaches ~11° must NOT
            // be hard-floored out — it stays in the list and earns a respectable worth on framing/brightness.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            // dec −39 from lat 40 → peak altitude 90 − |40 − (−39)| = 11°. Bright (mag 5), bright surface.
            var lowBright = SizedObj("DRAGONS", ra, -39, majArcmin: 40, mag: 5.0, surfaceBrightness: 20.0);

            var o = TonightSkyService.Rank(new[] { lowBright }, site, Optics(), at, limit: 10).Single();

            Assert.That(o.MaxAltitudeDeg, Is.EqualTo(11.0).Within(0.5), "genuinely a low target");
            Assert.That(o.Framing, Is.EqualTo(FramingFit.Good));
            Assert.That(o.WindowStartUtc, Is.Not.Null, "it has a real (if short) dark window → listed");
            Assert.That(o.Score, Is.GreaterThan(40.0), "advise-don't-dictate: well-framed + bright still scores well");
        }

        [Test]
        public void A_not_yet_risen_object_with_a_window_tonight_is_included() {
            // 10 pm in deep December dark; the object is still BELOW the horizon now but rises into the long
            // night and has a real window before dawn. The slice-1 above-horizon-now gate would have dropped
            // it; the slice-2 window gate keeps it.
            var at = new DateTimeOffset(2026, 12, 21, 22, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var lst0 = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var ra = lst0 + 120;   // hour angle −120° → east of the meridian, not yet risen
            var altNow = TonightSkyService.AltitudeFromHourAngleDeg(decDeg: 20, latDeg: 40, hourAngleDeg: -120);
            Assert.That(altNow, Is.LessThan(0), "precondition: the object is below the horizon at the query instant");

            var ranked = TonightSkyService.Rank(new[] { SizedObj("RISER", ra, 20, majArcmin: 30) }, site, Optics(), at, limit: 10);

            var o = ranked.Single(x => x.Id == "RISER");
            Assert.That(o.WindowStartUtc, Is.Not.Null);
            Assert.That(o.IntegrationHours, Is.GreaterThan(0));
            Assert.That(o.WindowStartUtc!.Value, Is.GreaterThan(at), "the window opens after the query (not yet risen)");
        }

        [Test]
        public void Score_is_bounded_and_explained_by_its_component_reasons() {
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var o = TonightSkyService.Rank(
                new[] { SizedObj("X", ra, 40, majArcmin: 40, mag: 6.0, surfaceBrightness: 21.0) },
                site, Optics(), at, limit: 10).Single();

            Assert.That(o.Score, Is.InRange(0.0, 100.0));
            Assert.That(o.ScoreReasons, Is.Not.Null.And.Count.EqualTo(5), "one tag per scoring component");
            Assert.That(o.ScoreReasons!, Has.Some.Contains("frame"), "framing reason present");
            // Each reason carries its rounded point contribution "(+N)"; their sum reconstructs the score
            // to within the per-component rounding slack (5 × 0.5).
            var sum = o.ScoreReasons!.Sum(ParsePoints);
            Assert.That(sum, Is.EqualTo(o.Score).Within(3.0));
        }

        private static double ParsePoints(string reason) {
            var plus = reason.LastIndexOf("(+", StringComparison.Ordinal);
            var close = reason.LastIndexOf(')');
            return double.Parse(reason.AsSpan(plus + 2, close - plus - 2),
                provider: System.Globalization.CultureInfo.InvariantCulture);
        }

        [Test]
        public void Remaining_hours_never_exceed_the_window_and_a_past_window_has_none() {
            var site = Site(lat: 40, lon: 0, horizon: 0);

            // Not-yet-risen at 10 pm: the whole window is ahead, so remaining ≈ the full integration hours.
            var atRise = new DateTimeOffset(2026, 12, 21, 22, 0, 0, TimeSpan.Zero);
            var lstRise = TonightSkyService.LocalSiderealTimeDeg(atRise, site.LongitudeDeg);
            var riser = TonightSkyService.Rank(
                new[] { SizedObj("RISER", lstRise + 120, 20, majArcmin: 30) }, site, Optics(), atRise, limit: 10).Single();
            Assert.That(riser.RemainingHours, Is.LessThanOrEqualTo(riser.IntegrationHours + 1e-9));
            Assert.That(riser.RemainingHours, Is.EqualTo(riser.IntegrationHours).Within(0.2),
                "a window entirely ahead → all of it remains");

            // Mid-morning after dawn: an object that transited last midnight has already set, so its only
            // dark run tonight is in the past → zero hours remain (but the window fields still describe it).
            var atMorning = new DateTimeOffset(2026, 12, 21, 8, 0, 0, TimeSpan.Zero);
            var lstMidnight = TonightSkyService.LocalSiderealTimeDeg(
                new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero), site.LongitudeDeg);
            var past = TonightSkyService.Rank(
                new[] { SizedObj("PAST", lstMidnight, 20, majArcmin: 30) }, site, Optics(), atMorning, limit: 10).Single();
            Assert.That(past.WindowEndUtc!.Value, Is.LessThan(atMorning), "its window closed before the query");
            Assert.That(past.RemainingHours, Is.EqualTo(0));
        }

        // ─── §36.8 slice 4a: per-request optics + mosaic overrides ───

        [Test]
        public void Mosaic_one_by_one_equals_the_baseline() {
            // The new mosaic params default to 1×1, which must reproduce the existing single-frame ranking
            // exactly — additive change, no behaviour drift when no override is supplied.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var candidates = new[] {
                SizedObj("WIDE", ra, 40, majArcmin: 60),
                SizedObj("SMALL", ra, 40, majArcmin: 8),
            };

            var baseline = TonightSkyService.Rank(candidates, site, Optics(), at, limit: 10);
            var explicit1x1 = TonightSkyService.Rank(candidates, site, Optics(), at, limit: 10,
                mosaicTilesX: 1, mosaicTilesY: 1);

            Assert.That(explicit1x1.Select(o => o.Id), Is.EqualTo(baseline.Select(o => o.Id)));
            for (var i = 0; i < baseline.Count; i++) {
                Assert.That(explicit1x1[i].Score, Is.EqualTo(baseline[i].Score));
                Assert.That(explicit1x1[i].Framing, Is.EqualTo(baseline[i].Framing));
            }
        }

        [Test]
        public void A_larger_mosaic_enlarges_the_fov_and_reframes_a_too_big_target() {
            // A 60′ nebula overflows a single 3000 mm frame (min FOV ≈ 17′ → TooBig), but a 5×5 mosaic
            // quintuples the FOV per axis (min FOV ≈ 86′) so it now fits — the framing classification flips
            // from TooBig to Good and the worth rises with it.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var neb = new[] { SizedObj("NEB", ra, 40, majArcmin: 60) };

            var single = TonightSkyService.Rank(neb, site, Optics(focalLengthMm: 3000), at, limit: 10).Single();
            var mosaic = TonightSkyService.Rank(neb, site, Optics(focalLengthMm: 3000), at, limit: 10,
                mosaicTilesX: 5, mosaicTilesY: 5).Single();

            Assert.That(single.Framing, Is.EqualTo(FramingFit.TooBig), "overflows a single long-focal frame");
            Assert.That(mosaic.Framing, Is.EqualTo(FramingFit.Good), "a 5×5 mosaic enlarges the FOV → fits");
            Assert.That(mosaic.Score, Is.GreaterThan(single.Score), "better framing → higher worth");
        }

        [Test]
        public void Optics_override_drives_framing_through_GetTonight() {
            // GetTonight normally reads the profile optics; a per-request override must take precedence. The
            // profile is a SHORT 448 mm train (frames the 60′ nebula Good), but a LONG 3000 mm override must
            // make the same object overflow — proving the override path (not the profile) is what's used.
            var at = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            var site = Site(lat: 40, lon: 0, horizon: 0);
            var ra = TonightSkyService.LocalSiderealTimeDeg(at, site.LongitudeDeg);
            var dso = new DsoEntryDto("NGC9999", "Wide Nebula", "Neb", ra, 40, 6.0, 60.0, 60.0, 0, 20.0);
            var svc = new TonightSkyService(
                ProfileStore(site, Optics(focalLengthMm: 448)), new FakeCatalog(new[] { dso }));

            var profileFramed = svc.GetTonight(at, limit: 10).Single(o => o.Id == "NGC9999");
            var overridden = svc.GetTonight(at, limit: 10,
                opticsOverride: Optics(focalLengthMm: 3000)).Single(o => o.Id == "NGC9999");

            Assert.That(profileFramed.Framing, Is.EqualTo(FramingFit.Good), "profile 448 mm frames the 60′ nebula well");
            Assert.That(overridden.Framing, Is.EqualTo(FramingFit.TooBig), "the 3000 mm override overflows it");
            Assert.That(overridden.Score, Is.LessThan(profileFramed.Score), "worse framing → lower worth");
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
