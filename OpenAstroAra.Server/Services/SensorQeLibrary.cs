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
/// NEXTGEN §5 <b>Tier 1</b> — the small sensor→peak-QE library
/// (design/NEXTGEN_PLANNING.md "Data strategy"): QE is a property of the
/// <i>sensor</i> (shared and stable across every camera built on it), is never
/// exposed over ASCOM, and is the one exposure-planning input a fresh profile
/// can get for free — the connect-time electronics auto-capture fills it from
/// this table, keyed off the driver's <c>Camera.SensorName</c>, ONLY when the
/// profile's QE is unset (a user-entered value is never overwritten, and the
/// fill is refinable in Settings → Imaging afterwards).
/// <para>Values are typical PEAK quantum efficiencies from published sensor /
/// manufacturer data, stored as fractions and deliberately rounded to 0.05:
/// vendor figures for the same sensor vary by ±5–10%, and the Glover model is
/// forgiving (QE enters the sky-flux estimate linearly, and the read-noise
/// contribution curve is flat near the optimum), so coarse honest values beat
/// false precision. Electronics-owned specs (full well, e⁻/ADU, read noise)
/// deliberately do NOT live here — they vary per camera on the same sensor
/// (Tier 2 auto-capture / Tier 3 manual, per the design's split).</para>
/// </summary>
internal static class SensorQeLibrary {

    // Matched as substrings of the driver-reported sensor name (drivers report
    // freely: "IMX571", "Sony IMX571", "SONY IMX571 (mono)"). LONGEST key first
    // so a longer designation can never be shadowed by a shorter prefix of
    // itself (e.g. a future "IMX571A" row would win over "IMX571").
    private static readonly (string Key, double QePeak)[] Entries = [
        // Kodak/onsemi CCDs (legacy, distinctly lower QE than modern BSI CMOS).
        ("KAF-16803", 0.60),
        ("KAF-8300", 0.55),
        // Sony EXview HAD CCD.
        ("ICX694", 0.75),
        // Sony BSI CMOS — the modern astro-camera mainstream.
        ("IMX571", 0.80),   // APS-C 26 MP (ASI2600 / QHY268 / ToupTek 2600…)
        ("IMX455", 0.80),   // full-frame 61 MP (ASI6200 / QHY600…)
        ("IMX461", 0.80),   // medium format 102 MP
        ("IMX411", 0.80),   // medium format 151 MP
        ("IMX533", 0.80),   // 1" square 9 MP (ASI533…)
        ("IMX410", 0.80),   // full-frame 24 MP (ASI2400…)
        ("IMX294", 0.75),   // 4/3" 11.7 MP (ASI294…)
        ("IMX183", 0.85),   // 1" 20 MP (ASI183…)
        ("IMX178", 0.80),   // 1/1.8" 6.4 MP
        ("IMX174", 0.75),   // 1/1.2" global shutter
        ("IMX290", 0.80),   // 1/2.8" (guiding/planetary)
        ("IMX462", 0.90),   // 1/2.8" (high NIR sensitivity)
        ("IMX585", 0.90),   // 1/1.2" 8.3 MP (STARVIS 2)
    ];

    /// <summary>Peak QE (fraction, 0–1) for the sensor the driver reported, or
    /// null when the name is empty or matches no library row (the caller keeps
    /// its current value / the calculator's documented generic default).</summary>
    internal static double? LookupPeakQe(string? sensorName) {
        if (string.IsNullOrWhiteSpace(sensorName)) {
            return null;
        }
        var normalized = sensorName.ToUpperInvariant();
        foreach (var (key, qePeak) in Entries) {
            if (normalized.Contains(key, StringComparison.Ordinal)) {
                return qePeak;
            }
        }
        return null;
    }
}
