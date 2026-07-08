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
    /// §59.3/§59.4 Smart Focus feature vector — the per-star shape metrics <see cref="StarDetector"/> now
    /// measures (FWHM, roundness, peak-to-background) and the median <see cref="FocusFeatureExtractor"/>
    /// aggregation over a frame. Synthetic Gaussian stars with known width/elongation/brightness give
    /// deterministic ordering expectations; the aggregation is exercised on hand-built star lists.
    /// </summary>
    [TestFixture]
    public class FocusFeatureExtractorTest {

        private const ushort Background = 1000;

        // Stamp an (optionally elliptical) Gaussian star into a flat-background frame.
        private static void AddStar(ushort[] frame, int width, int height, int cx, int cy,
                double amplitude, double sigmaX = 1.5, double sigmaY = 1.5) {
            int r = (int)Math.Ceiling(Math.Max(sigmaX, sigmaY) * 3);
            for (int dy = -r; dy <= r; dy++) {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    double v = amplitude * Math.Exp(-(dx * dx) / (2 * sigmaX * sigmaX) - (dy * dy) / (2 * sigmaY * sigmaY));
                    int idx = y * width + x;
                    frame[idx] = (ushort)Math.Min(ushort.MaxValue, frame[idx] + v);
                }
            }
        }

        private static ushort[] FlatField(int width, int height) {
            var f = new ushort[width * height];
            Array.Fill(f, Background);
            return f;
        }

        private static StarDetectionParams NormalParams() => new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0 };

        private static DetectedStar DetectOne(ushort[] frame, int w, int h) {
            var result = StarDetector.Detect(frame, w, h, NormalParams());
            Assert.That(result.DetectedStars, Is.EqualTo(1), "expected exactly one detected star");
            return result.StarList[0];
        }

        [Test]
        public void Round_star_has_roundness_near_one() {
            int w = 120, h = 120;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 60, 60, amplitude: 6000, sigmaX: 1.6, sigmaY: 1.6);

            var star = DetectOne(frame, w, h);
            Assert.That(star.Roundness, Is.GreaterThan(0.85).And.LessThanOrEqualTo(1.0));
        }

        [Test]
        public void Elongated_star_is_less_round_than_a_circular_one() {
            int w = 120, h = 120;
            var round = FlatField(w, h);
            AddStar(round, w, h, 60, 60, amplitude: 6000, sigmaX: 1.8, sigmaY: 1.8);
            var elongated = FlatField(w, h);
            AddStar(elongated, w, h, 60, 60, amplitude: 6000, sigmaX: 1.3, sigmaY: 3.2);

            double roundR = DetectOne(round, w, h).Roundness;
            double elongatedR = DetectOne(elongated, w, h).Roundness;

            Assert.That(elongatedR, Is.LessThan(0.75));
            Assert.That(roundR, Is.GreaterThan(elongatedR + 0.15));
        }

        [Test]
        public void Wider_star_has_a_larger_fwhm() {
            int w = 120, h = 120;
            var narrow = FlatField(w, h);
            AddStar(narrow, w, h, 60, 60, amplitude: 6000, sigmaX: 1.2, sigmaY: 1.2);
            var wide = FlatField(w, h);
            AddStar(wide, w, h, 60, 60, amplitude: 6000, sigmaX: 2.6, sigmaY: 2.6);

            double narrowFwhm = DetectOne(narrow, w, h).FWHM;
            double wideFwhm = DetectOne(wide, w, h).FWHM;

            Assert.That(narrowFwhm, Is.GreaterThan(0));
            Assert.That(wideFwhm, Is.GreaterThan(narrowFwhm));
        }

        [Test]
        public void Brighter_star_has_a_higher_peak_to_background() {
            int w = 120, h = 120;
            var bright = FlatField(w, h);
            AddStar(bright, w, h, 60, 60, amplitude: 12000, sigmaX: 1.8, sigmaY: 1.8);
            var dim = FlatField(w, h);
            AddStar(dim, w, h, 60, 60, amplitude: 3000, sigmaX: 1.8, sigmaY: 1.8);

            double brightP = DetectOne(bright, w, h).PeakToBackground;
            double dimP = DetectOne(dim, w, h).PeakToBackground;

            Assert.That(dimP, Is.GreaterThan(0));
            Assert.That(brightP, Is.GreaterThan(dimP));
            // (peak − bg)/bg for the bright star ≈ 12000/1000 ≈ 12; assert a generous band.
            Assert.That(brightP, Is.GreaterThan(5.0).And.LessThan(20.0));
        }

        [Test]
        public void Peak_to_background_stays_bounded_on_a_near_zero_background_frame() {
            // A dark/bias-free frame has a ~0 median background; the 1-ADU denominator floor must keep
            // PeakToBackground a finite, peak-scale value rather than dividing by zero or NaN-ing — the
            // §59.3 median must never ingest an infinity from an all-dark calibration frame.
            int w = 120, h = 120;
            var frame = new ushort[w * h]; // background 0, no Array.Fill
            AddStar(frame, w, h, 60, 60, amplitude: 6000, sigmaX: 1.8, sigmaY: 1.8);

            var star = DetectOne(frame, w, h);
            Assert.That(double.IsFinite(star.PeakToBackground), Is.True);
            Assert.That(star.PeakToBackground, Is.GreaterThan(0));
            // With background ≈ 0 the ratio degrades to (peak − bg)/1 ≈ the raw peak — bounded, not exploded.
            Assert.That(star.PeakToBackground, Is.LessThan(ushort.MaxValue));
        }

        [Test]
        public void Extract_on_a_detected_frame_yields_positive_medians() {
            int w = 160, h = 160;
            var frame = FlatField(w, h);
            (int x, int y)[] centers = { (40, 40), (120, 40), (80, 80), (40, 120), (120, 120) };
            foreach (var (cx, cy) in centers) {
                AddStar(frame, w, h, cx, cy, amplitude: 6000, sigmaX: 1.7, sigmaY: 1.7);
            }

            var vector = FocusFeatureExtractor.Extract(StarDetector.Detect(frame, w, h, NormalParams()));

            Assert.That(vector.StarCount, Is.EqualTo(centers.Length));
            Assert.That(vector.MedianHFR, Is.GreaterThan(0));
            Assert.That(vector.MedianFWHM, Is.GreaterThan(0));
            Assert.That(vector.MedianRoundness, Is.GreaterThan(0.8).And.LessThanOrEqualTo(1.0));
            Assert.That(vector.MedianPeakToBackground, Is.GreaterThan(0));
        }

        [Test]
        public void Extract_on_an_empty_result_returns_the_empty_vector() {
            var vector = FocusFeatureExtractor.Extract(new StarDetectionResult { StarList = new List<DetectedStar>() });
            Assert.That(vector, Is.EqualTo(FocusFeatureVector.Empty));
            Assert.That(vector.StarCount, Is.EqualTo(0));
            Assert.That(vector.MedianHFR, Is.EqualTo(0));
            Assert.That(vector.MedianFWHM, Is.EqualTo(0));
            Assert.That(vector.MedianRoundness, Is.EqualTo(0));
            Assert.That(vector.MedianPeakToBackground, Is.EqualTo(0));
            Assert.That(vector.MedianDonutOuterDiameter, Is.EqualTo(0));
            Assert.That(vector.MedianDonutInnerDiameter, Is.EqualTo(0));
            Assert.That(vector.MedianRingThickness, Is.EqualTo(0));
        }

        [Test]
        public void Extract_null_result_throws() {
            Assert.Throws<ArgumentNullException>(() => FocusFeatureExtractor.Extract(null!));
        }

        [Test]
        public void Extract_uses_the_median_of_an_odd_count() {
            var stars = new List<DetectedStar> {
                Star(hfr: 2.0, fwhm: 4.0, roundness: 0.90, peak: 6.0, outerDiameter: 10.0, innerDiameter: 4.0), // ring 6
                Star(hfr: 3.0, fwhm: 5.0, roundness: 0.80, peak: 8.0, outerDiameter: 14.0, innerDiameter: 6.0), // ring 8
                Star(hfr: 9.0, fwhm: 9.0, roundness: 0.20, peak: 2.0, outerDiameter: 30.0, innerDiameter: 2.0), // ring 28, outlier
            };
            var vector = FocusFeatureExtractor.Extract(new StarDetectionResult { StarList = stars });

            Assert.That(vector.StarCount, Is.EqualTo(3));
            // Median picks the middle order statistic per field, shrugging off the outlier.
            Assert.That(vector.MedianHFR, Is.EqualTo(3.0));
            Assert.That(vector.MedianFWHM, Is.EqualTo(5.0));
            Assert.That(vector.MedianRoundness, Is.EqualTo(0.80));
            Assert.That(vector.MedianPeakToBackground, Is.EqualTo(6.0));
            Assert.That(vector.MedianDonutOuterDiameter, Is.EqualTo(14.0)); // median{10,14,30}
            Assert.That(vector.MedianDonutInnerDiameter, Is.EqualTo(4.0));  // median{2,4,6}
            // Ring thickness is the median of per-star (outer − inner) = median{6,8,28} = 8, which is NOT
            // MedianDonutOuterDiameter − MedianDonutInnerDiameter (14 − 4 = 10): the median is non-linear.
            Assert.That(vector.MedianRingThickness, Is.EqualTo(8.0));
            Assert.That(vector.MedianRingThickness,
                Is.Not.EqualTo(vector.MedianDonutOuterDiameter - vector.MedianDonutInnerDiameter));
        }

        [Test]
        public void Extract_averages_the_two_central_values_on_an_even_count() {
            var stars = new List<DetectedStar> {
                Star(hfr: 2.0, fwhm: 4.0, roundness: 0.95, peak: 10.0),
                Star(hfr: 4.0, fwhm: 6.0, roundness: 0.85, peak: 6.0),
            };
            var vector = FocusFeatureExtractor.Extract(new StarDetectionResult { StarList = stars });

            Assert.That(vector.StarCount, Is.EqualTo(2));
            Assert.That(vector.MedianHFR, Is.EqualTo(3.0).Within(1e-9));       // (2+4)/2
            Assert.That(vector.MedianFWHM, Is.EqualTo(5.0).Within(1e-9));      // (4+6)/2
            Assert.That(vector.MedianRoundness, Is.EqualTo(0.90).Within(1e-9)); // (0.95+0.85)/2
            Assert.That(vector.MedianPeakToBackground, Is.EqualTo(8.0).Within(1e-9)); // (10+6)/2
        }

        private static DetectedStar Star(double hfr, double fwhm, double roundness, double peak,
                double outerDiameter = 0, double innerDiameter = 0) =>
            new DetectedStar {
                HFR = hfr, FWHM = fwhm, Roundness = roundness, PeakToBackground = peak,
                DonutOuterDiameter = outerDiameter, DonutInnerDiameter = innerDiameter,
            };

        // Stamp a uniform-brightness annulus (a defocused obstructed-scope "donut"): a filled ring between
        // innerRadius and outerRadius, its centre left at background so the detector's blob is the ring alone.
        private static void AddDonut(ushort[] frame, int width, int height, int cx, int cy,
                double amplitude, double innerRadius, double outerRadius) {
            int r = (int)Math.Ceiling(outerRadius);
            for (int dy = -r; dy <= r; dy++) {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < innerRadius || dist > outerRadius) continue;
                    int idx = y * width + x;
                    frame[idx] = (ushort)Math.Min(ushort.MaxValue, frame[idx] + amplitude);
                }
            }
        }

        [Test]
        public void Filled_star_has_a_zero_inner_diameter_and_a_positive_outer() {
            int w = 120, h = 120;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 60, 60, amplitude: 6000, sigmaX: 1.8, sigmaY: 1.8);

            var star = DetectOne(frame, w, h);
            // A refractor / in-focus star peaks at the centre — no dark hole, so the inner edge is bin 0.
            Assert.That(star.DonutInnerDiameter, Is.EqualTo(0));
            Assert.That(star.DonutOuterDiameter, Is.GreaterThan(0));
            Assert.That(star.RingThickness, Is.EqualTo(star.DonutOuterDiameter));
        }

        [Test]
        public void Defocused_donut_has_a_nonzero_inner_diameter_inside_its_outer() {
            int w = 140, h = 140;
            var frame = FlatField(w, h);
            AddDonut(frame, w, h, 70, 70, amplitude: 6000, innerRadius: 4, outerRadius: 9);

            var star = DetectOne(frame, w, h);
            // The obstruction shadow gives a real inner hole; the outer edge sits beyond it.
            Assert.That(star.DonutInnerDiameter, Is.GreaterThan(0));
            Assert.That(star.DonutOuterDiameter, Is.GreaterThan(star.DonutInnerDiameter));
            Assert.That(star.RingThickness, Is.GreaterThan(0));
            // Inner diameter tracks the ~r=4 inner rim, outer the ~r=9 rim (2·bin-radius, pixel scale).
            Assert.That(star.DonutInnerDiameter, Is.GreaterThan(4).And.LessThan(12));
            Assert.That(star.DonutOuterDiameter, Is.GreaterThan(12).And.LessThan(22));
        }

        [Test]
        public void A_wider_donut_has_a_larger_outer_diameter() {
            int w = 160, h = 160;
            var tight = FlatField(w, h);
            AddDonut(tight, w, h, 80, 80, amplitude: 6000, innerRadius: 3, outerRadius: 6);
            var wide = FlatField(w, h);
            AddDonut(wide, w, h, 80, 80, amplitude: 6000, innerRadius: 5, outerRadius: 11);

            Assert.That(DetectOne(wide, w, h).DonutOuterDiameter,
                Is.GreaterThan(DetectOne(tight, w, h).DonutOuterDiameter));
        }

        [Test]
        public void Extract_on_a_donut_frame_yields_positive_median_donut_geometry() {
            int w = 200, h = 200;
            var frame = FlatField(w, h);
            (int x, int y)[] centers = { (50, 50), (150, 50), (100, 100), (50, 150), (150, 150) };
            foreach (var (cx, cy) in centers) {
                AddDonut(frame, w, h, cx, cy, amplitude: 6000, innerRadius: 4, outerRadius: 9);
            }

            var vector = FocusFeatureExtractor.Extract(StarDetector.Detect(frame, w, h, NormalParams()));

            Assert.That(vector.StarCount, Is.EqualTo(centers.Length));
            Assert.That(vector.MedianDonutInnerDiameter, Is.GreaterThan(0));
            Assert.That(vector.MedianDonutOuterDiameter, Is.GreaterThan(vector.MedianDonutInnerDiameter));
            Assert.That(vector.MedianRingThickness, Is.GreaterThan(0));
        }
    }
}
