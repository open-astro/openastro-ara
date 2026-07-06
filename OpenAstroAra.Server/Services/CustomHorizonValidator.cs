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
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>§36 custom terrain horizon — validation + canonical form for the PUT
/// endpoint, and the azimuth→altitude interpolation every consumer shares
/// (HorizonService's overlay, TonightSkyService's visibility windows).</summary>
public static class CustomHorizonValidator {

    /// <summary>One vertex per azimuth degree closing the loop — anything more is
    /// noise the UI can't even edit.</summary>
    public const int MaxPoints = 361;

    /// <summary>Altitudes below this are non-physical for a skyline (a dip below the
    /// astronomical horizon from elevation is real but small).</summary>
    public const double MinAltitudeDeg = -10;
    public const double MaxAltitudeDeg = 90;

    /// <summary>Canonicalize: sort by azimuth, collapse duplicate azimuths
    /// (keep-last, the user's most recent entry wins), map azimuth 360 onto 0.
    /// Returns the normalized dto, or an error string for out-of-range values.
    /// An EMPTY list is valid — it means "no skyline entered" (consumers fall
    /// back to the flat default altitude).</summary>
    public static (CustomHorizonDto? Normalized, string? Error) Normalize(CustomHorizonDto? body) {
        var points = body?.Points ?? [];
        if (points.Count > MaxPoints) {
            return (null, $"At most {MaxPoints} horizon points are supported (got {points.Count}).");
        }
        foreach (var p in points) {
            if (double.IsNaN(p.AzimuthDeg) || p.AzimuthDeg is < 0 or > 360) {
                return (null, $"Azimuth must be 0-360 degrees (got {p.AzimuthDeg.ToString(CultureInfo.InvariantCulture)}).");
            }
            if (double.IsNaN(p.AltitudeDeg) || p.AltitudeDeg is < MinAltitudeDeg or > MaxAltitudeDeg) {
                return (null, $"Altitude must be {MinAltitudeDeg}..{MaxAltitudeDeg} degrees (got {p.AltitudeDeg.ToString(CultureInfo.InvariantCulture)}).");
            }
        }
        var canonical = points
            .Select(p => p.AzimuthDeg >= 360 ? p with { AzimuthDeg = p.AzimuthDeg - 360 } : p)
            .GroupBy(p => p.AzimuthDeg)
            .Select(g => g.Last())
            .OrderBy(p => p.AzimuthDeg)
            .ToList();
        return (new CustomHorizonDto(canonical), null);
    }

    /// <summary>The skyline altitude at <paramref name="azimuthDeg"/>: linear
    /// interpolation between the two neighbouring vertices, wrapping 360→0 (the
    /// segment between the LAST and FIRST vertex crosses north). A single vertex
    /// is a flat horizon at its altitude. <paramref name="points"/> must be the
    /// canonical (sorted, de-duplicated) form <see cref="Normalize"/> produces.</summary>
    public static double AltitudeAtAzimuth(IReadOnlyList<CustomHorizonPointDto> points, double azimuthDeg) {
        if (points.Count == 0) {
            throw new ArgumentException("interpolation needs at least one point", nameof(points));
        }
        if (points.Count == 1) {
            return points[0].AltitudeDeg;
        }
        var az = azimuthDeg % 360;
        if (az < 0) az += 360;

        // Find the first vertex with azimuth > az; its predecessor starts the segment.
        var upper = 0;
        while (upper < points.Count && points[upper].AzimuthDeg <= az) {
            upper++;
        }
        CustomHorizonPointDto a, b;
        double span, offset;
        if (upper == 0 || upper == points.Count) {
            // az sits on the wraparound segment: last vertex → (first vertex + 360°).
            a = points[^1];
            b = points[0];
            span = 360 - a.AzimuthDeg + b.AzimuthDeg;
            offset = upper == 0 ? az + 360 - a.AzimuthDeg : az - a.AzimuthDeg;
        } else {
            a = points[upper - 1];
            b = points[upper];
            span = b.AzimuthDeg - a.AzimuthDeg;
            offset = az - a.AzimuthDeg;
        }
        if (span <= 0) {
            return a.AltitudeDeg; // duplicate-azimuth degenerate — canonical form prevents it
        }
        var t = offset / span;
        return a.AltitudeDeg + (b.AltitudeDeg - a.AltitudeDeg) * t;
    }
}
