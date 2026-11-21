#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1589 — the ONE "is this pointing at or above the profile's clearance limit" check. On 2026-11-05 the
/// scope on the CM4 came close to the mount while it tracked M33 near the zenith, 30 minutes before any
/// flip was due; the only altitude limit was the horizon floor. The limit is the profile's
/// <c>clearance_max_altitude_deg</c>, active only while <c>clearance_limit_enabled</c> is on (off by
/// default). The REST GoTo refuses with it, the mediator's slew core (sequence GoTo, Center, the meridian
/// flip) fails the instruction with it, the flip refuses before it starts, and
/// <see cref="MountClearanceWatch"/> stops tracking with it. Static, taking the profile store, like
/// <see cref="HorizonGuard"/>.
/// <para>The altitude comes from the RA and Dec as given (a J2000 target or the mount's own frame) and the
/// profile site; the J2000/JNow difference moves it by well under half a degree. A missing site or a failed
/// read answers null, as the horizon check does: the guard refuses, it never blocks a move because the
/// profile is missing.</para>
/// </summary>
public static partial class ClearanceGuard {

    /// <summary>The profile's clearance limit in degrees, or null while the limit is off.</summary>
    public static double? LimitDeg(SafetyPoliciesDto? safety) =>
        safety is { ClearanceLimitEnabled: true } ? safety.ClearanceMaxAltitudeDeg : null;

    /// <summary>The altitude (degrees) of RA <paramref name="raHours"/> / Dec <paramref name="decDeg"/> at
    /// <paramref name="atUtc"/> from the site.</summary>
    public static double AltitudeDeg(SiteSettingsDto site, double raHours, double decDeg, DateTimeOffset atUtc) {
        ArgumentNullException.ThrowIfNull(site);
        var lst = SiteAstrometry.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
        var hourAngle = ((lst - raHours * 15.0) % 360.0 + 360.0) % 360.0;
        return SiteAstrometry.AltitudeFromHourAngleDeg(decDeg, site.LatitudeDeg, hourAngle);
    }

    /// <summary>True when <paramref name="altitudeDeg"/> has reached the limit: at it counts, so the stop comes
    /// before the clearance is passed.</summary>
    public static bool Reached(double altitudeDeg, double limitDeg) => altitudeDeg >= limitDeg;

    /// <summary>The target's altitude and the limit when the limit is on and the target is at or above it at
    /// <paramref name="atUtc"/>; null = fine to proceed (the limit is off, the target is below it, or it
    /// cannot be judged).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile read or transform fault must degrade to 'unknown — proceed', never fault the caller. Log-and-recover boundary, as HorizonGuard.")]
    public static (double AltitudeDeg, double LimitDeg)? TargetAboveClearance(IProfileStore? profiles, Coordinates coords,
            DateTimeOffset atUtc, ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(coords);
        try {
            if (LimitDeg(profiles?.GetSafetyPolicies()) is not double limit) {
                return null;
            }
            var site = profiles?.GetSiteSettings();
            if (site is null || !double.IsFinite(site.LatitudeDeg) || !double.IsFinite(site.LongitudeDeg)) {
                return null;
            }
            var altitude = AltitudeDeg(site, coords.RA, coords.Dec, atUtc);
            return Reached(altitude, limit) ? (altitude, limit) : null;
        } catch (Exception ex) {
            if (logger is not null) {
                LogClearanceCheckFailed(logger, ex);
            }
            return null;
        }
    }

    /// <summary>The English sentence a refused move carries: the target's altitude and the limit.</summary>
    public static string RefusalDetail(double altitudeDeg, double limitDeg) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The target is at {altitudeDeg:F1}°, at or above the {limitDeg:F1}° clearance limit, where the camera can reach the mount. Nothing was sent to the mount.");

    [LoggerMessage(EventId = 4255, Level = LogLevel.Warning, Message = "#1589 clearance check failed; proceeding")]
    private static partial void LogClearanceCheckFailed(ILogger logger, Exception exception);
}
