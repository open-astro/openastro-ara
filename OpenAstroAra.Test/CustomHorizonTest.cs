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
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>§36 custom terrain horizon: validation/canonical form, the shared
    /// azimuth→altitude interpolation, and both consumers (overlay + Tonight's Sky
    /// visibility), plus the profile plumbing (§70 strip, normalizer back-fill).</summary>
    [TestFixture]
    public class CustomHorizonTest {

        private static CustomHorizonDto Horizon(params (double Az, double Alt)[] pts) =>
            new(pts.Select(p => new CustomHorizonPointDto(p.Az, p.Alt)).ToList());

        private static SiteSettingsDto Site(bool useCustom, double flatAlt = 20.0, double lat = 45.0) =>
            new(SiteName: "Test", LatitudeDeg: lat, LongitudeDeg: 0.0, ElevationM: 0.0,
                TimeZone: "UTC", UseCustomHorizon: useCustom, DefaultHorizonAltitudeDeg: flatAlt,
                BortleClass: 6, TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        // ── Normalize ───────────────────────────────────────────────────────

        [Test]
        public void Normalize_sorts_dedupes_keep_last_and_wraps_360_onto_0() {
            var (normalized, error) = CustomHorizonValidator.Normalize(
                Horizon((180, 40), (0, 10), (360, 25), (180, 35)));

            Assert.That(error, Is.Null);
            // 360 wraps onto 0 (keep-last beats the (0,10) entry); duplicate 180 keeps 35.
            Assert.That(normalized!.Points.Select(p => (p.AzimuthDeg, p.AltitudeDeg)),
                Is.EqualTo(new[] { (0.0, 25.0), (180.0, 35.0) }));
        }

        [Test]
        public void Normalize_accepts_empty_and_rejects_out_of_range() {
            Assert.That(CustomHorizonValidator.Normalize(new CustomHorizonDto([])).Error, Is.Null);
            Assert.That(CustomHorizonValidator.Normalize(null).Error, Is.Null);
            Assert.That(CustomHorizonValidator.Normalize(Horizon((-1, 10))).Error, Does.Contain("Azimuth"));
            Assert.That(CustomHorizonValidator.Normalize(Horizon((10, 95))).Error, Does.Contain("Altitude"));
            Assert.That(CustomHorizonValidator.Normalize(Horizon((10, -15))).Error, Does.Contain("Altitude"));
            Assert.That(CustomHorizonValidator.Normalize(Horizon((double.NaN, 10))).Error, Does.Contain("Azimuth"));

            var tooMany = new CustomHorizonDto(
                Enumerable.Range(0, 400).Select(i => new CustomHorizonPointDto(i * 0.9, 10)).ToList());
            Assert.That(CustomHorizonValidator.Normalize(tooMany).Error, Does.Contain("At most"));
        }

        // ── AltitudeAtAzimuth ───────────────────────────────────────────────

        [Test]
        public void Interpolation_covers_vertices_segments_and_the_wraparound() {
            var pts = CustomHorizonValidator.Normalize(
                Horizon((0, 10), (90, 30), (270, 50))).Normalized!.Points;

            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(pts, 0), Is.EqualTo(10).Within(1e-9), "exact vertex");
            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(pts, 45), Is.EqualTo(20).Within(1e-9), "mid-segment");
            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(pts, 180), Is.EqualTo(40).Within(1e-9));
            // Wraparound 270→(0+360): quarter across the 90-degree gap at az 315.
            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(pts, 315), Is.EqualTo(30).Within(1e-9), "wrap segment");
            // Negative azimuth normalizes into range: −45 ≡ 315.
            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(pts, -45), Is.EqualTo(30).Within(1e-9));
            // A single vertex is a flat horizon.
            Assert.That(CustomHorizonValidator.AltitudeAtAzimuth(
                [new CustomHorizonPointDto(120, 33)], 5), Is.EqualTo(33));
        }

        // ── AzimuthFromHourAngleDeg sanity ──────────────────────────────────

        [Test]
        public void Azimuth_from_hour_angle_matches_the_compass() {
            // Upper transit south of zenith (dec < lat, H = 0) → due south.
            Assert.That(SiteAstrometry.AzimuthFromHourAngleDeg(20, 45, 0), Is.EqualTo(180).Within(1e-6));
            // A dec-0 object 6h before transit (H = −90) rises due east from anywhere.
            Assert.That(SiteAstrometry.AzimuthFromHourAngleDeg(0, 45, -90), Is.EqualTo(90).Within(1e-6));
            // …and 6h after transit sets due west.
            Assert.That(SiteAstrometry.AzimuthFromHourAngleDeg(0, 45, 90), Is.EqualTo(270).Within(1e-6));
        }

        // ── HorizonService overlay ──────────────────────────────────────────

        [Test]
        public void Overlay_serves_the_skyline_when_enabled_and_flags_only_the_empty_case() {
            var at = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var custom = CustomHorizonValidator.Normalize(Horizon((0, 5), (180, 45))).Normalized!;

            var honored = HorizonService.Compute(Site(useCustom: true), at, custom);
            Assert.That(honored.CustomHorizonIgnored, Is.False, "the skyline is being served");

            var requestedButEmpty = HorizonService.Compute(Site(useCustom: true), at, new CustomHorizonDto([]));
            Assert.That(requestedButEmpty.CustomHorizonIgnored, Is.True);

            var disabled = HorizonService.Compute(Site(useCustom: false), at, custom);
            Assert.That(disabled.CustomHorizonIgnored, Is.False);

            // The honored overlay differs from the flat one exactly where the skyline
            // does: both curves share the azimuth grid; at due south the 45-degree
            // wall projects to a different declination than the flat 20-degree ring
            // (same LST, same azimuth — only the altitude changed).
            var flat = HorizonService.Compute(Site(useCustom: false), at, custom);
            var southIdx = 180 / 2; // AzimuthStepDeg = 2
            Assert.That(honored.Points[southIdx].AzimuthDeg, Is.EqualTo(180));
            Assert.That(honored.Points[southIdx].DecDeg,
                Is.Not.EqualTo(flat.Points[southIdx].DecDeg).Within(1e-6));
        }

        // ── Tonight's Sky visibility ────────────────────────────────────────

        // The skyline-wall-vs-flat-horizon ranking test moved to the CLIENT with the
        // Tonight's Sky ranker (PORT_DECISIONS 2026-07-15); the local ranker's custom-
        // horizon support is a tracked follow-up (the points aren't in the offline
        // profile cache yet). The validator + profile plumbing below are unchanged.

        // ── profile plumbing ────────────────────────────────────────────────

        [Test]
        public void Store_round_trips_and_snapshot_apply_carries_the_horizon() {
            var store = new InMemoryProfileStore();
            Assert.That(store.GetCustomHorizon().Points, Is.Empty, "empty until entered");

            var custom = CustomHorizonValidator.Normalize(Horizon((10, 12), (200, 30))).Normalized!;
            store.PutCustomHorizon(custom);
            Assert.That(store.GetCustomHorizon().Points, Has.Count.EqualTo(2));

            // Capture → Apply (profile-select's push) must carry the section.
            var snap = ProfileStoreSnapshot.Capture(store);
            var target = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(target, snap);
            Assert.That(target.GetCustomHorizon().Points, Has.Count.EqualTo(2));
        }

        [Test]
        public void Normalizer_backfills_a_missing_or_null_section() {
            var normalized = ProfileSnapshotNormalizer.Normalize(
                ProfileSnapshotNormalizer.Defaults with { CustomHorizon = null });
            Assert.That(normalized.CustomHorizon, Is.Not.Null);
            Assert.That(normalized.CustomHorizon!.Points, Is.Empty);
        }
    }
}
