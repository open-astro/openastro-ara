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

/// <summary>§36 Planning horizon — projects the active profile's local horizon onto the equatorial
/// (RA/Dec) sky at a given instant so an equatorial sky-chart view can overlay it. In equatorial
/// coordinates the horizon is a curve in RA/Dec that moves with the site latitude
/// and the local sidereal time; this service computes that curve plus the zenith and the cardinal
/// points. All the trig lives in <see cref="SiteAstrometry"/> (the site-astrometry math home);
/// this is a thin orchestrator over the active profile's site settings.</summary>
public interface IHorizonService {
    /// <summary>The local horizon overlay for the active profile's site at <paramref name="atUtc"/>.</summary>
    HorizonDto GetHorizon(DateTimeOffset atUtc);
}

public sealed class HorizonService : IHorizonService {
    // Azimuth sampling step (degrees) for the horizon curve. 2° → 181 vertices closing the loop —
    // smooth at any practical field of view and cheap to compute/serialise.
    private const int AzimuthStepDeg = 2;

    private readonly IProfileStore _profileStore;

    public HorizonService(IProfileStore profileStore) {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
    }

    public HorizonDto GetHorizon(DateTimeOffset atUtc) =>
        Compute(_profileStore.GetSiteSettings(), atUtc, _profileStore.GetCustomHorizon());

    /// <summary>Pure projection: the horizon curve + zenith + cardinals for <paramref name="site"/>
    /// at <paramref name="atUtc"/>. With <c>UseCustomHorizon</c> on and a non-empty
    /// <paramref name="customHorizon"/> skyline, each azimuth sample sits at the interpolated
    /// terrain altitude; otherwise the flat <see cref="SiteSettingsDto.DefaultHorizonAltitudeDeg"/>
    /// all the way round. <c>HorizonAltitudeDeg</c> always reports the flat default (the custom
    /// shape lives in the per-point altitudes).</summary>
    internal static HorizonDto Compute(SiteSettingsDto site, DateTimeOffset atUtc,
            CustomHorizonDto? customHorizon = null) {
        var lat = site.LatitudeDeg;
        var horizonAlt = site.DefaultHorizonAltitudeDeg;
        var lst = SiteAstrometry.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);
        var skyline = site.UseCustomHorizon && customHorizon is { Points.Count: > 0 }
            ? customHorizon.Points
            : null;

        double AltitudeAt(double azimuthDeg) => skyline is null
            ? horizonAlt
            : CustomHorizonValidator.AltitudeAtAzimuth(skyline, azimuthDeg);

        var points = new List<HorizonPointDto>();
        for (var az = 0; az <= 360; az += AzimuthStepDeg) {
            // Wrap the final vertex exactly onto the first so the polyline closes without a seam gap.
            var azimuth = az == 360 ? 0.0 : az;
            var (ra, dec) = SiteAstrometry.EquatorialFromAltAz(AltitudeAt(azimuth), azimuth, lat, lst);
            points.Add(new HorizonPointDto(ra, dec, azimuth));
        }

        var (zenithRa, zenithDec) = SiteAstrometry.EquatorialFromAltAz(90.0, 0.0, lat, lst);
        var cardinals = new List<CardinalPointDto> {
            Cardinal("N", 0.0, AltitudeAt(0.0), lat, lst),
            Cardinal("E", 90.0, AltitudeAt(90.0), lat, lst),
            Cardinal("S", 180.0, AltitudeAt(180.0), lat, lst),
            Cardinal("W", 270.0, AltitudeAt(270.0), lat, lst),
        };

        return new HorizonDto(
            AtUtc: atUtc,
            HorizonAltitudeDeg: horizonAlt,
            LocalSiderealTimeDeg: lst,
            Zenith: new HorizonPointDto(zenithRa, zenithDec, 0.0),
            Points: points,
            Cardinals: cardinals,
            // False when the requested custom skyline is actually being served; true
            // only for the requested-but-empty case (UseCustomHorizon on, no points).
            CustomHorizonIgnored: site.UseCustomHorizon && skyline is null);
    }

    private static CardinalPointDto Cardinal(string label, double azDeg, double altDeg, double latDeg, double lstDeg) {
        var (ra, dec) = SiteAstrometry.EquatorialFromAltAz(altDeg, azDeg, latDeg, lstDeg);
        return new CardinalPointDto(label, ra, dec);
    }
}
