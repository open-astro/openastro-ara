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
/// §18.I — optional body for the solve-a-frame endpoint. Supplying both fields seeds a fast
/// near-solve around that approximate sky position instead of a slow blind solve. Omit the body
/// (or either field) and the solver falls back to the frame's own <c>OBJCTRA</c>/<c>OBJCTDEC</c>
/// FITS headers, then to a blind solve when the frame carries no pointing at all. The pair is
/// all-or-nothing: a lone RA or Dec is treated as no hint.
/// </summary>
public record PlateSolveRequestDto(
    double? ApproxRaHours = null,     // approximate right ascension hint, hours (J2000)
    double? ApproxDecDegrees = null); // approximate declination hint, degrees (J2000)

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
