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
/// No stored clock times to drift. #1138: a user-set explicit window (site-local <c>HH:mm</c>
/// start/end on the safety policies, wrapping past midnight) replaces the sun when both are set —
/// for a schedule the sun does not describe (a remote observatory in another time zone).</para>
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
    internal static bool IsUnattended(SiteSettingsDto site, DateTimeOffset atUtc) => IsUnattended(site, null, atUtc);

    /// <summary>The window with the #1138 explicit override: when <paramref name="safety"/> carries a
    /// parseable start AND end, the site-local clock decides; otherwise the sun does.</summary>
    internal static bool IsUnattended(SiteSettingsDto site, SafetyPoliciesDto? safety, DateTimeOffset atUtc) {
        if (safety is not null && TryParseWindow(safety.UnattendedWindowStart, safety.UnattendedWindowEnd, out var start, out var end)) {
            var local = ToSiteLocal(site?.TimeZone, atUtc).TimeOfDay;
            return InWindow(local, start, end);
        }
        return IsAstronomicalDarkness(site!, atUtc);
    }

    private static readonly System.Text.RegularExpressions.Regex ClockShape = new(@"^(\d{1,2}):(\d{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Both ends parse as <c>HH:mm</c> (24 h) and differ.</summary>
    internal static bool TryParseWindow(string? startText, string? endText, out TimeSpan start, out TimeSpan end) {
        start = end = default;
        return TryParseClock(startText, out start) && TryParseClock(endText, out end) && start != end;
    }

    private static bool TryParseClock(string? text, out TimeSpan value) {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        // The same shape the client validates: H:mm / HH:mm, 0–23 hours, 0–59 minutes ("6:75" is not 7:15).
        var m = ClockShape.Match(text.Trim());
        if (!m.Success) {
            return false;
        }
        var hours = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59) {
            return false;
        }
        value = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>Inside [start, end), wrapping past midnight when end &lt; start (22:00 → 06:00).</summary>
    internal static bool InWindow(TimeSpan localTimeOfDay, TimeSpan start, TimeSpan end) =>
        start < end
            ? localTimeOfDay >= start && localTimeOfDay < end
            : localTimeOfDay >= start || localTimeOfDay < end;

    /// <summary>The site's wall clock for <paramref name="atUtc"/>: the IANA/Windows zone id from the site
    /// settings, UTC when it is blank or unknown to this host.</summary>
    internal static DateTimeOffset ToSiteLocal(string? timeZoneId, DateTimeOffset atUtc) {
        if (string.IsNullOrWhiteSpace(timeZoneId)) {
            return atUtc.ToUniversalTime();
        }
        try {
            return TimeZoneInfo.ConvertTime(atUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()));
        } catch (TimeZoneNotFoundException) {
            return atUtc.ToUniversalTime();
        } catch (InvalidTimeZoneException) {
            return atUtc.ToUniversalTime();
        } catch (System.Security.SecurityException) {
            return atUtc.ToUniversalTime();
        }
    }

    private static bool IsAstronomicalDarkness(SiteSettingsDto site, DateTimeOffset atUtc) {
        var (sunRa, sunDec) = SiteAstrometry.SunEquatorialDeg(atUtc);
        var lst = SiteAstrometry.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
        var sunAlt = SiteAstrometry.AltitudeFromHourAngleDeg(sunDec, site.LatitudeDeg, lst - sunRa);
        return sunAlt < AstronomicalTwilightAltDeg;
    }
}
