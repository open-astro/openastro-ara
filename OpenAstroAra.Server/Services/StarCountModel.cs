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
/// NEXTGEN §3.1 slice 2 — the galactic star-count model: cumulative stars per square
/// degree brighter than a limiting magnitude, as a function of galactic latitude.
/// Pairs with <see cref="StarDetectability.LimitingMagnitude"/> to answer "how many
/// registerable stars does a sub of length t actually contain in this FOV?".
///
/// <para><b>Provenance + validation (the §3.1 accuracy gate).</b> Every constant below
/// is derived from the canonical HYG snapshot (<c>hygdata_v40.csv.gz</c>, the same
/// sha256 <c>8e3ff9e6…</c> that <c>DataManagerService</c> pins for <c>hyg-stars</c>)
/// by <c>scripts/fit-star-count-model.py</c>: star counts pooled over all galactic
/// longitudes in ±5° bands around the 7 fixed latitudes, per-band log-linear fits of
/// <c>log₁₀ N(&lt;m)</c> on m ∈ [5,8], and the pinned VALIDATION GATE — each fit
/// extrapolated out-of-sample to m = 9 must land within a factor of 2 of the actual
/// pooled density at EVERY band (measured ratios 1.17–1.67, all inside the gate;
/// <c>StarCountModelTest</c> re-derives the fit from the embedded densities and
/// re-asserts the gate so the proof stands in CI, not just in a script run).</para>
///
/// <para><b>Model.</b> The shipped predictor anchors at the ACTUAL m = 9 densities and
/// extrapolates with the validated per-band slope —
/// <c>N(&lt;m, b) = N₉(b) · 10^(slope(b)·(m−9))</c> — interpolated linearly in
/// <c>sin|b|</c> between band centres. Anchoring at the real m = 9 data is strictly
/// more accurate than the raw fit; the gate exists to prove the log-linear FORM before
/// trusting its slope beyond the data.</para>
///
/// <para><b>Known bias, deliberately one-sided and documented:</b> real cumulative
/// counts flatten at faint magnitudes (the gate itself measured the log-linear form
/// overestimating m = 9 by 1.2–1.7×), so extrapolation beyond m = 9 tends to
/// OVERestimate available stars — i.e. the star-detectability floor it feeds is
/// optimistic, an advisory that under-warns rather than over-warns. Per §3.1 the
/// beyond-mag-9 extrapolation is labelled as such in user-facing reason strings.</para>
/// </summary>
public static class StarCountModel {

    // Band centres as sin|b| (0°,10°,20°,30°,50°,70°,90°) — the interpolation axis,
    // computed at full precision so a query at an exact band centre lands exactly on
    // its knot (a rounded literal here left the m=9 anchor 4e-7 off).
    private static readonly double[] BandSinB = [
        0.0,
        Math.Sin(10.0 * Math.PI / 180.0),
        Math.Sin(20.0 * Math.PI / 180.0),
        Math.Sin(30.0 * Math.PI / 180.0),
        Math.Sin(50.0 * Math.PI / 180.0),
        Math.Sin(70.0 * Math.PI / 180.0),
        1.0,
    ];

    // Actual pooled HYG densities N(<9)/deg² per band (the anchor).
    private static readonly double[] DensityAtMag9 =
        [2.941788, 2.517927, 2.121440, 1.872188, 1.649431, 1.546706, 1.458785];

    // Validated per-band log-linear slopes d(log₁₀N)/dm from the m ∈ [5,8] fits.
    private static readonly double[] SlopePerMag =
        [0.468471, 0.466291, 0.458120, 0.502271, 0.473209, 0.457310, 0.439958];

    /// <summary>Cumulative stars per square degree brighter than
    /// <paramref name="limitingMag"/> at galactic latitude <paramref name="galacticLatitudeDeg"/>
    /// (sign ignored — counts are symmetric about the plane by construction).</summary>
    public static double CumulativeStarsPerDeg2(double limitingMag, double galacticLatitudeDeg) {
        if (!double.IsFinite(limitingMag)) {
            throw new ArgumentOutOfRangeException(nameof(limitingMag), "limiting magnitude must be finite");
        }
        if (!double.IsFinite(galacticLatitudeDeg) || Math.Abs(galacticLatitudeDeg) > 90.0) {
            // Range guard (r1): a latitude beyond ±90° is caller garbage — folding it
            // through sin|b| would silently alias onto a valid band instead of surfacing.
            throw new ArgumentOutOfRangeException(nameof(galacticLatitudeDeg),
                "galactic latitude must be finite and within ±90°");
        }
        var sinB = Math.Sin(Math.Abs(galacticLatitudeDeg) * Math.PI / 180.0);
        var n9 = InterpolateInSinB(DensityAtMag9, sinB);
        var slope = InterpolateInSinB(SlopePerMag, sinB);
        return n9 * Math.Pow(10.0, slope * (limitingMag - 9.0));
    }

    /// <summary>Galactic latitude (degrees, signed) of an equatorial J2000 position —
    /// the standard NGP rotation (α_G = 192.85948°, δ_G = 27.12825°). Slice 3 feeds a
    /// target's RA/Dec through this to pick the count-model latitude.</summary>
    public static double GalacticLatitudeDeg(double raDeg, double decDeg) {
        if (!double.IsFinite(raDeg)) {
            throw new ArgumentOutOfRangeException(nameof(raDeg), "RA must be finite");
        }
        if (!double.IsFinite(decDeg) || Math.Abs(decDeg) > 90.0) {
            throw new ArgumentOutOfRangeException(nameof(decDeg),
                "declination must be finite and within ±90°");
        }
        const double ngpRa = 192.85948 * Math.PI / 180.0;
        const double ngpDec = 27.12825 * Math.PI / 180.0;
        var ra = raDeg * Math.PI / 180.0;
        var dec = decDeg * Math.PI / 180.0;
        var sinB = Math.Sin(ngpDec) * Math.Sin(dec)
                 + Math.Cos(ngpDec) * Math.Cos(dec) * Math.Cos(ra - ngpRa);
        return Math.Asin(Math.Clamp(sinB, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    // Piecewise-linear interpolation over the BandSinB knots; clamped at the ends
    // (sin|b| spans exactly [0,1], so clamping only absorbs float dust).
    private static double InterpolateInSinB(double[] values, double sinB) {
        if (sinB <= BandSinB[0]) {
            return values[0];
        }
        for (var i = 1; i < BandSinB.Length; i++) {
            if (sinB <= BandSinB[i]) {
                var f = (sinB - BandSinB[i - 1]) / (BandSinB[i] - BandSinB[i - 1]);
                return values[i - 1] + f * (values[i] - values[i - 1]);
            }
        }
        return values[^1];
    }
}
