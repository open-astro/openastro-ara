#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>One §59.2 Phase-1 calibration sample: the focuser offset and the §59.3
    /// <see cref="FocusFeatureVector"/> measured at a frame captured there.</summary>
    public readonly record struct FocusCalibrationSample(double FocuserOffset, FocusFeatureVector Features);

    /// <summary>
    /// §59.2 Smart Focus inverse map — the first consumer of the §59.3 <see cref="FocusFeatureVector"/>.
    /// A Classic-AF sweep produces labelled <c>(offset, feature vector)</c> samples; ARA fits the V-curve
    /// as before (via <see cref="FocusCurveFit"/>) to locate best focus, then stores the INVERSE mapping —
    /// given a frame's defocus feature, how far is the focuser from best focus? — so a later session can read
    /// the rig's state from a single out-of-focus frame instead of re-running a full sweep (PORT_PLAYBOOK.md
    /// §59.2/§59.3). Pure math, no hardware, so it's fully unit-testable against synthetic V-curves.
    /// <para>This slice predicts the move MAGNITUDE only (the dominant §59.4 refractor feature, HFR, rises
    /// monotonically with distance from focus, so both arms fold onto one <c>feature → |Δoffset|</c> curve).
    /// It does NOT resolve direction (in vs out of focus): that needs the intra/extra-focal asymmetry
    /// feature, which isn't built yet — a Phase-2 second-shot confirm (§59.11) resolves the sign. Predicting
    /// magnitude from one frame is already the core §59.2 win over a full sweep.</para>
    /// </summary>
    /// <summary>§59.4 — the feature the inverse table is keyed on. <see cref="MedianHfr"/> is the
    /// universal default; <see cref="DonutOuterDiameter"/> is the obstructed-scope upgrade (linear in
    /// defocus where the calibration data confirms it — see <c>FocusInverseMap.Build</c>).</summary>
    public enum FocusMagnitudeKey {
        MedianHfr,
        DonutOuterDiameter,
    }

    public sealed class FocusInverseMap {

        /// <summary>Minimum labelled samples needed to fit the calibrating V-curve.</summary>
        public const int MinSamples = FocusCurveFit.MinPoints;

        /// <summary>§59.4 — the fraction of adjacent table pairs that must be strictly monotone for the
        /// donut-diameter key to be trusted; below it (near-focus degeneracy, noisy diameters) the map
        /// silently falls back to the HFR key.</summary>
        public const double MinKeyConcordance = 0.8;

        /// <summary>The focuser offset of best focus (the fitted V-curve vertex).</summary>
        public double BestFocusOffset { get; }

        /// <summary>The HFR the fit predicts at best focus — the Smart runner's success reference
        /// (HFR is minimal at focus regardless of optic or magnitude key).</summary>
        public double InFocusHfr { get; }

        /// <summary>The most-defocused calibrated HFR (informational; range checks use the key axis).</summary>
        public double MaxCalibratedHfr { get; }

        /// <summary>The move magnitude at the widest calibrated defocus.</summary>
        public double MaxCalibratedMagnitude { get; }

        /// <summary>§59.4 — which feature this map's table is keyed on. <see cref="FocusMagnitudeKey.MedianHfr"/>
        /// unless the telescope type prefers the donut diameter AND the calibration data confirmed it
        /// monotone (≥ <see cref="MinKeyConcordance"/> concordance).</summary>
        public FocusMagnitudeKey Key { get; }

        // (key value → |Δoffset to focus|), sorted by key ascending; [0] is the in-focus anchor
        // (fitted vertex HFR for the HFR key, 0 for the donut key — a zero-diameter donut IS focus).
        private readonly (double KeyValue, double Magnitude)[] _table;

        private FocusInverseMap(double bestFocusOffset, double inFocusHfr, double maxCalibratedHfr,
                FocusMagnitudeKey key, (double KeyValue, double Magnitude)[] table) {
            BestFocusOffset = bestFocusOffset;
            InFocusHfr = inFocusHfr;
            MaxCalibratedHfr = maxCalibratedHfr;
            Key = key;
            _table = table;
            MaxCalibratedMagnitude = table[^1].Magnitude;
        }

        /// <summary>
        /// Build the inverse map from a sweep's labelled samples, or return <c>null</c> when it can't be
        /// calibrated: fewer than <see cref="MinSamples"/> usable (star-bearing) frames, or the fit finds no
        /// clean focus minimum that the sweep actually brackets (<see cref="FocusCurveFitResult.WithinSampledRange"/>
        /// — an extrapolated vertex means the sweep never captured focus, so the folded distances are
        /// unreliable). Starless samples (<see cref="FocusFeatureVector.StarCount"/> 0) carry no defocus
        /// signal and are dropped. This overload keys on HFR (the pre-§59.4 behavior).
        /// </summary>
        public static FocusInverseMap? Build(IReadOnlyList<FocusCalibrationSample> samples) =>
            Build(samples, TelescopeType.Other);

        /// <summary>
        /// §59.4 typed build: an obstructed <paramref name="type"/> prefers the donut OUTER diameter as the
        /// magnitude key (linear in defocus, unlike HFR's curve) — but only when THIS calibration's data
        /// walks monotonically with defocus (≥ <see cref="MinKeyConcordance"/> of adjacent pairs strictly
        /// increasing); a near-focus sweep whose diameters degrade to FWHM-like extents, or noisy data,
        /// silently falls back to the HFR key. The V-curve fit itself is ALWAYS on HFR — only the inverse
        /// table's axis changes.
        /// </summary>
        public static FocusInverseMap? Build(IReadOnlyList<FocusCalibrationSample> samples, TelescopeType type) {
            ArgumentNullException.ThrowIfNull(samples);

            var usable = new List<FocusCalibrationSample>(samples.Count);
            var points = new List<FocusPoint>(samples.Count);
            foreach (var s in samples) {
                if (s.Features is { StarCount: > 0 }) {
                    usable.Add(s);
                    points.Add(new FocusPoint(s.FocuserOffset, s.Features.MedianHFR, s.Features.StarCount));
                }
            }

            // Fit the curve as before — the vertex is best focus. Require a usable minimum the sweep actually
            // bracketed; an extrapolated or non-minimum fit can't anchor a trustworthy inverse map.
            var fit = FocusCurveFit.FitBest(points);
            if (fit is not { IsUsable: true, WithinSampledRange: true }) {
                return null;
            }

            double best = fit.BestPosition;
            double maxHfr = 0;
            foreach (var s in usable) {
                maxHfr = Math.Max(maxHfr, s.Features.MedianHFR);
            }

            if (FocusFeatureProfile.PrefersDonutMagnitudeKey(type)) {
                var donutTable = FoldTable(usable, best, static f => f.MedianDonutOuterDiameter, anchorKeyValue: 0.0);
                if (IsConcordant(donutTable)) {
                    return new FocusInverseMap(best, fit.PredictedHfr, maxHfr, FocusMagnitudeKey.DonutOuterDiameter, donutTable);
                }
            }

            // Fold both arms onto one feature→magnitude curve: |offset − best| is the move that nulls the
            // defocus. The vertex anchors the in-focus end at (predicted HFR, magnitude 0).
            var hfrTable = FoldTable(usable, best, static f => f.MedianHFR, anchorKeyValue: fit.PredictedHfr);
            return new FocusInverseMap(best, fit.PredictedHfr, maxHfr, FocusMagnitudeKey.MedianHfr, hfrTable);
        }

        private static (double KeyValue, double Magnitude)[] FoldTable(
                List<FocusCalibrationSample> usable, double best,
                Func<FocusFeatureVector, double> key, double anchorKeyValue) {
            var table = new List<(double KeyValue, double Magnitude)>(usable.Count + 1) {
                (anchorKeyValue, 0.0),
            };
            foreach (var s in usable) {
                table.Add((key(s.Features), Math.Abs(s.FocuserOffset - best)));
            }
            table.Sort(static (a, b) => a.KeyValue.CompareTo(b.KeyValue));
            return [.. table];
        }

        // Sorted by KEY — a trustworthy key must walk the magnitude up with it. Concordance counts the
        // NON-INVERTED adjacent pairs (magnitude not decreasing): folding two symmetric arms produces
        // coincident key values at equal magnitudes, which are consistent, not violations — only a
        // magnitude that DROPS as the key grows (near-focus degeneracy, noisy diameters) betrays the key.
        private static bool IsConcordant((double KeyValue, double Magnitude)[] table) {
            int nonInverted = 0, pairs = table.Length - 1;
            for (int i = 1; i < table.Length; i++) {
                if (table[i].Magnitude >= table[i - 1].Magnitude - 1e-9) {
                    nonInverted++;
                }
            }
            return pairs > 0 && (double)nonInverted / pairs >= MinKeyConcordance;
        }

        /// <summary>
        /// Predict the MAGNITUDE (in focuser steps, non-negative) of the move needed to reach best focus from
        /// a frame with these features — the §59.2 one-frame read. Returns <c>null</c> for an unusable
        /// (starless) frame, or a query MORE defocused than anything calibrated (§59.11 "outside calibrated
        /// range" — re-sweep rather than trust an extrapolation). A frame at or inside the in-focus anchor
        /// predicts ~0. Direction (in vs out) is not resolved here — see the class remarks.
        /// </summary>
        public double? PredictOffsetMagnitude(FocusFeatureVector query) {
            ArgumentNullException.ThrowIfNull(query);
            if (query.StarCount == 0) {
                return null;
            }
            double q = Key == FocusMagnitudeKey.DonutOuterDiameter ? query.MedianDonutOuterDiameter : query.MedianHFR;
            if (q > _table[^1].KeyValue) {
                return null; // beyond the calibrated defocus — an extrapolation we won't trust
            }
            // In-focus floor keyed off the anchor's key value (the fitted vertex HFR / a zero-diameter
            // donut), NOT _table[0]: with noisy real data a sample's measured value can land BELOW the
            // least-squares vertex, taking index 0 after the sort, which would otherwise make this branch
            // return that sample's non-zero magnitude for a genuinely sharp frame. A query at or sharper
            // than best focus needs no move.
            if (q <= (Key == FocusMagnitudeKey.DonutOuterDiameter ? 0.0 : InFocusHfr)) {
                return 0.0;
            }
            // Piecewise-linear interpolation over the sorted (key → magnitude) table.
            for (int i = 1; i < _table.Length; i++) {
                if (q <= _table[i].KeyValue) {
                    var lo = _table[i - 1];
                    var hi = _table[i];
                    double span = hi.KeyValue - lo.KeyValue;
                    if (span <= 0) {
                        return hi.Magnitude; // coincident key values (folded arms) → the shared magnitude
                    }
                    double t = (q - lo.KeyValue) / span;
                    return lo.Magnitude + t * (hi.Magnitude - lo.Magnitude);
                }
            }
            return MaxCalibratedMagnitude; // q == the max key value exactly — the last bracket
        }
    }
}
