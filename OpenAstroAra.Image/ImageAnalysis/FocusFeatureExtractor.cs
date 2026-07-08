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

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>
    /// §59.3 Smart Focus feature vector for a single frame — the richer-than-HFR descriptor recorded at
    /// each Classic-AF step so the rig's defocus→focuser-offset relationship can be learned once and then
    /// inverted from a single frame (§59.2). Each field is a whole-frame aggregate of the per-star metrics
    /// <see cref="StarDetector"/> measures (<see cref="DetectedStar.FWHM"/> etc.); §59.4 later weights the
    /// fields per telescope type. This slice ships the refractor-relevant subset (HFR, FWHM, roundness,
    /// peak-to-background) — donut geometry (asymmetry, ring thickness) follows for obstructed scopes.
    /// </summary>
    public sealed record FocusFeatureVector(
        int StarCount,
        double MedianHFR,
        double MedianFWHM,
        double MedianRoundness,
        double MedianPeakToBackground) {

        /// <summary>The empty-field vector — no stars, every metric zero. Returned for a starless frame so
        /// callers never branch on null; a zero <see cref="StarCount"/> is the "unusable sample" signal.</summary>
        public static readonly FocusFeatureVector Empty = new(0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Reduces a <see cref="StarDetectionResult"/> to its §59.3 <see cref="FocusFeatureVector"/>. Uses the
    /// median (not the mean) of every per-star metric: a focus frame routinely carries a few hot pixels,
    /// cosmic rays, or half-truncated edge blobs that survive detection, and the median shrugs them off
    /// where a mean would be dragged — the same robustness rationale behind the background median+MAD.
    /// Pure, allocation-light, and hardware-free, mirroring <see cref="StarDetector"/> and FocusCurveFit.
    /// </summary>
    public static class FocusFeatureExtractor {

        public static FocusFeatureVector Extract(StarDetectionResult result) {
            ArgumentNullException.ThrowIfNull(result);
            var stars = result.StarList;
            int n = stars.Count;
            if (n == 0) {
                return FocusFeatureVector.Empty;
            }

            var hfr = new double[n];
            var fwhm = new double[n];
            var roundness = new double[n];
            var peak = new double[n];
            for (int i = 0; i < n; i++) {
                var s = stars[i];
                hfr[i] = s.HFR;
                fwhm[i] = s.FWHM;
                roundness[i] = s.Roundness;
                peak[i] = s.PeakToBackground;
            }

            return new FocusFeatureVector(
                StarCount: n,
                MedianHFR: Median(hfr),
                MedianFWHM: Median(fwhm),
                MedianRoundness: Median(roundness),
                MedianPeakToBackground: Median(peak));
        }

        // Median of a non-empty sample; sorts in place (each array is a private per-call copy). Even counts
        // average the two central order statistics, the standard continuous-median convention.
        private static double Median(double[] values) {
            Array.Sort(values);
            int mid = values.Length / 2;
            return (values.Length & 1) == 1
                ? values[mid]
                : (values[mid - 1] + values[mid]) / 2.0;
        }
    }
}
