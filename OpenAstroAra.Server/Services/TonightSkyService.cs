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
using System.Collections.Generic;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>§36/§25.5 Tonight's Sky — ranks a curated deep-sky list by how high each object
/// sits above the active profile's site horizon at a given instant.</summary>
public interface ITonightSkyService {
    /// <summary>The curated objects above the site horizon at <paramref name="atUtc"/>, highest
    /// first, capped at <paramref name="limit"/>. As an internal convenience a non-positive
    /// <paramref name="limit"/> returns all of them — but the public <c>GET /planning/tonight</c>
    /// endpoint rejects <c>limit &lt; 1</c> with a 400, so that path only applies to direct callers.
    /// Reads the site lat/long + horizon from the active profile.
    /// <para><b>Slice-1 inclusion boundary:</b> only objects already above the horizon AT
    /// <paramref name="atUtc"/> are listed (the altitude-ranking gate). A target that hasn't risen yet —
    /// e.g. at a sunset query — is omitted even though its <c>WindowStartUtc</c>/<c>IntegrationHours</c>
    /// would describe a fine night. Surfacing not-yet-risen targets is the equipment-aware scoring pass
    /// (slice 2, see <c>design/TONIGHT_SKY.md</c>); until then a planner run reflects "up right now",
    /// and the per-object window/transit/hours describe tonight's dark window for those objects.</para></summary>
    IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit);
}

public sealed class TonightSkyService : ITonightSkyService {
    private readonly IProfileStore _profileStore;
    private readonly ISkyCatalogService _catalog;
    private IReadOnlyList<CatalogObject>? _candidates; // the culled candidate set, cached once the real catalog loads

    // Cull bound: OpenNGC carries ~13k objects, most far too faint to image. A magnitude floor keeps
    // the candidate set to the realistically-shootable ones (and the per-object window math affordable).
    private const double MaxCandidateMagnitude = 12.0;

    public TonightSkyService(IProfileStore profileStore, ISkyCatalogService catalog) {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit) =>
        Rank(BuildCandidates(), _profileStore.GetSiteSettings(), atUtc, limit);

    /// <summary>The candidate set: the installed OpenNGC catalog culled to the realistically-shootable
    /// objects, or the hardcoded starter <see cref="Catalog"/> when openngc-dso isn't installed (or the
    /// cull leaves nothing) — so the endpoint still returns something useful with no sky-data.</summary>
    private IReadOnlyList<CatalogObject> BuildCandidates() {
        // Cache the culled set: the magnitude floor is static, so once the real catalog is loaded the
        // filtered list never changes. The hardcoded fallback is deliberately NOT cached — openngc-dso
        // can be installed at runtime (DataManager) and SkyCatalogService re-checks the file each call,
        // so staying uncached on the fallback lets a later install take effect without a daemon restart.
        // NOTE: a catalog *update* (re-downloading a newer openngc-dso over an installed one) is NOT
        // picked up live — both this cache and SkyCatalogService._dsoEntries are process-lifetime; a
        // re-cull on sky-data change is a later DataManager-integration slice.
        var cached = Volatile.Read(ref _candidates);
        if (cached is not null) {
            return cached;
        }
        var dsos = _catalog.GetAllDsos(CancellationToken.None);
        if (dsos is null || dsos.Count == 0) {
            return Catalog;
        }
        var list = new List<CatalogObject>(dsos.Count);
        foreach (var d in dsos) {
            // Keep it simple (slice 1): a magnitude floor. Objects with no recorded magnitude are
            // dropped here — size-based inclusion is a later slice's framing-fit concern.
            if (d.Magnitude is not { } mag || mag > MaxCandidateMagnitude) {
                continue;
            }
            list.Add(new CatalogObject(
                d.Name, d.CommonName ?? d.Name, d.Type, mag, d.RaDeg, d.DecDeg,
                d.MajAxArcmin, d.MinAxArcmin, d.PosAngleDeg, d.SurfaceBrightness));
        }
        if (list.Count == 0) {
            return Catalog;   // catalog present but cull empty (unexpected) — fall back, don't cache
        }
        IReadOnlyList<CatalogObject> result = list;
        return Interlocked.CompareExchange(ref _candidates, result, null) ?? result;
    }

