#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The daemon's site-astrometry statics — Meeus low-precision sun/moon positions, sidereal
/// time, and horizontal-coordinate transforms. Extracted from the removed TonightSkyService
/// (PORT_DECISIONS 2026-07-15: planning compute moved to the client; the client's Dart port
/// of this math lives in <c>lib/util/tonight_sky_local.dart</c>) because the EXECUTION side
/// still needs it: MeridianFlipExecutor's altitude safety, PolarAlignGeometry, and
/// UnattendedSeverity's sun gate.
/// <para><b>TWIN ALERT:</b> the client carries a Dart twin of this math in
/// <c>client/openastroara_client/lib/util/tonight_sky_local.dart</c> — any formula or
/// constant change here MUST be mirrored there (the client's parity tests pin the
/// constants and will catch drift).</para>
/// </summary>
public static class SiteAstrometry {

    /// <summary>The sun's apparent equatorial coordinates (RA/Dec, degrees) at <paramref name="atUtc"/>,
    /// via Meeus, <i>Astronomical Algorithms</i> ch. 25 "low accuracy" solar position (≈0.01° — far finer
    /// than a twilight threshold needs): geometric mean longitude + equation of centre → true ecliptic
    /// longitude, then rotate by the obliquity of the ecliptic to equatorial. The sun's ecliptic latitude
    /// is taken as 0.</summary>
    internal static (double RaDeg, double DecDeg) SunEquatorialDeg(DateTimeOffset atUtc) {
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var t = (jd - 2451545.0) / 36525.0;                               // Julian centuries since J2000.0
        // Reduce L0 to [0,360) per Meeus §25 before the trig: L0 grows ~36000°/century, and runtimes
        // with weaker range reduction than glibc (Windows' Cody–Waite, WASM) lose precision on the raw
        // large argument far from J2000.
        var l0 = Mod360(280.46646 + 36000.76983 * t + 0.0003032 * t * t); // geometric mean longitude (deg)
        var m = Deg2Rad(Mod360(357.52911 + 35999.05029 * t - 0.0001537 * t * t)); // mean anomaly (rad)
        var c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
              + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
              + 0.000289 * Math.Sin(3 * m);                               // equation of centre (deg)
        var lambda = Deg2Rad(l0 + c);                                     // true ecliptic longitude (rad)
        var eps = Deg2Rad(23.439);                                        // mean obliquity (low-precision)
        var ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        var dec = Math.Asin(Math.Clamp(Math.Sin(eps) * Math.Sin(lambda), -1.0, 1.0));
        return (Mod360(Rad2Deg(ra)), Rad2Deg(dec));
    }

    /// <summary>The moon's apparent equatorial coordinates (RA/Dec, degrees) at <paramref name="atUtc"/>,
    /// via the Astronomical Almanac low-precision lunar series (the truncation Meeus ch. 47 is built
    /// from): mean longitude plus the six largest longitude terms and the four largest latitude terms,
    /// rotated by the mean obliquity to equatorial. Accuracy ≈ 0.3° — an advisory separation figure,
    /// not an ephemeris. Geocentric: topocentric parallax can shift the moon up to ~1°, immaterial for
    /// a whole-degree advisory readout.</summary>
    internal static (double RaDeg, double DecDeg) MoonEquatorialDeg(DateTimeOffset atUtc) {
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var t = (jd - 2451545.0) / 36525.0;                               // Julian centuries since J2000.0
        // Ecliptic longitude λ / latitude β (deg). Every trig argument goes through SinDeg's Mod360,
        // matching SunEquatorialDeg's range-reduction discipline (the raw arguments grow ~481000°/century).
        var lambda = Mod360(218.32 + 481267.881 * t)
            + 6.29 * SinDeg(135.0 + 477198.87 * t)
            - 1.27 * SinDeg(259.3 - 413335.36 * t)
            + 0.66 * SinDeg(235.7 + 890534.22 * t)
            + 0.21 * SinDeg(269.9 + 954397.74 * t)
            - 0.19 * SinDeg(357.5 + 35999.05 * t)
            - 0.11 * SinDeg(186.5 + 966404.03 * t);
        var beta = 5.13 * SinDeg(93.3 + 483202.02 * t)
            + 0.28 * SinDeg(228.2 + 960400.89 * t)
            - 0.28 * SinDeg(318.3 + 6003.15 * t)
            - 0.17 * SinDeg(217.6 - 407332.21 * t);
        var l = Deg2Rad(lambda);
        var b = Deg2Rad(beta);
        var eps = Deg2Rad(23.439);                                        // mean obliquity (low-precision)
        // Full ecliptic→equatorial rotation — the moon has real ecliptic latitude (up to ±5.1°),
        // so the sun's β=0 shortcut doesn't apply here.
        var x = Math.Cos(b) * Math.Cos(l);
        var y = Math.Cos(eps) * Math.Cos(b) * Math.Sin(l) - Math.Sin(eps) * Math.Sin(b);
        var z = Math.Sin(eps) * Math.Cos(b) * Math.Sin(l) + Math.Cos(eps) * Math.Sin(b);
        var ra = Math.Atan2(y, x);
        var dec = Math.Asin(Math.Clamp(z, -1.0, 1.0));
        return (Mod360(Rad2Deg(ra)), Rad2Deg(dec));
    }

