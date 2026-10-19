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

/// <summary>#1267 — <c>GET /api/v1/targets/resolve?name=</c>: one name (any identifier: "AB Cas",
/// "HIP 12235", "V* AB Cas", "M 31", "Wolf 359") to one J2000 position.
/// <para><see cref="Status"/> is <c>resolved</c>, <c>not_found</c> (the offline index and Simbad both
/// drew a blank) or <c>offline</c> (not in the offline index, and Simbad could not be reached: the
/// client says so instead of spinning). <see cref="Source"/> says where a resolution came from:
/// <c>index</c> (the bundled HYG / OpenNGC cross-ids, instant, no internet), <c>cache</c> (a Simbad
/// answer kept in the daemon's database, so a target resolved at home is still there at the dark
/// site) or <c>simbad</c> (asked just now). Positions are J2000 degrees.</para></summary>
public sealed record TargetResolveResponseDto(
    string Query,
    string Status,
    string Source,
    string? MainId,
    IReadOnlyList<string> Aliases,
    double? RaDeg,
    double? DecDeg,
    string? ObjectType,
    double? Magnitude,
    string? Message);
