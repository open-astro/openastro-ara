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
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.10 — <see cref="CollimationEvaluator"/> reduces a calibration's donut stars into a collimation verdict.
    /// Hand-built <see cref="DetectedStar"/> lists (the detector's per-star centroid-offset output) give
    /// deterministic expectations: a coherent shadow shift reads as miscollimation with a direction, uncorrelated
    /// per-star shifts average out (false-positive resistance), off-axis and hole-less stars are excluded, and a
    /// thin sample yields no verdict.
    /// </summary>
    [TestFixture]
    public class CollimationEvaluatorTest {

        private const int W = 1000, H = 1000;

        // A donut star at (x, y) with a per-star shadow-centroid offset and donut diameters. Position packs the
        // centre the way StarDetector.Measure does (row-major), so Unpack recovers (x, y) for the near-centre test.
        private static DetectedStar Star(int x, int y, double offsetX, double offsetY,
                double outerDiameter = 20.0, double innerDiameter = 6.0) =>
            new DetectedStar {
                Position = (y * W) + x,
                DonutOuterDiameter = outerDiameter,
                DonutInnerDiameter = innerDiameter,
                DonutCentroidOffsetX = offsetX,
                DonutCentroidOffsetY = offsetY,
            };

        // A ring of near-centre stars (all within the NearCentreRadiusFraction disk) each carrying the same offset.
        private static List<DetectedStar> NearCentreStars(int count, double offsetX, double offsetY, double outer = 20.0) {
            var list = new List<DetectedStar>(count);
            for (int i = 0; i < count; i++) {
                // Spread them ±60 px around the centre — well inside 0.3·halfDiag ≈ 212 px.
                int x = 500 + ((i % 3) - 1) * 30;
                int y = 500 + ((i / 3) - 1) * 30;
                list.Add(Star(x, y, offsetX, offsetY, outer));
            }
            return list;
        }

        [Test]
        public void A_concentric_field_reads_good() {
            var verdict = CollimationEvaluator.Evaluate(NearCentreStars(8, 0.0, 0.0), W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Good));
            Assert.That(verdict.OffsetPercent, Is.LessThan(CollimationEvaluator.SlightThresholdPercent));
            Assert.That(verdict.StarsUsed, Is.EqualTo(8));
        }

        [Test]
        public void A_coherent_10pct_shift_reads_slight_with_direction() {
            // Every star's shadow is pushed +2 px on a 20 px donut → 10% of the diameter, toward +x (0°).
            var verdict = CollimationEvaluator.Evaluate(NearCentreStars(8, 2.0, 0.0, outer: 20.0), W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Slight));
            Assert.That(verdict.OffsetPercent, Is.EqualTo(10.0).Within(0.001));
            Assert.That(verdict.DirectionDegrees, Is.EqualTo(0.0).Within(0.5), "a +x shift points along 0°");
        }

        [Test]
        public void A_coherent_20pct_shift_reads_significant() {
            // +4 px on a 20 px donut, toward +y → 20% of the diameter, above the 15% significant threshold.
            var verdict = CollimationEvaluator.Evaluate(NearCentreStars(8, 0.0, 4.0, outer: 20.0), W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Significant));
            Assert.That(verdict.OffsetPercent, Is.EqualTo(20.0).Within(0.001));
            Assert.That(verdict.DirectionDegrees, Is.EqualTo(90.0).Within(0.5), "a +y shift points along 90°");
        }

        [Test]
        public void Uncorrelated_per_star_shifts_average_out() {
            // Eight near-centre stars each with a large (30% of diameter) offset, but in cancelling directions:
            // a real tilt would be coherent; atmospheric/seeing noise is not, and the vector-average kills it.
            var stars = new List<DetectedStar> {
                Star(500, 500, 6.0, 0.0), Star(470, 500, -6.0, 0.0),
                Star(530, 500, 0.0, 6.0), Star(500, 470, 0.0, -6.0),
                Star(500, 530, 4.2, 4.2), Star(470, 470, -4.2, -4.2),
                Star(530, 530, 4.2, -4.2), Star(470, 530, -4.2, 4.2),
            };
            var verdict = CollimationEvaluator.Evaluate(stars, W, H);
            Assert.That(verdict.StarsUsed, Is.EqualTo(8));
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Good),
                "uncorrelated per-star offsets must vector-average toward zero (false-positive resistance)");
            Assert.That(verdict.OffsetPercent, Is.LessThan(CollimationEvaluator.SlightThresholdPercent));
        }

        [Test]
        public void Off_axis_stars_are_excluded() {
            // Near-centre stars are clean; a coherent big offset lives only on off-axis stars (coma, not
            // collimation) — the verdict must be driven by the near-centre set and ignore the off-axis ones.
            var stars = NearCentreStars(6, 0.0, 0.0);
            for (int i = 0; i < 6; i++) {
                stars.Add(Star(950, 950, 8.0, 0.0)); // ~636 px from centre, past 0.3·halfDiag ≈ 212 px
            }
            var verdict = CollimationEvaluator.Evaluate(stars, W, H);
            Assert.That(verdict.StarsUsed, Is.EqualTo(6), "only the near-centre stars should count");
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Good));
        }

        [Test]
        public void Hole_less_stars_are_ignored() {
            // Refractor / in-focus stars have no obstruction shadow (inner diameter 0) — no collimation signal.
            var stars = NearCentreStars(8, 5.0, 0.0);
            for (int i = 0; i < stars.Count; i++) {
                var s = stars[i];
                s.DonutInnerDiameter = 0;
                stars[i] = s;
            }
            var verdict = CollimationEvaluator.Evaluate(stars, W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Insufficient));
            Assert.That(verdict.StarsUsed, Is.EqualTo(0));
            Assert.That(double.IsNaN(verdict.DirectionDegrees), Is.True);
        }

        [Test]
        public void Too_few_stars_yields_insufficient_not_a_verdict() {
            // Four donut stars (< MinStars) even with a big coherent offset must NOT raise an alarm.
            var verdict = CollimationEvaluator.Evaluate(NearCentreStars(4, 6.0, 0.0), W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Insufficient));
            Assert.That(verdict.StarsUsed, Is.EqualTo(4));
            Assert.That(verdict.OffsetPercent, Is.EqualTo(0.0));
            Assert.That(double.IsNaN(verdict.DirectionDegrees), Is.True);
        }

        [Test]
        public void Empty_input_is_insufficient() {
            var verdict = CollimationEvaluator.Evaluate(new List<DetectedStar>(), W, H);
            Assert.That(verdict.Severity, Is.EqualTo(CollimationSeverity.Insufficient));
            Assert.That(verdict.StarsUsed, Is.EqualTo(0));
        }
    }
}