    /// <summary>The fraction of the moon's disc that is illuminated at <paramref name="atUtc"/>, 0–1.
    /// From the sun–moon elongation ψ: with the sun treated as infinitely distant the phase angle is
    /// 180° − ψ, so k = (1 + cos(180° − ψ))/2 = (1 − cos ψ)/2 (Meeus ch. 48; the finite-distance
    /// correction moves k by well under a percent).</summary>
    internal static double MoonIlluminatedFraction(DateTimeOffset atUtc) {
        var (sunRa, sunDec) = SunEquatorialDeg(atUtc);
        var (moonRa, moonDec) = MoonEquatorialDeg(atUtc);
        var psi = Deg2Rad(AngularSeparationDeg(sunRa, sunDec, moonRa, moonDec));
        return (1.0 - Math.Cos(psi)) / 2.0;
    }

    /// <summary>Great-circle separation (degrees) between two equatorial positions:
    /// cos ψ = sin δ₁ sin δ₂ + cos δ₁ cos δ₂ cos Δα. Fine for advisory readouts (the acos form
    /// loses precision only below ~0.1°, far under a whole-degree display).</summary>
    internal static double AngularSeparationDeg(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
        var d1 = Deg2Rad(dec1Deg);
        var d2 = Deg2Rad(dec2Deg);
        var cosSep = Math.Sin(d1) * Math.Sin(d2)
                   + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(Deg2Rad(ra1Deg - ra2Deg));
        return Rad2Deg(Math.Acos(Math.Clamp(cosSep, -1.0, 1.0)));
    }

    /// <summary>Local apparent sidereal time in degrees (Meeus low-precision GMST + east-positive
    /// longitude). Arcminute-level accuracy — far finer than altitude ranking needs.</summary>
    internal static double LocalSiderealTimeDeg(DateTimeOffset atUtc, double longitudeDeg) {
        // Julian Date of the instant (Unix epoch JD = 2440587.5).
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var d = jd - 2451545.0; // days from J2000.0
        // Reduce GMST to [0,360) before combining, matching SunEquatorialDeg's L0 handling — the raw
        // value grows ~361°/day, so keep the same range-reduction discipline across the file.
        var gmst = Mod360(280.46061837 + 360.98564736629 * d);
        return Mod360(gmst + longitudeDeg);
    }

