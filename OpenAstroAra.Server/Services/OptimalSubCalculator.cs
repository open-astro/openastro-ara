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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// NEXTGEN_PLANNING §2/§3 — the Optimal-Sub calculator: the read-noise-limited sub-exposure
/// floor (the criterion popularised by Dr. Robin Glover, author of SharpCap, in his "How to Get
/// Perfect Subexposures" talk — used with his permission, 2026-06-30) intersected with the
/// sky-background saturation ceiling. Pure static math, no I/O.
/// <para><b>Model (single gray band):</b> the sky is treated as a flat spectrum through one
/// effective passband referenced to the V-band photometric zero point — no per-wavelength sky
/// spectrum or QE curve in v1. Consequently <see cref="DefaultBroadbandBandwidthNm"/> is an
/// <i>effective</i> width (100 nm), deliberately narrower than a luminance filter's physical
/// ~300 nm: the V-referenced zero point overstates flux far from V once the sky spectrum and QE
/// roll-off are considered, and 100 nm keeps the design doc's "3 nm Hα ≈ 30× less sky flux than
/// L" ratio exact. Numbers from SharpCap's Smart Histogram (a full spectral model) will differ
/// somewhat — that is expected, not a bug.</para>
/// <para><b>Bounds (v1):</b> only the two bounds derivable from the sky + camera model — the
/// read-noise floor and the sky-background saturation ceiling (<c>headroom·effectiveWell/P</c>).
/// Star/core saturation, the star-detectability floor, and the satellite-trail ceiling are
/// deliberate refinements per NEXTGEN_PLANNING §3. Note the collapsed-window criterion
/// (<c>headroom·FW &lt; C(ε)·R²</c>) is independent of <c>P</c> — a property of this simple
/// model.</para>
/// </summary>
public static class OptimalSubCalculator {

    /// <summary>Vega-referenced photon flux of a mag-0 star at V (~550 nm) in
    /// photons/s/cm²/nm — the standard ~1000 photons/s/cm²/Å zero point (≈8.8×10⁵ photons/s/cm²
    /// integrated over the ~88 nm V passband, the figure Glover's sky-flux model uses).</summary>
    public const double PhotonFluxMag0PerCm2PerNm = 1.0e4;

    /// <summary>Tier-0 generic-CMOS quantum efficiency used when the profile has none.</summary>
    public const double DefaultQuantumEfficiency = 0.8;

    /// <summary>Tier-0 generic-CMOS read noise (e⁻ RMS, mid-gain typical).</summary>
    public const double DefaultReadNoiseE = 3.5;

    /// <summary>Tier-0 generic APS-C CMOS full-well capacity (e⁻).</summary>
    public const double DefaultFullWellE = 50_000;

    /// <summary>Effective broadband (L / OSC) passband in nm — see the class remarks for why
    /// this is 100 nm rather than a luminance filter's physical ~300 nm.</summary>
    public const double DefaultBroadbandBandwidthNm = 100;

    /// <summary>Bit depth assumed for the ADC-clip bound on the effective well:
    /// <c>ElectronsPerAdu × (2¹⁶ − 1)</c>.</summary>
    private const double AdcMaxAdu = 65_535;

    /// <summary>An approximate zenith sky surface brightness (mag/arcsec²) for a Bortle class:
    /// ~22.0 at the darkest (Bortle 1) falling ~0.5 mag per class to ~18.0 at inner-city
    /// (Bortle 9). A coarse planning input, not a photometric model; class is clamped to the
    /// canonical 1–9 range. (Moved from TonightSkyService so scoring and exposure planning share
    /// one sky model.)</summary>
    public static double SkyMagFromBortle(int bortleClass) =>
        22.0 - (Math.Clamp(bortleClass, 1, 9) - 1) * 0.5;

    /// <summary><c>P</c>: the modelled sky-background flux in e⁻/s/pixel.
    /// <code>
    /// pixelScale = 206.265 · PixelSizeUm / (FocalLengthMm · ReducerFactor)   [arcsec/px]
    /// P = PhotonFluxMag0PerCm2PerNm · 10^(−0.4·SkyMag) · BandwidthNm
    ///       · π·(ApertureMm/20)²                                             [cm² aperture area]
    ///       · pixelScale²                                                    [arcsec²/px]
    ///       · QE
    /// </code></summary>
    public static double SkyFluxEPerSecPerPx(OptimalSubInputDto input) {
        ValidateFluxInputs(input);
        var pixelScaleArcsec = 206.265 * input.PixelSizeUm / (input.FocalLengthMm * input.ReducerFactor);
        var apertureAreaCm2 = Math.PI * Math.Pow(input.ApertureMm / 20.0, 2);
        return PhotonFluxMag0PerCm2PerNm
            * Math.Pow(10.0, -0.4 * input.SkyMagPerArcsec2)
            * input.FilterBandwidthNm
            * apertureAreaCm2
            * pixelScaleArcsec * pixelScaleArcsec
            * input.QuantumEfficiency;
    }

