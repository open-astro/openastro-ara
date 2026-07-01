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
/// NEXTGEN_PLANNING §2/§3 — inputs to the Optimal-Sub calculation: the read-noise-limited
/// sub-exposure floor (criterion popularised by Dr. Robin Glover of SharpCap, used with
/// permission) intersected with the sky-background saturation ceiling. All physical units are
/// in the member names. <see cref="Gain"/> and <see cref="ElectronsPerAdu"/> are context /
/// provenance for the electronics values — read noise and full well are per-gain, per-readout-
/// mode figures; <see cref="ElectronsPerAdu"/> additionally bounds the effective well via the
/// 16-bit ADC clip (0 = unknown → that bound is skipped). <see cref="NoiseTolerancePct"/> is
/// the Glover knob: the acceptable read-noise contribution to total noise (5% → the popularised
/// <c>t = 10·R²/P</c>; 3% → ≈ <c>16·R²/P</c>). <see cref="SaturationHeadroomFraction"/> is how
/// much of the effective full well the sky background may fill before the ceiling bites.
/// </summary>
public sealed record OptimalSubInputDto(
    double ReadNoiseE,
    double FullWellE,
    double ElectronsPerAdu,
    int Gain,
    double PixelSizeUm,
    double ApertureMm,
    double FocalLengthMm,
    double ReducerFactor,
    double QuantumEfficiency,
    double SkyMagPerArcsec2,
    double FilterBandwidthNm,
    double NoiseTolerancePct = 5.0,
    double SaturationHeadroomFraction = 0.8);

/// <summary>Which bound decides the recommended sub length. <see cref="ReadNoiseFloor"/> — the
/// Glover floor is reachable and is the recommendation (the subtle bound: too short is invisible;
/// longer buys no further read-noise gain). <see cref="SaturationCeiling"/> — the sky-background
/// saturation ceiling sits <i>below</i> the floor, so the window is collapsed and the ceiling is
/// the best available. <see cref="None"/> is the unset/default sentinel only (a default-constructed
/// DTO) — <c>OptimalSubCalculator.Compute</c> never returns it; downstream switches need not treat
/// it as a computed outcome. Serialized all-lowercase per the §60.6 enum convention
/// (<c>none</c>/<c>readnoisefloor</c>/<c>saturationceiling</c>).</summary>
public enum OptimalSubBound {
    None,
    ReadNoiseFloor,
    SaturationCeiling,
}

/// <summary>
/// NEXTGEN_PLANNING §2/§3 — the computed sub-exposure window. <see cref="SkyFluxEPerSecPerPx"/>
/// is <c>P</c>, the modelled sky-background flux. <see cref="FloorSec"/> is the Glover read-noise
/// floor <c>C(ε)·R²/P</c>; <see cref="CeilingSec"/> the sky-background saturation ceiling.
/// <see cref="Viable"/> is <c>floor ≤ ceiling</c>. <see cref="RecommendedSec"/> =
/// <c>min(floor, ceiling)</c> — per Glover, going past the floor yields no further read-noise
/// gain while quietly costing dynamic range, so the floor <i>is</i> the recommendation; when the
/// ceiling wins, <see cref="LimitingBound"/> says so. <see cref="AssumedDefaults"/> names every
/// input the calculator endpoint had to default (snake_case field names) so advice stays
/// transparent — null when all inputs were explicit (e.g. from the pure calculator).
/// </summary>
public sealed record OptimalSubResultDto(
    double SkyFluxEPerSecPerPx,
    double FloorSec,
    double CeilingSec,
    bool Viable,
    OptimalSubBound LimitingBound,
    double RecommendedSec,
    IReadOnlyList<string>? AssumedDefaults = null);