    /// <summary>One deep-sky object: catalog id + common name, type, magnitude, J2000 RA/Dec (deg), and
    /// the optional apparent size / position angle / surface brightness when the catalog carried them.</summary>
    internal readonly record struct CatalogObject(
        string Id, string Name, string Type, double Magnitude, double RaDeg, double DecDeg,
        double? SizeMajArcmin = null, double? SizeMinArcmin = null,
        double? PosAngleDeg = null, double? SurfaceBrightness = null);

    // A small spread-across-the-sky starter set so something worthwhile is always up. J2000 positions.
    // The full smart-culled catalog (§36.8 / §55.1) supersedes this hardcoded list later.
    internal static readonly IReadOnlyList<CatalogObject> Catalog = new[] {
        new CatalogObject("M31", "Andromeda Galaxy", "galaxy", 3.4, 10.685, 41.269),
        new CatalogObject("M33", "Triangulum Galaxy", "galaxy", 5.7, 23.462, 30.660),
        new CatalogObject("M45", "Pleiades", "cluster", 1.6, 56.750, 24.117),
        new CatalogObject("M1", "Crab Nebula", "nebula", 8.4, 83.633, 22.014),
        new CatalogObject("M42", "Orion Nebula", "nebula", 4.0, 83.822, -5.391),
        new CatalogObject("NGC2237", "Rosette Nebula", "nebula", 9.0, 97.950, 5.050),
        new CatalogObject("M81", "Bode's Galaxy", "galaxy", 6.9, 148.888, 69.065),
        new CatalogObject("M97", "Owl Nebula", "nebula", 9.9, 168.699, 55.019),
        new CatalogObject("M104", "Sombrero Galaxy", "galaxy", 8.0, 189.998, -11.623),
        new CatalogObject("M63", "Sunflower Galaxy", "galaxy", 8.6, 198.955, 42.029),
        new CatalogObject("M51", "Whirlpool Galaxy", "galaxy", 8.4, 202.470, 47.195),
        new CatalogObject("M101", "Pinwheel Galaxy", "galaxy", 7.9, 210.802, 54.349),
        new CatalogObject("M13", "Hercules Cluster", "cluster", 5.8, 250.423, 36.461),
        new CatalogObject("M20", "Trifid Nebula", "nebula", 6.3, 270.600, -23.030),
        new CatalogObject("M8", "Lagoon Nebula", "nebula", 6.0, 270.904, -24.387),
        new CatalogObject("M16", "Eagle Nebula", "nebula", 6.0, 274.700, -13.807),
        new CatalogObject("M17", "Omega Nebula", "nebula", 6.0, 275.196, -16.171),
        new CatalogObject("M57", "Ring Nebula", "nebula", 8.8, 283.396, 33.029),
        new CatalogObject("M27", "Dumbbell Nebula", "nebula", 7.4, 299.901, 22.721),
        new CatalogObject("NGC7000", "North America Nebula", "nebula", 4.0, 314.750, 44.330),
    };

    /// <summary>Pure ranking over the hardcoded starter <see cref="Catalog"/>. Retained for the no-data
    /// path and the existing unit tests; <see cref="GetTonight"/> ranks the OpenNGC candidates instead.</summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(SiteSettingsDto site, DateTimeOffset atUtc, int limit) =>
        Rank(Catalog, site, atUtc, limit);

    // Window scan resolution: sample altitude + sun-altitude every 5 minutes across ±12 h of atUtc.
    // 5 min is finer than the ~1-minute-per-degree-of-altitude rate near the horizon matters for an
    // "hours tonight" figure, and ±12 h always spans one whole night either side of any instant.
    private const int WindowStepMinutes = 5;
    private const int WindowHalfSpanMinutes = 12 * 60;
    // LST advances at this many degrees per solar day — the rate baked into LocalSiderealTimeDeg's GMST
    // term; reused to solve for the transit instant analytically (hour angle = 0).
    private const double SiderealDegPerDay = 360.98564736629;

