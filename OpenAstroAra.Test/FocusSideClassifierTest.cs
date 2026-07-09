#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Image.ImageAnalysis;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.3 — the side-of-focus classifier: learns which features separate a calibration sweep's two arms
    /// (below vs above fitted best focus) and classifies a single frame to an arm. Synthetic "rigs" build
    /// labelled sample sets whose arm signatures are exactly controlled, so every gate (per-arm minimum,
    /// overlap, sign consistency, relative separation, confidence) is exercised deterministically.
    /// </summary>
    [TestFixture]
    public class FocusSideClassifierTest {

        private const double Best = 10_000;

        /// <summary>A feature vector at defocus magnitude <paramref name="m"/> — HFR follows a V-curve arm
        /// (the classifier ignores it by design), the side-signature fields are caller-controlled.</summary>
        private static FocusFeatureVector Vec(double m, double skew = 0.0, double shadow = 0.6,
                double peakToBackground = 40.0, int stars = 40) =>
            new(StarCount: stars, MedianHFR: 1.5 + m / 100.0, MedianFWHM: (1.5 + m / 100.0) * 2.0,
                MedianRoundness: 0.9, MedianPeakToBackground: peakToBackground,
                MedianDonutOuterDiameter: m / 20.0, MedianDonutInnerDiameter: m / 60.0,
                MedianRingThickness: m / 30.0, MedianDonutShadowDepth: shadow, MedianRadialSkew: skew);

        private static FocusCalibrationSample At(double offset, FocusFeatureVector f) => new(offset, f);

        /// <summary>An aberrated rig: the radial skew is −0.4 on the below-best arm and +0.4 above (the
        /// spherical-aberration flip), shadow depth also separates (0.8 vs 0.5). Sampled at ±100/200/300.</summary>
        private static List<FocusCalibrationSample> AberratedRig() {
            var samples = new List<FocusCalibrationSample>();
            foreach (var m in new[] { 100.0, 200.0, 300.0 }) {
                samples.Add(At(Best - m, Vec(m, skew: -0.4, shadow: 0.8)));
                samples.Add(At(Best + m, Vec(m, skew: 0.4, shadow: 0.5)));
            }
            return samples;
        }

        [Test]
        public void An_aberrated_rig_classifies_each_arm_with_confidence() {
            var classifier = FocusSideClassifier.Build(AberratedRig(), Best);
            Assert.That(classifier, Is.Not.Null);
            Assert.That(classifier!.QualifiedFeatureCount, Is.GreaterThanOrEqualTo(2), "skew + shadow depth both separate");

            // A frame that LOOKS like the below-best arm (negative skew, deep shadow) at an interpolated
            // magnitude no sample sits at: the verdict is "move UP toward best" (+1).
            var below = classifier.Classify(Vec(150, skew: -0.4, shadow: 0.8), magnitude: 150);
            Assert.That(below.Direction, Is.EqualTo(1), "below best → move up");
            Assert.That(below.Confidence, Is.GreaterThanOrEqualTo(FocusSideClassifier.MinDirectionConfidence));

            var above = classifier.Classify(Vec(150, skew: 0.4, shadow: 0.5), magnitude: 150);
            Assert.That(above.Direction, Is.EqualTo(-1), "above best → move down");
            Assert.That(above.Confidence, Is.GreaterThanOrEqualTo(FocusSideClassifier.MinDirectionConfidence));
        }

        [Test]
        public void A_symmetric_rig_qualifies_nothing_and_stays_unresolved() {
            // A perfectly corrected optic: both arms look identical at matched defocus — there is no side
            // signal, and the classifier must say so rather than guess.
            var samples = new List<FocusCalibrationSample>();
            foreach (var m in new[] { 100.0, 200.0, 300.0 }) {
                samples.Add(At(Best - m, Vec(m)));
                samples.Add(At(Best + m, Vec(m)));
            }

            var classifier = FocusSideClassifier.Build(samples, Best);

            Assert.That(classifier, Is.Not.Null, "arms exist — the classifier builds");
            Assert.That(classifier!.QualifiedFeatureCount, Is.EqualTo(0), "nothing separates identical arms");
            Assert.That(classifier.Classify(Vec(150, skew: -0.4), 150), Is.EqualTo(FocusSideVerdict.Unresolved));
        }

        [Test]
        public void Fewer_than_two_samples_on_an_arm_yields_no_classifier() {
            var samples = new List<FocusCalibrationSample> {
                At(Best - 100, Vec(100, skew: -0.4)),
                At(Best - 200, Vec(200, skew: -0.4)),
                At(Best + 100, Vec(100, skew: 0.4)), // only ONE above-best sample — nothing to interpolate
            };
            Assert.That(FocusSideClassifier.Build(samples, Best), Is.Null);
        }

        [Test]
        public void A_near_focus_query_is_unresolved() {
            var classifier = FocusSideClassifier.Build(AberratedRig(), Best)!;
            // Inside the innermost calibrated magnitude the arms converge — and a sub-100-step move barely
            // needs a direction anyway. Never guess there.
            Assert.That(classifier.Classify(Vec(50, skew: -0.4, shadow: 0.8), magnitude: 50),
                Is.EqualTo(FocusSideVerdict.Unresolved));
        }

        [Test]
        public void A_query_beyond_the_calibrated_range_clamps_and_still_classifies() {
            var classifier = FocusSideClassifier.Build(AberratedRig(), Best)!;
            // The magnitude clamps to the widest calibrated defocus; the arm signatures there still apply.
            var verdict = classifier.Classify(Vec(500, skew: -0.4, shadow: 0.8), magnitude: 500);
            Assert.That(verdict.Direction, Is.EqualTo(1));
        }

        [Test]
        public void A_sign_inconsistent_feature_is_disqualified() {
            // The skew CROSSES between the arms (below-arm rises from −0.4 to +0.4 with defocus, above-arm
            // falls from +0.4 to −0.4): its arm difference flips sign across the overlap, so it carries no
            // usable side convention. Everything else is symmetric → the whole classifier stays Unresolved.
            var samples = new List<FocusCalibrationSample> {
                At(Best - 100, Vec(100, skew: -0.4)),
                At(Best - 300, Vec(300, skew: 0.4)),
                At(Best + 100, Vec(100, skew: 0.4)),
                At(Best + 300, Vec(300, skew: -0.4)),
            };

            var classifier = FocusSideClassifier.Build(samples, Best);

            Assert.That(classifier, Is.Not.Null);
            Assert.That(classifier!.QualifiedFeatureCount, Is.EqualTo(0),
                "a feature whose arm difference flips sign across the overlap must not qualify");
        }

        [Test]
        public void A_separation_below_the_relative_floor_is_disqualified() {
            // Peak-to-background separates by only ~2.5% of its scale (40 vs 41) — real seeing moves it
            // more than that night to night, so it must not qualify as a side signal.
            var samples = new List<FocusCalibrationSample>();
            foreach (var m in new[] { 100.0, 200.0, 300.0 }) {
                samples.Add(At(Best - m, Vec(m, peakToBackground: 40.0)));
                samples.Add(At(Best + m, Vec(m, peakToBackground: 41.0)));
            }

            var classifier = FocusSideClassifier.Build(samples, Best);

            Assert.That(classifier, Is.Not.Null);
            Assert.That(classifier!.QualifiedFeatureCount, Is.EqualTo(0));
        }

        [Test]
        public void An_ambiguous_frame_between_the_arms_is_unresolved() {
            var classifier = FocusSideClassifier.Build(AberratedRig(), Best)!;
            // Features exactly midway between the arm signatures lean toward neither side — confidence
            // lands under the floor and the verdict must not pick a winner.
            var verdict = classifier.Classify(Vec(150, skew: 0.0, shadow: 0.65), magnitude: 150);
            Assert.That(verdict, Is.EqualTo(FocusSideVerdict.Unresolved));
        }

        [Test]
        public void A_starless_query_is_unresolved() {
            var classifier = FocusSideClassifier.Build(AberratedRig(), Best)!;
            Assert.That(classifier.Classify(FocusFeatureVector.Empty, magnitude: 150),
                Is.EqualTo(FocusSideVerdict.Unresolved));
        }

        [Test]
        public void Starless_samples_are_dropped_before_arm_counting() {
            // Two good samples per arm plus starless junk: the junk must not count toward the per-arm
            // minimum nor poison the tables.
            var samples = new List<FocusCalibrationSample> {
                At(Best - 100, Vec(100, skew: -0.4)),
                At(Best - 300, Vec(300, skew: -0.4)),
                At(Best - 200, FocusFeatureVector.Empty),
                At(Best + 100, Vec(100, skew: 0.4)),
                At(Best + 300, Vec(300, skew: 0.4)),
                At(Best + 200, FocusFeatureVector.Empty),
            };

            var classifier = FocusSideClassifier.Build(samples, Best);

            Assert.That(classifier, Is.Not.Null);
            Assert.That(classifier!.Classify(Vec(200, skew: -0.4), 200).Direction, Is.EqualTo(1));
        }
    }
}