    /// <summary>The full sub-exposure window. Floor: <c>C(ε)·R²/P</c> with
    /// <c>C = 1/((1+ε)²−1)</c> and <c>ε = NoiseTolerancePct/100</c> (5% → C ≈ 9.756, the
    /// criterion popularly rounded to "10"; 3% → C ≈ 16.42). Ceiling:
    /// <c>SaturationHeadroomFraction · effectiveWell / P</c> where <c>effectiveWell</c> is the
    /// full well clipped by the 16-bit ADC when <see cref="OptimalSubInputDto.ElectronsPerAdu"/>
    /// is known. See the class remarks for the model's scope.</summary>
    public static OptimalSubResultDto Compute(OptimalSubInputDto input) {
        ValidateComputeInputs(input);
        var p = SkyFluxEPerSecPerPx(input);

        var epsilon = input.NoiseTolerancePct / 100.0;
        var c = 1.0 / (Math.Pow(1.0 + epsilon, 2) - 1.0);
        var floorSec = c * input.ReadNoiseE * input.ReadNoiseE / p;

        var effectiveWell = input.ElectronsPerAdu > 0
            ? Math.Min(input.FullWellE, input.ElectronsPerAdu * AdcMaxAdu)
            : input.FullWellE;
        var ceilingSec = input.SaturationHeadroomFraction * effectiveWell / p;

        var viable = floorSec <= ceilingSec;
        return new OptimalSubResultDto(
            SkyFluxEPerSecPerPx: p,
            FloorSec: floorSec,
            CeilingSec: ceilingSec,
            Viable: viable,
            LimitingBound: viable ? OptimalSubBound.ReadNoiseFloor : OptimalSubBound.SaturationCeiling,
            RecommendedSec: Math.Min(floorSec, ceilingSec));
    }

    private static void ValidateFluxInputs(OptimalSubInputDto input) {
        RequirePositive(input.PixelSizeUm, nameof(input.PixelSizeUm));
        RequirePositive(input.ApertureMm, nameof(input.ApertureMm));
        RequirePositive(input.FocalLengthMm, nameof(input.FocalLengthMm));
        RequirePositive(input.ReducerFactor, nameof(input.ReducerFactor));
        RequirePositive(input.QuantumEfficiency, nameof(input.QuantumEfficiency));
        if (input.QuantumEfficiency > 1.0) {
            throw new ArgumentException(
                "Quantum efficiency must be in (0, 1] — above 1 is non-physical and would silently inflate the sky-flux estimate.",
                nameof(input));
        }
        RequirePositive(input.FilterBandwidthNm, nameof(input.FilterBandwidthNm));
        if (!double.IsFinite(input.SkyMagPerArcsec2)) {
            throw new ArgumentException("Sky brightness (mag/arcsec²) must be finite.", nameof(input));
        }
    }

    private static void ValidateComputeInputs(OptimalSubInputDto input) {
        RequirePositive(input.ReadNoiseE, nameof(input.ReadNoiseE));
        RequirePositive(input.FullWellE, nameof(input.FullWellE));
        RequirePositive(input.NoiseTolerancePct, nameof(input.NoiseTolerancePct));
        if (input.NoiseTolerancePct > 100.0) {
            throw new ArgumentException(
                "Noise tolerance must be in (0, 100] % — tolerating more added noise than the shot noise itself is meaningless.",
                nameof(input));
        }
        if (input.ElectronsPerAdu < 0 || !double.IsFinite(input.ElectronsPerAdu)) {
            throw new ArgumentException("Electrons/ADU must be ≥ 0 (0 = unknown).", nameof(input));
        }
        if (!(input.SaturationHeadroomFraction > 0 && input.SaturationHeadroomFraction <= 1.0)) {
            throw new ArgumentException("Saturation headroom fraction must be in (0, 1].", nameof(input));
        }
    }

    private static void RequirePositive(double value, string name) {
        if (!(double.IsFinite(value) && value > 0)) {
            throw new ArgumentException($"{name} must be a positive, finite number.", name);
        }
    }
}
