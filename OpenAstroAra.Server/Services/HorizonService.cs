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
/// (RA/Dec) sky at a given instant so the Aladin atlas can overlay it. Aladin is an equatorial atlas,
/// not an alt/az planetarium, so the horizon is a curve in RA/Dec that moves with the site latitude
/// and the local sidereal time; this service computes that curve plus the zenith and the cardinal
/// points. All the trig lives in <see cref="TonightSkyService"/> (the site-astrometry math home);
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
        Compute(_profileStore.GetSiteSettings(), atUtc);

    /// <summary>Pure projection: the horizon curve + zenith + cardinals for <paramref name="site"/>
    /// at <paramref name="atUtc"/>. The horizon altitude is the profile's
    /// <see cref="SiteSettingsDto.DefaultHorizonAltitudeDeg"/> (flat all the way round for now).</summary>
    internal static HorizonDto Compute(SiteSettingsDto site, DateTimeOffset atUtc) {
        var lat = site.LatitudeDeg;
        var horizonAlt = site.DefaultHorizonAltitudeDeg;
        var lst = TonightSkyService.LocalSiderealTimeDeg(atUtc, site.LongitudeDeg);

        var points = new List<HorizonPointDto>();
        for (var az = 0; az <= 360; az += AzimuthStepDeg) {
            // Wrap the final vertex exactly onto the first so the polyline closes without a seam gap.
            var azimuth = az == 360 ? 0.0 : az;
            var (ra, dec) = TonightSkyService.EquatorialFromAltAz(horizonAlt, azimuth, lat, lst);
            points.Add(new HorizonPointDto(ra, dec, azimuth));
        }

        var (zenithRa, zenithDec) = TonightSkyService.EquatorialFromAltAz(90.0, 0.0, lat, lst);
        var cardinals = new List<CardinalPointDto> {
            Cardinal("N", 0.0, horizonAlt, lat, lst),
            Cardinal("E", 90.0, horizonAlt, lat, lst),
            Cardinal("S", 180.0, horizonAlt, lat, lst),
            Cardinal("W", 270.0, horizonAlt, lat, lst),
        };

        return new HorizonDto(
            AtUtc: atUtc,
            HorizonAltitudeDeg: horizonAlt,
            LocalSiderealTimeDeg: lst,
            Zenith: new HorizonPointDto(zenithRa, zenithDec, 0.0),
            Points: points,
            Cardinals: cardinals);
    }

    private static CardinalPointDto Cardinal(string label, double azDeg, double altDeg, double latDeg, double lstDeg) {
        var (ra, dec) = TonightSkyService.EquatorialFromAltAz(altDeg, azDeg, latDeg, lstDeg);
        return new CardinalPointDto(label, ra, dec);
    }
}
