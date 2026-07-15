#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;


/// <summary>NEXTGEN §1 — the recommended filter approach for a target: <see cref="Narrowband"/> mono
/// line filters (Hα/OIII/SII), <see cref="Duoband"/> OSC + dual/tri-band, <see cref="Broadband"/>
/// L/RGB/OSC. Serialized all-lowercase per §60.6 (<c>narrowband</c>/<c>duoband</c>/<c>broadband</c>).</summary>
public enum FilterApproach {
    Narrowband,
    Duoband,
    Broadband,
}

/// <summary>§36.8 framing fit — how an object's apparent size sits against the active optical train's
/// field of view, the equipment-aware heart of the Tonight's Sky score. <see cref="TooSmall"/> = lost in
/// the frame (a small galaxy at a short focal length), <see cref="Good"/> = fills a healthy fraction,
/// <see cref="TooBig"/> = overflows (Orion at a long focal length; a mosaic could rescue it),
/// <see cref="Unknown"/> = the catalog recorded no size. Serialized all-lowercase per the §60.6 enum
/// convention (<c>unknown</c>/<c>toosmall</c>/<c>good</c>/<c>toobig</c>).</summary>
public enum FramingFit {
    Unknown,
    TooSmall,
    Good,
    TooBig,
}

/// <summary>
/// §36 Planning horizon — the observer's local horizon projected onto the equatorial
/// (RA/Dec, J2000) sky for the active profile's site at a given instant, so an equatorial
/// sky-chart view can overlay it. In equatorial coordinates the
/// horizon is a curve in RA/Dec that depends on the site latitude and the local sidereal
/// time. <see cref="Points"/> is that curve — the site's horizon altitude
/// (<see cref="HorizonAltitudeDeg"/>, the profile's DefaultHorizonAltitudeDeg) swept
/// through azimuth 0→360° and closed back on itself; a target below it is "down".
/// <see cref="Zenith"/> is the point straight up (dec = latitude, RA = local sidereal time) — its
/// <see cref="HorizonPointDto.AzimuthDeg"/> is meaningless (the zenith has no azimuth) and is fixed
/// at 0. <see cref="Cardinals"/> marks N/E/S/W on the horizon for orientation.
/// <see cref="CustomHorizonIgnored"/> is true when the profile asks for a custom terrain horizon
/// (<c>UseCustomHorizon</c>) that this flat-horizon slice does not yet honour — so a later slice can
/// surface "terrain horizon not shown" without a breaking schema change.
/// </summary>
public sealed record HorizonDto(
    DateTimeOffset AtUtc,
    double HorizonAltitudeDeg,
    double LocalSiderealTimeDeg,
    HorizonPointDto Zenith,
    IReadOnlyList<HorizonPointDto> Points,
    IReadOnlyList<CardinalPointDto> Cardinals,
    bool CustomHorizonIgnored);

/// <summary>One point on the horizon overlay: its equatorial (J2000) coordinates and the
/// azimuth (degrees, north = 0, increasing eastward) it was projected from.</summary>
public sealed record HorizonPointDto(double RaDeg, double DecDeg, double AzimuthDeg);

/// <summary>A labelled compass point (N/E/S/W) sitting on the horizon, for orienting the overlay.</summary>
public sealed record CardinalPointDto(string Label, double RaDeg, double DecDeg);