    /// <summary>Altitude (deg) of an object at hour angle <paramref name="hourAngleDeg"/> seen from
    /// <paramref name="latDeg"/>: sin(alt) = sin δ sin φ + cos δ cos φ cos H.</summary>
    internal static double AltitudeFromHourAngleDeg(double decDeg, double latDeg, double hourAngleDeg) {
        var dec = Deg2Rad(decDeg);
        var lat = Deg2Rad(latDeg);
        var h = Deg2Rad(hourAngleDeg);
        var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(h);
        return Rad2Deg(Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)));
    }

    /// <summary>Compass azimuth (deg from north, increasing eastward, [0,360)) of an object at hour
    /// angle <paramref name="hourAngleDeg"/> seen from <paramref name="latDeg"/>:
    /// az = atan2(−sin H · cos δ, sin δ · cos φ − cos δ · sin φ · cos H). The horizontal companion of
    /// <see cref="AltitudeFromHourAngleDeg"/>, used to index the §36 custom-horizon skyline.</summary>
    internal static double AzimuthFromHourAngleDeg(double decDeg, double latDeg, double hourAngleDeg) {
        var dec = Deg2Rad(decDeg);
        var lat = Deg2Rad(latDeg);
        var h = Deg2Rad(hourAngleDeg);
        var az = Math.Atan2(
            -Math.Sin(h) * Math.Cos(dec),
            Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(h));
        return Mod360(Rad2Deg(az));
    }

    /// <summary>The equatorial (J2000) coordinates of the point at horizontal altitude/azimuth
    /// <paramref name="altDeg"/>/<paramref name="azDeg"/> (azimuth measured from north, increasing
    /// eastward) seen from <paramref name="latDeg"/> at local sidereal time <paramref name="lstDeg"/>.
    /// The inverse of <see cref="AltitudeFromHourAngleDeg"/>; used to draw the local horizon as a curve
    /// on the equatorial atlas. At a geographic pole (cos φ ≈ 0) or for a point at a celestial pole
    /// (cos δ ≈ 0) the hour angle is degenerate, so it is pinned to 0 rather than dividing by zero.</summary>
    internal static (double RaDeg, double DecDeg) EquatorialFromAltAz(
            double altDeg, double azDeg, double latDeg, double lstDeg) {
        var alt = Deg2Rad(altDeg);
        var az = Deg2Rad(azDeg);
        var lat = Deg2Rad(latDeg);
        var sinDec = Math.Sin(alt) * Math.Sin(lat) + Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az);
        var dec = Math.Asin(Math.Clamp(sinDec, -1.0, 1.0));
        var cosLat = Math.Cos(lat);
        var cosDec = Math.Cos(dec);
        double hourAngleDeg;
        // 1e-6 ≈ 0.2 arcsec from a pole — finer than any overlay resolves, and well clear of the
        // ~1e8-magnitude sinH/cosH regime where one term could overflow before Atan2 normalises them.
        if (Math.Abs(cosLat) < 1e-6 || Math.Abs(cosDec) < 1e-6) {
            hourAngleDeg = 0.0;
        } else {
            var sinH = -Math.Sin(az) * Math.Cos(alt) / cosDec;
            var cosH = (Math.Sin(alt) - Math.Sin(dec) * Math.Sin(lat)) / (cosDec * cosLat);
            hourAngleDeg = Rad2Deg(Math.Atan2(sinH, cosH));
        }
        return (Mod360(lstDeg - hourAngleDeg), Rad2Deg(dec));
    }

    /// <summary>The object's highest possible altitude from this latitude — geometric upper culmination
    /// on the meridian: 90 − |φ − δ|, clamped to [−90, 90]. Ignores atmospheric refraction and the
    /// lower-transit altitude of circumpolar objects; sufficient as a ranking hint, not a precise rise.</summary>
    internal static double MaxAltitudeDeg(double decDeg, double latDeg) =>
        Math.Clamp(90.0 - Math.Abs(latDeg - decDeg), -90.0, 90.0);

    internal static double Mod360(double x) {
        x %= 360.0;
        return x < 0 ? x + 360.0 : x;
    }

    internal static double Deg2Rad(double d) => d * Math.PI / 180.0;
    internal static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    /// <summary>sin of an angle given in degrees, range-reduced first (the lunar series' raw
    /// arguments are huge).</summary>
    internal static double SinDeg(double deg) => Math.Sin(Deg2Rad(Mod360(deg)));
}
