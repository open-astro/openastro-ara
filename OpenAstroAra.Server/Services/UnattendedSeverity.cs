#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §58.10 — severity escalation during unattended hours: while the user is presumably asleep,
/// equipment-impacting notification severities are bumped one level so the §35.5 alarm behaviour
/// engages earlier (the spec's warning→critical / critical→urgent, mapped onto ARA's four-tier
/// enum as Warning→Error and Error→Critical; Critical is already the alarm ceiling and Info
/// stays informational).
///
/// <para><b>The unattended window</b> is the spec's default, computed live from the site:
/// the sun below the astronomical-twilight threshold (−18°) — "from astronomical dusk to
/// astronomical dawn" — reusing the same Meeus sun model the Tonight's Sky planner runs on.
/// No stored clock times to drift; a user-set explicit window is a recorded follow-up.</para>
/// </summary>
public static class UnattendedSeverity {

    // The spec's astronomical-twilight threshold: sun altitude below −18° = fully dark.
    private const double AstronomicalTwilightAltDeg = -18.0;

    /// <summary>§58.10 scopes the bump to "equipment-impacting" events: hardware, the running
    /// sequence, storage, and safety. Software chatter stays put, and the Alarm channel is the
    /// escalation TARGET, not a source.</summary>
    internal static bool IsEquipmentImpacting(NotificationCategory category) =>
        category is NotificationCategory.Equipment or NotificationCategory.Sequence
            or NotificationCategory.Storage or NotificationCategory.Safety;

    /// <summary>One level up: Warning→Error, Error→Critical. Info stays informational (a
    /// bumped housekeeping note would wake nobody for a reason) and Critical is already the
    /// alarm ceiling.</summary>
    internal static NotificationSeverity Escalate(NotificationSeverity severity) => severity switch {
        NotificationSeverity.Warning => NotificationSeverity.Error,
        NotificationSeverity.Error => NotificationSeverity.Critical,
        _ => severity,
    };

    /// <summary>True when the site is in its unattended window at <paramref name="atUtc"/>:
    /// the sun below −18° altitude (astronomical darkness). Pure — the same sun/LST/altitude
    /// math the Tonight's Sky window scan uses.</summary>
    internal static bool IsUnattended(SiteSettingsDto site, DateTimeOffset atUtc) {
        var (sunRa, sunDec) = SiteAstrometry.SunEquatorialDeg(atUtc);
        var lst = SiteAstrometry.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
        var sunAlt = SiteAstrometry.AltitudeFromHourAngleDeg(sunDec, site.LatitudeDeg, lst - sunRa);
        return sunAlt < AstronomicalTwilightAltDeg;
    }
}
