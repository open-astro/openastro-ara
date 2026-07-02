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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// NEXTGEN §3.1 slice 1 — the limiting-magnitude solver: the faintest point source
/// (V magnitude) that reaches a detection SNR in a single sub of length <c>t</c>,
/// from the same rig+sky inputs <see cref="OptimalSubCalculator"/> already uses.
/// This is the design's "uncontroversial half": pure aperture-photometry math,
/// needed under any outcome of the §3.1 star-count-model decision, and deliberately
/// UNWIRED until that decision lands (slice 3 wires it into the sub-exposure window
/// + Tonight's Sky reasons).
///
/// <para>Model (§3.1, same conventions and zero point as the Glover sky-flux math —
/// atmospheric/optical transmission losses are folded into the shared documented
/// zero-point assumption, consistent with <see cref="OptimalSubCalculator.SkyFluxEPerSecPerPx"/>):
/// <code>
/// S(m)  = F₀ · 10^(−0.4·m) · BandwidthNm · apertureAreaCm² · QE · t     [e⁻, the star's signal]
/// N     = n_pix · (P·t + R²)                                            [e⁻², sky + read noise over the seeing disc]
/// SNR   = S / √(S + N)   ≥ k
/// </code>
/// Solving <c>SNR = k</c> for the minimum signal gives the closed form
/// <c>S* = (k² + √(k⁴ + 4k²N)) / 2</c>, and <c>m_lim = −2.5·log₁₀(S* / (F₀·BW·A·QE·t))</c>.
/// <c>n_pix</c> is the seeing-disc footprint in pixels (π·(FWHM/2)² over the pixel-scale
/// area, floored at one pixel for undersampled rigs). Per §3.1's snapshot semantics the
/// caller passes the PROFILE seeing; no per-binning variant exists (binning cancels to
/// first order — see the design note).</para>
/// </summary>
public static class StarDetectability {

    /// <summary>The conventional point-source detection threshold (§3.1): SNR 5 is
    /// "detected"; registration-quality centroids want ~10 — callers present both.</summary>
    public const double DefaultDetectionSnr = 5;

    /// <summary>Registration-quality centroid threshold (§3.1).</summary>
    public const double RegistrationSnr = 10;

    /// <summary>The seeing-disc footprint in pixels: the FWHM disc's area over the pixel
    /// area, floored at 1 (an undersampled rig concentrates the star inside one pixel —
    /// the noise footprint can't be smaller than the pixel that holds it).</summary>
    public static double SeeingDiscPixels(OptimalSubInputDto input, double seeingFwhmArcsec) {
        if (!double.IsFinite(seeingFwhmArcsec) || seeingFwhmArcsec <= 0) {
            throw new ArgumentOutOfRangeException(nameof(seeingFwhmArcsec),
                "seeing FWHM must be a positive, finite arcsec figure");
        }
        var pixelScaleArcsec = OptimalSubCalculator.PixelScaleArcsec(input);
        var discAreaArcsec2 = Math.PI * Math.Pow(seeingFwhmArcsec / 2.0, 2);
        return Math.Max(1.0, discAreaArcsec2 / (pixelScaleArcsec * pixelScaleArcsec));
    }

    /// <summary>The faintest V magnitude that reaches <paramref name="snrThreshold"/> in one
    /// sub of <paramref name="exposureSec"/>. Larger is fainter/deeper. Uses the shared sky
    /// rate <c>P</c> from <see cref="OptimalSubCalculator.SkyFluxEPerSecPerPx"/> (which also
    /// validates the rig/sky inputs) and the profile-snapshot seeing per §3.1.</summary>
    public static double LimitingMagnitude(
            OptimalSubInputDto input, double exposureSec, double seeingFwhmArcsec,
            double snrThreshold = DefaultDetectionSnr) {
        if (!double.IsFinite(exposureSec) || exposureSec <= 0) {
            throw new ArgumentOutOfRangeException(nameof(exposureSec),
                "exposure must be a positive, finite seconds figure");
        }
        if (!double.IsFinite(snrThreshold) || snrThreshold <= 0) {
            throw new ArgumentOutOfRangeException(nameof(snrThreshold),
                "SNR threshold must be positive and finite");
        }

        if (!double.IsFinite(input.ReadNoiseE)) {
            // > 0 alone would let +∞ through to an m_lim of −∞; a non-finite read noise is
            // garbage input, not "unset" (unset is the ≤ 0 the Tier-0 default covers below).
            throw new ArgumentOutOfRangeException(nameof(input),
                "read noise must be finite (or ≤ 0 for the generic default)");
        }

        // Shared sky rate P (e⁻/s/pixel) — validates aperture/pixel/QE/sky inputs too.
        var p = OptimalSubCalculator.SkyFluxEPerSecPerPx(input);
        var nPix = SeeingDiscPixels(input, seeingFwhmArcsec);
        var readNoise = input.ReadNoiseE > 0 ? input.ReadNoiseE : OptimalSubCalculator.DefaultReadNoiseE;

        // Background + read variance over the disc, then the minimum detectable signal from
        // SNR = S/√(S+N) = k  →  S² − k²S − k²N = 0  →  S* = (k² + √(k⁴ + 4k²N))/2.
        var n = nPix * (p * exposureSec + readNoise * readNoise);
        var k2 = snrThreshold * snrThreshold;
        var sMin = (k2 + Math.Sqrt(k2 * k2 + 4.0 * k2 * n)) / 2.0;

        // Convert the minimum signal back to a magnitude via the mag-0 electron rate.
        var apertureAreaCm2 = OptimalSubCalculator.ApertureAreaCm2(input);
        var mag0ElectronsPerSec = OptimalSubCalculator.PhotonFluxMag0PerCm2PerNm
            * input.FilterBandwidthNm * apertureAreaCm2 * input.QuantumEfficiency;
        return -2.5 * Math.Log10(sMin / (mag0ElectronsPerSec * exposureSec));
    }
}
