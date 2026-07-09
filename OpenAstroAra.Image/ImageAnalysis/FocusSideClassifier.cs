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
    /// §59.3 — the side-of-focus verdict: <see cref="Direction"/> is the MOVE direction toward best focus
    /// (+1 = the frame reads as BELOW best, move the focuser up; −1 = above best, move down; 0 =
    /// unresolved — the caller keeps its heuristic). <see cref="Confidence"/> ∈ [0, 1] is the weighted
    /// agreement of the qualifying features (0 when unresolved).
    /// </summary>
    public readonly record struct FocusSideVerdict(int Direction, double Confidence) {
        public static readonly FocusSideVerdict Unresolved = new(0, 0.0);
    }

    /// <summary>
    /// §59.3 — the side-of-focus classifier: LEARNS which features separate the calibration sweep's two
    /// arms (samples below vs above the fitted best-focus position) and with what sign, then classifies a
    /// single frame's features to an arm — resolving the direction the magnitude-only
    /// <see cref="FocusInverseMap"/> deliberately leaves open.
    ///
    /// Physics honesty: for an aberration-free optic, intra- and extra-focal images are mirror images —
    /// a single mono frame cannot resolve the sign. What breaks the symmetry on real rigs is rig-specific
    /// aberration (dominantly spherical), whose signature (e.g. <c>MedianRadialSkew</c>, ring/shadow shape)
    /// flips across focus with a rig-specific sign. So nothing here assumes a convention: a feature only
    /// participates if the sweep's arms actually separate in it — consistently signed at every probed
    /// magnitude and by at least <see cref="MinRelativeSeparation"/> relative to the feature's scale. A
    /// well-corrected rig (or a noisy sweep) qualifies nothing and every classification returns
    /// <see cref="FocusSideVerdict.Unresolved"/> — the §59.2 runner then keeps today's
    /// toward-calibrated-best heuristic, so the classifier can only improve behavior, never regress it.
    ///
    /// Pure math over the same labelled samples <see cref="FocusInverseMap"/> consumes; the fitted best
    /// position is passed in (no duplicate curve fit). Lifecycle mirrors the map: <see cref="Build"/>
    /// returns <c>null</c> when the samples can't support a classifier at all (fewer than
    /// <see cref="MinSamplesPerArm"/> usable samples on either arm, or no magnitude overlap).
    /// </summary>
    public sealed class FocusSideClassifier {

        /// <summary>Each arm needs at least this many usable (star-bearing) samples to interpolate.</summary>
        public const int MinSamplesPerArm = 2;

        /// <summary>A feature qualifies only if the arms separate by at least this fraction of the
        /// feature's own scale, averaged over the probed magnitudes — below it, night-to-night seeing
        /// noise would swamp the signal.</summary>
        public const double MinRelativeSeparation = 0.10;

        /// <summary>A verdict below this weighted-agreement confidence reports Unresolved instead —
        /// a coin-flip direction is worse than the caller's heuristic.</summary>
        public const double MinDirectionConfidence = 0.35;

        // Denominator floor for the relative-separation scale, so a feature that is ~0 on both arms
        // (e.g. donut diameters on a refractor) reads "no separation" rather than dividing by zero.
        private const double ScaleFloor = 1e-6;

        // The candidate side features: everything except MedianHFR (the inverse map's magnitude key —
        // folded, so by construction it cannot separate the arms) and StarCount (sky-dependent, not
        // optics-dependent). §59.4 will restrict this set per telescope type in a later slice.
        private static readonly (string Name, Func<FocusFeatureVector, double> Get)[] CandidateFeatures = [
            ("fwhm", f => f.MedianFWHM),
            ("roundness", f => f.MedianRoundness),
            ("peak_to_background", f => f.MedianPeakToBackground),
            ("donut_outer_diameter", f => f.MedianDonutOuterDiameter),
            ("donut_inner_diameter", f => f.MedianDonutInnerDiameter),
            ("ring_thickness", f => f.MedianRingThickness),
            ("donut_shadow_depth", f => f.MedianDonutShadowDepth),
            ("radial_skew", f => f.MedianRadialSkew),
        ];

        private sealed record QualifiedFeature(
            Func<FocusFeatureVector, double> Get,
            double Weight,
            (double M, double Value)[] InTable,
            (double M, double Value)[] OutTable);

        private readonly List<QualifiedFeature> _features;
        private readonly double _minMagnitude;
        private readonly double _maxMagnitude;

        /// <summary>The magnitude range (focuser steps from best) over which both arms are calibrated —
        /// classification is only attempted inside (queries above clamp to the edge; below → Unresolved).</summary>
        public double MinMagnitude => _minMagnitude;

        public double MaxMagnitude => _maxMagnitude;

        /// <summary>The number of features whose arm separation passed the qualification gates — 0 means
        /// the classifier is alive but every verdict will be Unresolved (a symmetric, well-corrected rig).</summary>
        public int QualifiedFeatureCount => _features.Count;

        private FocusSideClassifier(List<QualifiedFeature> features, double minMagnitude, double maxMagnitude) {
            _features = features;
            _minMagnitude = minMagnitude;
            _maxMagnitude = maxMagnitude;
        }

        /// <summary>
        /// Learn the arm signatures from a sweep's labelled samples. <paramref name="bestFocusOffset"/> is
        /// the fitted vertex the samples' offsets are measured against (the caller already has it from
        /// <see cref="FocusInverseMap.BestFocusOffset"/> — no re-fit here). Returns <c>null</c> when either
        /// arm has fewer than <see cref="MinSamplesPerArm"/> usable samples or the arms share no magnitude
        /// range; returns a classifier with <see cref="QualifiedFeatureCount"/> 0 (every verdict
        /// Unresolved) when the arms exist but nothing separates them.
        /// </summary>
        public static FocusSideClassifier? Build(IReadOnlyList<FocusCalibrationSample> samples, double bestFocusOffset) {
            ArgumentNullException.ThrowIfNull(samples);

            List<(double M, FocusFeatureVector F)> inArm = [], outArm = [];
            foreach (var s in samples) {
                if (s.Features is not { StarCount: > 0 }) {
                    continue;
                }
                double m = Math.Abs(s.FocuserOffset - bestFocusOffset);
                if (s.FocuserOffset < bestFocusOffset) {
                    inArm.Add((m, s.Features));
                } else if (s.FocuserOffset > bestFocusOffset) {
                    outArm.Add((m, s.Features));
                }
                // A sample exactly AT best carries no side label; skip it.
            }
            if (inArm.Count < MinSamplesPerArm || outArm.Count < MinSamplesPerArm) {
                return null;
            }
            inArm.Sort(static (a, b) => a.M.CompareTo(b.M));
            outArm.Sort(static (a, b) => a.M.CompareTo(b.M));

            // The overlap: only magnitudes BOTH arms actually sampled are comparable — outside it, one
            // arm's value would be an extrapolation and the "separation" an artifact.
            double mLo = Math.Max(inArm[0].M, outArm[0].M);
            double mHi = Math.Min(inArm[^1].M, outArm[^1].M);
            if (mHi <= mLo) {
                return null;
            }

            // Probe points: every sampled magnitude (either arm) inside the overlap — the places where at
            // least one arm has a real measurement rather than pure interpolation.
            var probes = new List<double>();
            foreach (var (m, _) in inArm) {
                if (m >= mLo && m <= mHi) probes.Add(m);
            }
            foreach (var (m, _) in outArm) {
                if (m >= mLo && m <= mHi) probes.Add(m);
            }

            var qualified = new List<QualifiedFeature>();
            foreach (var (_, get) in CandidateFeatures) {
                var inTable = BuildTable(inArm, get);
                var outTable = BuildTable(outArm, get);

                // Qualification: the arms must separate with ONE consistent sign at every probe point
                // (a sign flip means the feature crosses — unusable as a side signal), and by enough
                // relative to the feature's own scale that seeing noise won't swamp it.
                int sign = 0;
                double relSum = 0;
                bool consistent = true;
                foreach (var m in probes) {
                    double vIn = Interpolate(inTable, m);
                    double vOut = Interpolate(outTable, m);
                    double d = vOut - vIn;
                    double scale = Math.Max(Math.Max(Math.Abs(vOut), Math.Abs(vIn)), ScaleFloor);
                    int s = Math.Sign(d);
                    if (s == 0) {
                        consistent = false;
                        break;
                    }
                    if (sign == 0) {
                        sign = s;
                    } else if (s != sign) {
                        consistent = false;
                        break;
                    }
                    relSum += Math.Abs(d) / scale;
                }
                if (!consistent) {
                    continue;
                }
                double meanRel = relSum / probes.Count;
                if (meanRel < MinRelativeSeparation) {
                    continue;
                }
                qualified.Add(new QualifiedFeature(get, Math.Min(meanRel, 1.0), inTable, outTable));
            }

            return new FocusSideClassifier(qualified, mLo, mHi);
        }

        /// <summary>
        /// Classify a frame's features to an arm, given the move magnitude the inverse map already
        /// predicted for it (the classifier compares the frame against what EACH arm looks like at that
        /// same defocus). Unresolved when: nothing qualified at build time, the frame is starless, the
        /// magnitude is inside <see cref="MinMagnitude"/> (the arms converge at focus — and direction
        /// barely matters there), or the features don't lean far enough toward either arm.
        /// </summary>
        public FocusSideVerdict Classify(FocusFeatureVector query, double magnitude) {
            ArgumentNullException.ThrowIfNull(query);
            if (_features.Count == 0 || query.StarCount == 0 || magnitude < _minMagnitude) {
                return FocusSideVerdict.Unresolved;
            }
            double m = Math.Min(magnitude, _maxMagnitude);

            double score = 0, weightSum = 0;
            foreach (var f in _features) {
                double q = f.Get(query);
                double dIn = Math.Abs(q - Interpolate(f.InTable, m));
                double dOut = Math.Abs(q - Interpolate(f.OutTable, m));
                // ∈ [−1, +1]: positive = the query sits closer to the OUT arm's signature at this defocus.
                double vote = (dIn - dOut) / Math.Max(dIn + dOut, ScaleFloor);
                score += f.Weight * vote;
                weightSum += f.Weight;
            }
            double confidence = Math.Abs(score) / weightSum;
            if (confidence < MinDirectionConfidence) {
                return FocusSideVerdict.Unresolved;
            }
            // OUT arm = the frame reads ABOVE best → move DOWN (−1); IN arm = below best → move UP (+1).
            return new FocusSideVerdict(score > 0 ? -1 : 1, confidence);
        }

        // Mean value per distinct magnitude (a sweep can sample the same |offset| once per arm side, but a
        // duplicate within one arm — e.g. re-probes — averages), sorted ascending for interpolation.
        private static (double M, double Value)[] BuildTable(
                List<(double M, FocusFeatureVector F)> arm, Func<FocusFeatureVector, double> get) {
            var table = new List<(double M, double Value)>(arm.Count);
            int i = 0;
            while (i < arm.Count) {
                double m = arm[i].M;
                double sum = 0;
                int n = 0;
                while (i < arm.Count && arm[i].M == m) {
                    sum += get(arm[i].F);
                    n++;
                    i++;
                }
                table.Add((m, sum / n));
            }
            return [.. table];
        }

        // Piecewise-linear over the sorted table, clamped at both ends (same convention as the inverse map).
        private static double Interpolate((double M, double Value)[] table, double m) {
            if (m <= table[0].M) {
                return table[0].Value;
            }
            for (int i = 1; i < table.Length; i++) {
                if (m <= table[i].M) {
                    var lo = table[i - 1];
                    var hi = table[i];
                    double span = hi.M - lo.M;
                    if (span <= 0) {
                        return hi.Value;
                    }
                    double t = (m - lo.M) / span;
                    return lo.Value + t * (hi.Value - lo.Value);
                }
            }
            return table[^1].Value;
        }
    }
}
