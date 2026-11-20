#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using System;
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2073 — the minimal counterweight check on a REST GoTo, until #2048's full model (ARA-1..19)
/// lands. It uses #2048's shared model: the counterweight angle φ is how far the RA axis has
/// turned from counterweight-down (0 = Home, 90 = counterweight horizontal, 180 = straight up), and
/// for a reported hour angle, assuming counterweight-down, φ = 90° − the angular distance from the
/// hour angle to the nearer of 0 h and 12 h. The limit is #2048's 60° default
/// (<c>polar_align.max_cw_angle_deg</c> there; that setting does not exist yet, so the limit is a
/// constant here).
/// <para>Only a target below the pole (|HA| &gt; 6 h) is checked. Near the lower meridian the
/// counterweight is close to horizontal, and the 2026-11-19 incident showed what happens there:
/// two GoTos at HA ≈ −11.1 h that the mount would have made counterweight-up. They were stopped
/// only because tracking was off. Above the pole, a target near the meridian also has φ near 90°,
/// but that is the normal meridian-flip zone that the mount's own meridian limits and Ara's flip
/// handle; a 60° limit there would refuse every target within 2 h of the meridian.</para>
/// <para>With the 60° limit the check refuses a target within 2 h of the lower meridian
/// (10 h &lt; |HA| ≤ 12 h). Static, taking the profile store, like <see cref="HorizonGuard"/>.</para>
/// <para>Only a German equatorial mount has a counterweight that can go up: an alt-az mount or a fork
/// (ASCOM <c>AlignmentMode</c> <c>algAltAz</c> or <c>algPolar</c>) is never refused, or it would lose every
/// target within 2 h of the lower meridian (Polaris for about 4 h a day). A mount whose type is unknown is
/// checked (fail-safe).</para>
/// </summary>
public static partial class CounterweightGuard {

    /// <summary>#2048's default counterweight limit L, in degrees.</summary>
    public const double MaxCounterweightAngleDeg = 60.0;

    /// <summary>The target's hour angle (hours, in (−12, 12]) and counterweight angle when it is
    /// below the pole and past <see cref="MaxCounterweightAngleDeg"/>; null = fine to proceed. A
    /// missing site or a failed read also answers null, as the horizon check does: the guard only
    /// refuses, it never blocks a GoTo because the profile is missing. The hour angle uses the
    /// daemon's clock and the profile longitude, and the target as given (J2000); the
    /// sub-degree epoch difference is far inside the 2 h band. <paramref name="alignmentMode"/> is the
    /// mount's ASCOM type: alt-az and polar (fork) answer null; German polar or null (unknown) are checked.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile read fault must degrade to 'unknown — proceed', never fault the caller. Log-and-recover boundary, as HorizonGuard.")]
    public static (double HourAngleHours, double CounterweightAngleDeg, double LimitDeg)? TargetPastCounterweightLimit(
            IProfileStore? profiles, Coordinates coords, DateTimeOffset atUtc, AlignmentMode? alignmentMode = null, ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(coords);
        if (!HasCounterweight(alignmentMode)) {
            return null;
        }
        try {
            var site = profiles?.GetSiteSettings();
            if (site is null || !double.IsFinite(site.LongitudeDeg)) {
                return null;
            }
            var lstDeg = SiteAstrometry.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
            var haHours = WrapHours((lstDeg - coords.RADegrees) / 15.0);
            var phi = CounterweightAngleDeg(haHours);
            return Math.Abs(haHours) > 6.0 && phi > MaxCounterweightAngleDeg
                ? (haHours, phi, MaxCounterweightAngleDeg)
                : null;
        } catch (Exception ex) {
            if (logger is not null) {
                LogCounterweightCheckFailed(logger, ex);
            }
            return null;
        }
    }

    /// <summary>True for a German equatorial mount, and for an unknown one (fail-safe: checked).</summary>
    internal static bool HasCounterweight(AlignmentMode? alignmentMode) =>
        alignmentMode is null or AlignmentMode.GermanPolar;

    /// <summary>#2048's φ_HA: 90° − the angular distance (degrees) from the hour angle to the nearer
    /// of 0 h and 12 h. 0 at HA ±6 h (Home), 90 at HA 0 h or 12 h.</summary>
    internal static double CounterweightAngleDeg(double hourAngleHours) {
        var a = Math.Abs(WrapHours(hourAngleHours));
        return 90.0 - Math.Min(a, 12.0 - a) * 15.0;
    }

    /// <summary>An hour angle wrapped to (−12, 12].</summary>
    private static double WrapHours(double hours) {
        var h = ((hours % 24.0) + 24.0) % 24.0;
        return h > 12.0 ? h - 24.0 : h;
    }

    [LoggerMessage(EventId = 4250, Level = LogLevel.Warning, Message = "#2073 counterweight check failed; proceeding")]
    private static partial void LogCounterweightCheckFailed(ILogger logger, Exception exception);
}
