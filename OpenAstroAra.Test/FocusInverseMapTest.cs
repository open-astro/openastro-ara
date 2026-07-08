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
using NUnit.Framework;
using OpenAstroAra.Image.ImageAnalysis;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.2 Smart Focus inverse map — pure-math coverage against synthetic V-curves. A frame's HFR traces a
    /// hyperbola <c>√(hfr0² + (k·Δoffset)²)</c> against focuser offset; the map fits the vertex (best focus)
    /// and inverts the folded feature→magnitude relation. Same synthetic-frame discipline as
    /// FocusCurveFitTest / FocusFeatureExtractorTest — no rig, no Alpaca.
    /// </summary>
    [TestFixture]
    public class FocusInverseMapTest {

        private const double Best = 5000;
        private const double Hfr0 = 1.5;   // in-focus HFR
        private const double K = 0.01;     // HFR curvature per focuser step

        private static FocusFeatureVector Feature(double hfr, int starCount = 30) =>
            new(StarCount: starCount, MedianHFR: hfr, MedianFWHM: hfr * 1.6, MedianRoundness: 0.9,
                MedianPeakToBackground: 40);

        private static double HfrAt(double offset) {
            double d = offset - Best;
            return Math.Sqrt(Hfr0 * Hfr0 + (K * d) * (K * d));
        }

        // A symmetric sweep of `count` frames straddling best focus at `step` spacing.
        private static List<FocusCalibrationSample> Sweep(double startOffset, double step, int count, int starCount = 30) {
            var list = new List<FocusCalibrationSample>(count);
            for (int i = 0; i < count; i++) {
                double offset = startOffset + i * step;
                list.Add(new FocusCalibrationSample(offset, Feature(HfrAt(offset), starCount)));
            }
            return list;
        }

        [Test]
        public void Build_locates_best_focus_at_the_vertex() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9));
            Assert.That(map, Is.Not.Null);
            Assert.That(map!.BestFocusOffset, Is.EqualTo(Best).Within(15), "the fitted vertex is best focus");
            Assert.That(map.InFocusHfr, Is.EqualTo(Hfr0).Within(0.3));
        }

        [Test]
        public void Predicts_the_move_magnitude_at_a_held_out_defocus() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9))!;
            // A frame 250 steps out of focus: HFR = √(1.5² + (0.01·250)²) ≈ 2.915. The map should predict a
            // ~250-step move (magnitude only — direction is Phase-2).
            double queryHfr = HfrAt(Best + 250);
            var predicted = map.PredictOffsetMagnitude(Feature(queryHfr));
            Assert.That(predicted, Is.Not.Null);
            Assert.That(predicted!.Value, Is.EqualTo(250).Within(20),
                "piecewise-linear interpolation over the folded feature→magnitude table");
        }

        [Test]
        public void Predicts_symmetric_magnitude_on_either_arm() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9))!;
            // The same HFR on the intra- and extra-focal arm folds to the same magnitude (sign unresolved).
            double inside = map.PredictOffsetMagnitude(Feature(HfrAt(Best - 320)))!.Value;
            double outside = map.PredictOffsetMagnitude(Feature(HfrAt(Best + 320)))!.Value;
            Assert.That(inside, Is.EqualTo(outside).Within(1e-6), "both arms fold onto one magnitude curve");
            Assert.That(outside, Is.EqualTo(320).Within(25));
        }

        [Test]
        public void An_in_focus_frame_predicts_near_zero() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9))!;
            var predicted = map.PredictOffsetMagnitude(Feature(Hfr0));
            Assert.That(predicted, Is.Not.Null);
            Assert.That(predicted!.Value, Is.EqualTo(0).Within(1e-6), "at the in-focus HFR the move magnitude is ~0");
        }

        [Test]
        public void A_query_beyond_the_calibrated_range_returns_null() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9))!;
            var predicted = map.PredictOffsetMagnitude(Feature(map.MaxCalibratedHfr + 1.0));
            Assert.That(predicted, Is.Null, "§59.11: an out-of-range extrapolation is not trusted — re-sweep");
        }

        [Test]
        public void A_starless_query_returns_null() {
            var map = FocusInverseMap.Build(Sweep(4600, 100, 9))!;
            Assert.That(map.PredictOffsetMagnitude(FocusFeatureVector.Empty), Is.Null);
            Assert.That(map.PredictOffsetMagnitude(Feature(2.5, starCount: 0)), Is.Null);
        }

        [Test]
        public void Too_few_samples_cannot_be_calibrated() {
            var two = new List<FocusCalibrationSample> {
                new(4900, Feature(HfrAt(4900))),
                new(5100, Feature(HfrAt(5100))),
            };
            Assert.That(FocusInverseMap.Build(two), Is.Null);
        }

        [Test]
        public void A_sweep_with_no_focus_minimum_cannot_be_calibrated() {
            // Monotonically rising HFR (the sweep never bracketed focus) → no usable vertex → null.
            var mono = new List<FocusCalibrationSample>();
            for (int i = 0; i < 9; i++) {
                double offset = 4600 + i * 100;
                mono.Add(new FocusCalibrationSample(offset, Feature(1.0 + offset * 0.001)));
            }
            Assert.That(FocusInverseMap.Build(mono), Is.Null);
        }

        [Test]
        public void An_all_starless_sweep_cannot_be_calibrated() {
            var blind = new List<FocusCalibrationSample>();
            for (int i = 0; i < 9; i++) {
                double offset = 4600 + i * 100;
                blind.Add(new FocusCalibrationSample(offset, Feature(HfrAt(offset), starCount: 0)));
            }
            Assert.That(FocusInverseMap.Build(blind), Is.Null);
        }
    }
}
