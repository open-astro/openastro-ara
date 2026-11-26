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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The ONE "is this target under the profile's horizon" check: the §38.10 resume refinement and
/// <see cref="TargetRecenter"/> skip a slew with it, and the GoTo endpoint refuses one (#2044).
/// Static, taking the profile store, so each caller keeps the fake it already has.
/// </summary>
public static partial class HorizonGuard {

    /// <summary>§38.10a — the target's current altitude vs. the profile's horizon limit at its
    /// azimuth (custom-horizon interpolation when enabled, else the flat default floor). Null =
    /// fine to proceed: above the limit, or the altitude/limit can't be determined (no profile, a
    /// failed transform) — the guard only ever SKIPS or REFUSES a slew, never blocks one on a missing profile.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile read or transform fault must degrade to 'unknown — proceed', never fault the caller. Log-and-recover boundary.")]
    public static (double AltitudeDeg, double LimitDeg)? TargetBelowHorizon(IProfileStore? profiles, Coordinates coords, ILogger? logger = null, DateTimeOffset? atUtc = null) {
        ArgumentNullException.ThrowIfNull(coords);
        try {
            var site = profiles?.GetSiteSettings();
            if (site is null) {
                return null;
            }
            // SiteAstrometry — the server's ONE managed sky model (shared with the §58.9 flip
            // predictor and Tonight's Sky); sub-degree precision is irrelevant to a horizon floor.
            var lst = SiteAstrometry.LocalSiderealTimeDeg(atUtc ?? DateTimeOffset.UtcNow, site.LongitudeDeg); // #2140 — the flip asks for the flip time
            var hourAngle = (lst - coords.RADegrees % 360.0 + 360.0) % 360.0;
            var altitude = SiteAstrometry.AltitudeFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var azimuth = SiteAstrometry.AzimuthFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var limit = HorizonLimitDeg(profiles, site, azimuth);
            return altitude < limit ? (altitude, limit) : null;
        } catch (Exception ex) {
            if (logger is not null) {
                LogAltitudeCheckFailed(logger, ex);
            }
            return null;
        }
    }

    /// <summary>The horizon altitude at <paramref name="azimuthDeg"/>: the §36 custom-horizon
    /// skyline interpolated via the ONE shared implementation
    /// (<see cref="CustomHorizonValidator.AltitudeAtAzimuth"/>), else the flat default floor.</summary>
    private static double HorizonLimitDeg(IProfileStore? profiles, SiteSettingsDto site, double azimuthDeg) {
        if (!site.UseCustomHorizon) {
            return site.DefaultHorizonAltitudeDeg;
        }
        var (normalized, _) = CustomHorizonValidator.Normalize(profiles?.GetCustomHorizon());
        var points = normalized?.Points;
        if (points is null || points.Count == 0) {
            return site.DefaultHorizonAltitudeDeg;
        }
        return CustomHorizonValidator.AltitudeAtAzimuth(points, azimuthDeg);
    }

    [LoggerMessage(EventId = 4246, Level = LogLevel.Warning, Message = "§38.10a altitude check failed; proceeding")]
    private static partial void LogAltitudeCheckFailed(ILogger logger, Exception exception);
}
