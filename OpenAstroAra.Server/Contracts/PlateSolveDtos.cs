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
/// §18.I — the astrometric solution for a frame. <see cref="Success"/> false means the solver ran but did not
/// find a solution (e.g. too few stars, wrong field); every other field is then null (no solution to report).
/// </summary>
public record PlateSolveResultDto(
    bool Success,
    double? Ra,             // right ascension at the frame centre, hours
    double? Dec,            // declination at the frame centre, degrees
    double? Orientation,    // image rotation, degrees east-of-north
    double? PixelScale,     // arcsec / pixel
    double? SearchRadius);  // solved search radius, degrees
