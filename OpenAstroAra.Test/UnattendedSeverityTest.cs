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

    /// <summary>§58.10 pure helpers: the one-level severity bump, the equipment-impacting
    /// category scope, and the astronomical-darkness window over the shared sun model.</summary>
    [TestFixture]
    public class UnattendedSeverityTest {

        [Test]
        public void Escalate_bumps_one_level_with_a_ceiling_and_an_info_floor() {
            Assert.That(UnattendedSeverity.Escalate(NotificationSeverity.Warning),
                Is.EqualTo(NotificationSeverity.Error));
            Assert.That(UnattendedSeverity.Escalate(NotificationSeverity.Error),
                Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(UnattendedSeverity.Escalate(NotificationSeverity.Critical),
                Is.EqualTo(NotificationSeverity.Critical), "already the alarm ceiling");
            Assert.That(UnattendedSeverity.Escalate(NotificationSeverity.Info),
                Is.EqualTo(NotificationSeverity.Info), "housekeeping notes wake nobody");
        }

        [Test]
        public void Equipment_impacting_scope_matches_the_spec() {
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Equipment), Is.True);
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Sequence), Is.True);
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Storage), Is.True);
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Safety), Is.True);
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Software), Is.False);
            Assert.That(UnattendedSeverity.IsEquipmentImpacting(NotificationCategory.Alarm), Is.False,
                "the Alarm channel is the escalation TARGET, not a source");
        }

        private static SafetyPoliciesDto Window(string? start, string? end) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                UnattendedWindowStart: start, UnattendedWindowEnd: end);

        [Test]
        public void An_explicit_window_replaces_the_sun_and_wraps_past_midnight() {
            // #1138 — a remote observatory in another zone: the site clock, not the sun, decides.
            var site = new SiteSettingsDto(SiteName: "Remote", LatitudeDeg: 40, LongitudeDeg: 0,
                ElevationM: 0, TimeZone: "America/Los_Angeles", UseCustomHorizon: false,
                DefaultHorizonAltitudeDeg: 0, BortleClass: 4, TypicalSeeingArcsec: 2.5,
                TwilightDefinition: "astronomical");
            var window = Window("22:00", "06:00");
            // 2026-12-21 10:00 UTC = 02:00 PST → inside; the sun model at lon 0 would say daylight.
            Assert.That(UnattendedSeverity.IsUnattended(site, window, new DateTimeOffset(2026, 12, 21, 10, 0, 0, TimeSpan.Zero)), Is.True);
            Assert.That(UnattendedSeverity.IsUnattended(site, null, new DateTimeOffset(2026, 12, 21, 10, 0, 0, TimeSpan.Zero)), Is.False, "the sun at lon 0 at 10:00 UTC is up");
            // 2026-12-21 20:00 UTC = 12:00 PST → outside, although the sun at lon 0 is down.
            Assert.That(UnattendedSeverity.IsUnattended(site, window, new DateTimeOffset(2026, 12, 21, 20, 0, 0, TimeSpan.Zero)), Is.False);
            Assert.That(UnattendedSeverity.IsUnattended(site, null, new DateTimeOffset(2026, 12, 21, 20, 0, 0, TimeSpan.Zero)), Is.True);
        }

        [Test]
        public void A_blank_or_malformed_window_falls_back_to_the_sun() {
            var site = new SiteSettingsDto(SiteName: "Test", LatitudeDeg: 40, LongitudeDeg: 0,
                ElevationM: 0, TimeZone: "UTC", UseCustomHorizon: false,
                DefaultHorizonAltitudeDeg: 0, BortleClass: 4, TypicalSeeingArcsec: 2.5,
                TwilightDefinition: "astronomical");
            var midnight = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
            Assert.That(UnattendedSeverity.IsUnattended(site, Window(null, null), midnight), Is.True);
            Assert.That(UnattendedSeverity.IsUnattended(site, Window("22:00", ""), midnight), Is.True, "one end only: the sun decides");
            Assert.That(UnattendedSeverity.IsUnattended(site, Window("ten pm", "06:00"), midnight), Is.True);
            Assert.That(UnattendedSeverity.IsUnattended(site, Window("22:00", "22:00"), midnight), Is.True, "an empty window is no window");
            Assert.That(UnattendedSeverity.TryParseWindow("7:30", "06:00", out var s, out var e), Is.True);
            Assert.That((s, e), Is.EqualTo((new TimeSpan(7, 30, 0), new TimeSpan(6, 0, 0))));
            Assert.That(UnattendedSeverity.InWindow(new TimeSpan(23, 0, 0), new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0)), Is.True);
            Assert.That(UnattendedSeverity.InWindow(new TimeSpan(12, 0, 0), new TimeSpan(22, 0, 0), new TimeSpan(6, 0, 0)), Is.False);
            Assert.That(UnattendedSeverity.InWindow(new TimeSpan(9, 0, 0), new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)), Is.True, "a daytime window for a night-shift operator");
            Assert.That(UnattendedSeverity.ToSiteLocal("Not/AZone", midnight), Is.EqualTo(midnight), "unknown zone: UTC");
        }

        [Test]
        public void The_window_is_astronomical_darkness_at_the_site() {
            var site = new SiteSettingsDto(SiteName: "Test", LatitudeDeg: 40, LongitudeDeg: 0,
                ElevationM: 0, TimeZone: "UTC", UseCustomHorizon: false,
                DefaultHorizonAltitudeDeg: 0, BortleClass: 4, TypicalSeeingArcsec: 2.5,
                TwilightDefinition: "astronomical");
            // Winter-solstice midnight at lat 40 N, lon 0: sun ~73° below the horizon.
            Assert.That(UnattendedSeverity.IsUnattended(site,
                new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero)), Is.True);
            // Same day at noon: sun ~26° up.
            Assert.That(UnattendedSeverity.IsUnattended(site,
                new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero)), Is.False);
            // Civil dusk (sun a few degrees below) is NOT yet unattended — the user is
            // plausibly still at the rig; only astronomical darkness bumps severities.
            Assert.That(UnattendedSeverity.IsUnattended(site,
                new DateTimeOffset(2026, 12, 21, 17, 0, 0, TimeSpan.Zero)), Is.False);
        }
    }
}
