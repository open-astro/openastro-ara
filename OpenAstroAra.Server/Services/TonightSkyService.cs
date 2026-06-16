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

namespace OpenAstroAra.Server.Services;

/// <summary>§36/§25.5 Tonight's Sky — ranks a curated deep-sky list by how high each object
/// sits above the active profile's site horizon at a given instant.</summary>
public interface ITonightSkyService {
    /// <summary>The curated objects above the site horizon at <paramref name="atUtc"/>, highest
    /// first, capped at <paramref name="limit"/> (≤ 0 = all). Reads the site lat/long + horizon
    /// from the active profile.</summary>
    IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit);
}

public sealed class TonightSkyService : ITonightSkyService {
    private readonly IProfileStore _profileStore;

    public TonightSkyService(IProfileStore profileStore) {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
    }

    public IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit) =>
        Rank(_profileStore.GetSiteSettings(), atUtc, limit);

    /// <summary>One curated deep-sky object: catalog id + common name, type, magnitude, J2000 RA/Dec (deg).</summary>
    internal readonly record struct CatalogObject(
        string Id, string Name, string Type, double Magnitude, double RaDeg, double DecDeg);

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

    /// <summary>Pure ranking: the catalog objects above the site horizon at <paramref name="atUtc"/>,
    /// altitude-descending, capped at <paramref name="limit"/>.</summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(SiteSettingsDto site, DateTimeOffset atUtc, int limit) {
        var horizon = site.DefaultHorizonAltitudeDeg;
        var lst = LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
        var visible = new List<TonightSkyObjectDto>(Catalog.Count);
        foreach (var o in Catalog) {
            var alt = AltitudeFromHourAngleDeg(o.DecDeg, site.LatitudeDeg, Mod360(lst - o.RaDeg));
            if (alt < horizon) {
                continue;
            }
            var maxAlt = MaxAltitudeDeg(o.DecDeg, site.LatitudeDeg);
            visible.Add(new TonightSkyObjectDto(
                o.Id, o.Name, o.Type, o.Magnitude, o.RaDeg, o.DecDeg,
                Math.Round(alt, 1), Math.Round(maxAlt, 1)));
        }
        // Highest first; the catalog id is a stable tie-break so equal-altitude rows don't reorder.
        visible.Sort((a, b) => {
            var c = b.AltitudeDeg.CompareTo(a.AltitudeDeg);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return limit > 0 && visible.Count > limit ? visible.GetRange(0, limit) : visible;
    }

    /// <summary>Local apparent sidereal time in degrees (Meeus low-precision GMST + east-positive
    /// longitude). Arcminute-level accuracy — far finer than altitude ranking needs.</summary>
    internal static double LocalSiderealTimeDeg(DateTimeOffset atUtc, double longitudeDeg) {
        // Julian Date of the instant (Unix epoch JD = 2440587.5).
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var d = jd - 2451545.0; // days from J2000.0
        var gmst = 280.46061837 + 360.98564736629 * d;
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
