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

/// <summary>
/// §36/§25.5 Tonight's Sky — one deep-sky object ranked for the active profile's site at a
/// given instant. <see cref="AltitudeDeg"/> is the object's altitude above the horizon right
/// then (the ranking key); <see cref="MaxAltitudeDeg"/> is its highest possible altitude from
/// this latitude (transit), a "how well it suits your sky" hint. <see cref="RaDeg"/>/
/// <see cref="DecDeg"/> (J2000) let the client recentre the atlas on it.
/// <para>§36.8 planner fields (appended, all optional so the existing client keeps working):
/// <see cref="SizeMajArcmin"/>/<see cref="SizeMinArcmin"/> are the apparent axes (arcmin),
/// <see cref="PosAngleDeg"/> the position angle, and <see cref="SurfaceBrightness"/> mag/arcsec²
/// (null when the catalog didn't record them). <see cref="WindowStartUtc"/>/<see cref="WindowEndUtc"/>
/// bracket the object's visibility window tonight — above the site horizon AND the sky dark enough
/// (sun below the profile's twilight threshold); both null when there's no qualifying window tonight.
/// <see cref="TransitUtc"/> is its upper culmination nearest the query instant, and
/// <see cref="IntegrationHours"/> is the window length in hours (0 when there's no window).</para>
/// <para><b>Window semantics:</b> the window is tonight's <i>longest</i> dark stretch for the object,
/// a whole-night planning figure — NOT "time remaining from now". So for an object that has already
/// passed its best window but is still up, <see cref="WindowStartUtc"/> (and even
/// <see cref="WindowEndUtc"/>) can be earlier than the query instant, and <see cref="IntegrationHours"/>
/// counts the full stretch, not what's left. <see cref="RemainingHours"/> is the "from now" figure.</para>
/// <para>§36.8 slice-2 equipment-aware fields (appended): <see cref="Framing"/> classifies the object's
/// apparent size against the active optical train's field of view; <see cref="Score"/> is a transparent
/// 0–100 "worth shooting tonight" rank and <see cref="ScoreReasons"/> the short component tags that
/// explain it; <see cref="RemainingHours"/> is the dark time still ahead of the query instant in the
/// object's window tonight (a not-yet-started window contributes its full length, a past window 0).</para>
/// <para>NEXTGEN §1 filter-advice fields (appended, all optional — advice only, never a gate):
/// <see cref="FilterAdvice"/> is the recommended filter approach for this target given the user's
/// declared planning filter set × the target's emission character × the site's Bortle, with
/// <see cref="AdviceReason"/> the one-line human explanation; both null when no filter set is declared
/// or the target's emission character is unknown (never guess). <see cref="OptimalSubS"/> is the
/// Glover read-noise-floor sub length (seconds) for the advised approach's representative filter —
/// null until the camera electronics + aperture are configured (the advice string still flows).</para>
/// </summary>
public sealed record TonightSkyObjectDto(
    string Id,
    string Name,
    string Type,
    double Magnitude,
    double RaDeg,
    double DecDeg,
    double AltitudeDeg,
    double MaxAltitudeDeg,
    double? SizeMajArcmin = null,
    double? SizeMinArcmin = null,
    double? PosAngleDeg = null,
    double? SurfaceBrightness = null,
    DateTimeOffset? WindowStartUtc = null,
    DateTimeOffset? WindowEndUtc = null,
    DateTimeOffset? TransitUtc = null,
    double IntegrationHours = 0,
    FramingFit Framing = FramingFit.Unknown,
    double Score = 0,
    IReadOnlyList<string>? ScoreReasons = null,
    double RemainingHours = 0,
    FilterApproach? FilterAdvice = null,
    string? AdviceReason = null,
    double? OptimalSubS = null);

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
