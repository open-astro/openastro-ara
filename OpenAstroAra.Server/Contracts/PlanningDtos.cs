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
/// §36/§25.5 Tonight's Sky — one curated deep-sky object ranked for the active
/// profile's site at a given instant. <see cref="AltitudeDeg"/> is the object's
/// altitude above the horizon right then (the ranking key); <see cref="MaxAltitudeDeg"/>
/// is its highest possible altitude from this latitude (transit), a "how well it
/// suits your sky" hint. <see cref="RaDeg"/>/<see cref="DecDeg"/> (J2000) let the
/// client recentre the atlas on it.
/// </summary>
public sealed record TonightSkyObjectDto(
    string Id,
    string Name,
    string Type,
    double Magnitude,
    double RaDeg,
    double DecDeg,
    double AltitudeDeg,
    double MaxAltitudeDeg);
