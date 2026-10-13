#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1140 P2 — the frame-level sky background the Integration Budget's calibration store fits
/// against. Two figures per frame from one pass over a 65536-entry count array: the 5th
/// percentile (<c>P05</c>) and the median. The percentile is the one the fit must use: a
/// frame-filling emission nebula lifts the median well above the sky (the design dry run
/// measured an Hα median rate 2.7× its p05 rate), while the darkest 5 % of pixels is sky
/// plus pedestal on any real field. The median is kept alongside as the sanity companion.
/// Pure and allocation-light (a 256 KB int count array; int ranks suffice up to 2.1 G pixels),
/// so it runs off the capture path for every frame, stars or not.
/// </summary>
public static class FrameBackground {

    /// <summary>The percentile the background is read at (0–1).</summary>
    public const double BackgroundPercentile = 0.05;

    public static (double P05, double Median) Measure(ReadOnlySpan<ushort> pixels) {
        if (pixels.Length == 0) {
            return (double.NaN, double.NaN);
        }
        var counts = new int[65536];
        foreach (var p in pixels) {
            counts[p]++;
        }
        var n = (long)pixels.Length;
        // Rank statistics on the count array: the value at which the running count first reaches
        // the target rank — ceil(p·n) for the percentile; (n+1)/2 for the median, which is the
        // middle element for odd n and the LOWER of the two middle elements for even n.
        var p05Rank = Math.Max(1, (long)Math.Ceiling(BackgroundPercentile * n));
        return (ValueAtRank(counts, p05Rank), ValueAtRank(counts, (n + 1) / 2));
    }

    private static double ValueAtRank(int[] counts, long rank) {
        long seen = 0;
        for (var v = 0; v < counts.Length; v++) {
            seen += counts[v];
            if (seen >= rank) {
                return v;
            }
        }
        return counts.Length - 1;
    }
}
