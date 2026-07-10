#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.ImageAnalysis;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// One §59.2 Smart Focus calibration sample: the absolute focuser position of a sweep probe plus the §59.3
/// <c>FocusFeatureVector</c> measured there — flattened field-by-field so the profile wire shape doesn't
/// couple to the Image-layer record (its ctor can grow; these names are the stable on-disk contract).
/// The field-by-field bridge lives HERE, in both directions (<see cref="From"/> / <see cref="ToSample"/>),
/// so a feature-vector addition means updating exactly one mapping, not every call site.
/// </summary>
public sealed record FocusCalibrationSampleDto(
    int FocuserPosition,
    int StarCount,
    double MedianHfr,
    double MedianFwhm,
    double MedianRoundness,
    double MedianPeakToBackground,
    double MedianDonutOuterDiameter,
    double MedianDonutInnerDiameter,
    double MedianRingThickness,
    double MedianDonutShadowDepth,
    // §59.3 — appended with a default so a pre-skew profile.json (or hand-built DTO) reads as 0; a flat-0
    // skew auto-fails the side-classifier's separation gate, and the next successful sweep re-records with
    // real values, so old calibrations self-heal rather than needing a migration.
    double MedianRadialSkew = 0.0) {

    /// <summary>Flatten a probe's measured feature vector into the wire shape (the sweep's store path).</summary>
    public static FocusCalibrationSampleDto From(int focuserPosition, FocusFeatureVector features) {
        ArgumentNullException.ThrowIfNull(features);
        return new FocusCalibrationSampleDto(
            FocuserPosition: focuserPosition,
            StarCount: features.StarCount,
            MedianHfr: features.MedianHFR,
            MedianFwhm: features.MedianFWHM,
            MedianRoundness: features.MedianRoundness,
            MedianPeakToBackground: features.MedianPeakToBackground,
            MedianDonutOuterDiameter: features.MedianDonutOuterDiameter,
            MedianDonutInnerDiameter: features.MedianDonutInnerDiameter,
            MedianRingThickness: features.MedianRingThickness,
            MedianDonutShadowDepth: features.MedianDonutShadowDepth,
            MedianRadialSkew: features.MedianRadialSkew);
    }

    /// <summary>Rebuild the Image-layer sample this DTO stores — the <c>FocusInverseMap.Build</c> load path
    /// (the §59.2 one-frame runner reconstructs the map from these on a later session).</summary>
    public FocusCalibrationSample ToSample() => new(
        FocuserPosition,
        new FocusFeatureVector(
            StarCount, MedianHfr, MedianFwhm, MedianRoundness, MedianPeakToBackground,
            MedianDonutOuterDiameter, MedianDonutInnerDiameter, MedianRingThickness,
            MedianDonutShadowDepth, MedianRadialSkew));
}

/// <summary>
/// §59.2/§59.3 — the Smart Focus calibration a Classic AF sweep produces, persisted in the profile so a later
/// session can predict the focuser move from a single frame (via <c>FocusInverseMap.Build</c>) instead of
/// re-running a full V-curve. Stores the RAW sweep samples, not the fitted table: rebuilding from ≤ a dozen
/// samples is trivial, the pure math type stays serialization-free, and stored samples survive future
/// feature-vector additions. Entirely daemon-owned (never client-edited); <c>null</c> at the profile level
/// means "not calibrated".
/// </summary>
/// <param name="Samples">The sweep's probe samples (absolute focuser positions; the map re-fits best focus).</param>
/// <param name="CalibratedUtc">When the calibrating sweep completed.</param>
/// <param name="FocuserTemperatureC">Focuser temperature at calibration, or null when the device reports none —
/// feeds the §59.13 <c>calibration_temp_delta_c</c> staleness gate (no reading → the gate can't fire).</param>
/// <param name="Filter">The filter the calibration ran on (recorded for diagnostics; §59.6 handles per-filter
/// differences via learned offsets, not per-filter calibration tables).</param>
public sealed record FocusCalibrationDto(
    IReadOnlyList<FocusCalibrationSampleDto> Samples,
    DateTimeOffset CalibratedUtc,
    double? FocuserTemperatureC,
    string? Filter);
