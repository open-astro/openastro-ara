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