    /// <summary>Ranks <paramref name="catalog"/> for <paramref name="site"/> at <paramref name="atUtc"/>:
    /// objects above the site horizon right then, altitude-descending, capped at <paramref name="limit"/>.
    /// Each returned object also carries its visibility window tonight, transit, and integration hours.
    /// (Scoring / framing-fit are a later slice — ranking stays altitude-descending here.)</summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(
            IReadOnlyList<CatalogObject> catalog, SiteSettingsDto site, DateTimeOffset atUtc, int limit) {
        var horizon = site.DefaultHorizonAltitudeDeg;
        var lat = site.LatitudeDeg;
        var lon = site.LongitudeDeg;
        var lst0 = LocalSiderealTimeDeg(atUtc, lon);

        // Pass 1 (cheap): everything above the horizon right now, keyed by its TRUE (unrounded) altitude
        // so the sort can't lose real order to rounding; only this set gets the costly window math.
        var visible = new List<(double Alt, CatalogObject Obj)>(catalog.Count);
        foreach (var o in catalog) {
            var alt = AltitudeFromHourAngleDeg(o.DecDeg, lat, Mod360(lst0 - o.RaDeg));
            if (alt >= horizon) {
                visible.Add((alt, o));
            }
        }
        // Highest first; the catalog id is a stable tie-break for a genuine exact tie.
        visible.Sort((a, b) => {
            var c = b.Alt.CompareTo(a.Alt);
            return c != 0 ? c : string.CompareOrdinal(a.Obj.Id, b.Obj.Id);
        });
        var capped = limit > 0 && visible.Count > limit ? visible.GetRange(0, limit) : visible;
        if (capped.Count == 0) {
            return Array.Empty<TonightSkyObjectDto>();
        }

        // Precompute the night sample grid ONCE (it's object-independent): local sidereal time and
        // whether the sun is below the twilight threshold at each 5-min sample across ±12 h. Per object
        // we then only need its altitude at each sample (index stepsPerSide is exactly atUtc).
        var twilight = TwilightSunAltitudeDeg(site.TwilightDefinition);
        var stepsPerSide = WindowHalfSpanMinutes / WindowStepMinutes;
        var sampleCount = stepsPerSide * 2 + 1;
        var sampleUtc = new DateTimeOffset[sampleCount];
        var sampleLstDeg = new double[sampleCount];
        var sunIsDown = new bool[sampleCount];   // true = sun below the twilight threshold (dark enough)
        for (var i = 0; i < sampleCount; i++) {
            var t = atUtc.AddMinutes((i - stepsPerSide) * WindowStepMinutes);
            var lst = LocalSiderealTimeDeg(t, lon);
            var (sunRa, sunDec) = SunEquatorialDeg(t);
            var sunAlt = AltitudeFromHourAngleDeg(sunDec, lat, Mod360(lst - sunRa));
            sampleUtc[i] = t;
            sampleLstDeg[i] = lst;
            sunIsDown[i] = sunAlt < twilight;   // strictly below the twilight threshold = dark (photometric convention)
        }

        // Site terms are constant across every object and sample — compute the latitude trig and the
        // horizon threshold once. Comparing sin(alt) ≥ sin(horizon) is equivalent to alt ≥ horizon (asin
        // is monotonic over [−90,90]) and lets the per-sample loop skip the asin entirely.
        var sinLat = Math.Sin(Deg2Rad(lat));
        var cosLat = Math.Cos(Deg2Rad(lat));
        var sinHorizon = Math.Sin(Deg2Rad(horizon));

        var result = new List<TonightSkyObjectDto>(capped.Count);
        var up = new bool[sampleCount];
        foreach (var (alt, o) in capped) {
            // "Up" at a sample = object above the site horizon AND the sky dark enough (sun below twilight).
            // The object's dec trig is constant across all samples, so hoist it out; only cos(hour angle)
            // varies per sample. sin(alt) = sinδ·sinφ + cosδ·cosφ·cos H (same formula as AltitudeFromHourAngleDeg).
            var sinDec = Math.Sin(Deg2Rad(o.DecDeg));
            var cosDec = Math.Cos(Deg2Rad(o.DecDeg));
            for (var i = 0; i < sampleCount; i++) {
                var cosH = Math.Cos(Deg2Rad(sampleLstDeg[i] - o.RaDeg));
                var sinAlt = sinDec * sinLat + cosDec * cosLat * cosH;
                up[i] = sinAlt >= sinHorizon && sunIsDown[i];
            }

            // Window tonight = the LONGEST qualifying dark run in the span, always — not the run that
            // merely brackets atUtc. An object that sets and re-rises (e.g. up 10pm–midnight then
            // 2am–6am) must report its best 4 h window even when the query falls in the shorter early
            // run; "max integration hours tonight" is the headline figure. (Bracketing-from-atUtc bought
            // no speed — the loop above already filled up[] for every sample.)
            var (start, end) = LongestRun(up);

            DateTimeOffset? windowStart = null, windowEnd = null;
            double integrationHours = 0;
            if (start >= 0) {
                windowStart = sampleUtc[start];
                // Each "up" sample stands for its WindowStepMinutes slot, so the window's exclusive upper
                // bound is one step past the last up-sample. Without the +step the duration would count
                // only the gaps BETWEEN samples — short by one step — and a single-sample window would
                // report 0 h despite the object being up for at least that slot.
                windowEnd = sampleUtc[end].AddMinutes(WindowStepMinutes);
                integrationHours = (windowEnd.Value - windowStart.Value).TotalHours;
            }

            // Transit (upper culmination) nearest atUtc: hour angle H = LST − RA reaches 0. H grows at the
            // sidereal rate, so the nearest H=0 is H0 degrees in the past (if H0 ≤ 180) or 360−H0 ahead.
            var h0 = Mod360(lst0 - o.RaDeg);
            var signedDeg = h0 <= 180.0 ? -h0 : 360.0 - h0;
            var transitUtc = atUtc.AddHours(signedDeg / SiderealDegPerDay * 24.0);

            result.Add(new TonightSkyObjectDto(
                o.Id, o.Name, o.Type, o.Magnitude, o.RaDeg, o.DecDeg,
                Math.Round(alt, 1), Math.Round(MaxAltitudeDeg(o.DecDeg, lat), 1),
                o.SizeMajArcmin, o.SizeMinArcmin, o.PosAngleDeg, o.SurfaceBrightness,
                windowStart, windowEnd, transitUtc, Math.Round(integrationHours, 2)));
        }
        return result;
    }

    /// <summary>The longest contiguous run of <c>true</c> in <paramref name="flags"/> as inclusive
    /// (start, end) sample indices, or (−1, −1) when none is set.</summary>
    private static (int Start, int End) LongestRun(bool[] flags) {
        int bestStart = -1, bestEnd = -1, curStart = -1;
        for (var i = 0; i < flags.Length; i++) {
            if (flags[i] && curStart < 0) {
                curStart = i;   // a run begins
            }
            // Evaluate a run only when it ENDS (a false sample, or the final index) — one comparison per
            // run, not per element. Strict `>` keeps the EARLIEST of equal-length runs by design, so a
            // user can start sooner.
            var runEnds = curStart >= 0 && (!flags[i] || i == flags.Length - 1);
            if (runEnds) {
                var curEnd = flags[i] ? i : i - 1;
                if (bestStart < 0 || curEnd - curStart > bestEnd - bestStart) {
                    bestStart = curStart;
                    bestEnd = curEnd;
                }
                curStart = -1;
            }
        }
        return (bestStart, bestEnd);
    }

    /// <summary>Sun altitude below which the sky is "dark" for the given twilight definition:
    /// civil −6°, nautical −12°, astronomical −18°. Unrecognised → astronomical (the darkest, safest
    /// default for imaging).</summary>
    private static double TwilightSunAltitudeDeg(string? twilightDefinition) =>
        (twilightDefinition?.Trim().ToLowerInvariant()) switch {
            "civil" => -6.0,
            "nautical" => -12.0,
            "astronomical" => -18.0,
            _ => -18.0,
        };

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

    private static double Mod360(double x) {
        x %= 360.0;
        return x < 0 ? x + 360.0 : x;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
