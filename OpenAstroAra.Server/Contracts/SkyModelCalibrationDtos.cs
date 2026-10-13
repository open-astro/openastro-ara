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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>#1140 P2 — one filter kind's sky-model calibration: the scalar <c>K</c> that scales the
/// spec-sheet sky flux (aperture, pixel scale, QE, passband, SQM) to what this rig's frames measured.
/// <c>Passed</c> is the Phase-V gate (|K| in 0.4–1.5 and scatter under 30 % over at least three
/// frames); a kind that fails keeps its figures so the user can see why, but the planner must not
/// apply it. <c>Note</c> says what stopped the fit (one exposure length and no bias frames, no e⁻/ADU…).</summary>
public sealed record SkyModelFilterCalibrationDto(
    string Kind,
    int Bin,
    double? K,
    double? ScatterPct,
    double? PedestalAdu,
    double? MeasuredRateEPerSecPerPx,
    double? PredictedRateEPerSecPerPx,
    int FrameCount,
    bool Passed,
    string? Note);

/// <summary>#1140 P2 — the per-profile sky-model calibration record, fitted from the library's recent
/// light frames (their 5th-percentile background against exposure, per filter kind and binning).
/// Daemon-derived data; stripped from a shared profile.</summary>
public sealed record SkyModelCalibrationDto(
    DateTimeOffset FittedUtc,
    int FramesConsidered,
    IReadOnlyList<SkyModelFilterCalibrationDto> Filters);
