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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.ImageData;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §2105 — the from-scratch <see cref="StarDetector"/> (background median+MAD → 8-connected blobs →
    /// flux-weighted centroid + HFR) and its <see cref="RenderedImage.DetectStars"/> wiring. Synthetic
    /// Gaussian star fields with known counts give deterministic, assertable expectations.
    /// </summary>
    [TestFixture]
    public class StarDetectorTest {

        private const ushort Background = 1000;

        // Stamp a round Gaussian star (peak = Background + amplitude) into a flat-background frame.
        private static void AddStar(ushort[] frame, int width, int height, int cx, int cy, double amplitude, double sigma = 1.5) {
            int r = (int)Math.Ceiling(sigma * 3);
            for (int dy = -r; dy <= r; dy++) {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -r; dx <= r; dx++) {
                    int x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    double v = amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
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

        [Test]
        public void Detect_finds_all_well_separated_stars() {
            int w = 200, h = 200;
            var frame = FlatField(w, h);
            (int x, int y)[] centers = { (40, 40), (160, 40), (100, 100), (40, 160), (160, 160) };
            foreach (var (cx, cy) in centers) {
                AddStar(frame, w, h, cx, cy, amplitude: 5000);
            }

            var result = StarDetector.Detect(frame, w, h, NormalParams());

            Assert.That(result.DetectedStars, Is.EqualTo(centers.Length));
            Assert.That(result.StarList.Count, Is.EqualTo(centers.Length));
        }

        [Test]
        public void Detect_reports_a_physically_reasonable_hfr() {
            int w = 120, h = 120;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 60, 60, amplitude: 6000, sigma: 1.5);

            var result = StarDetector.Detect(frame, w, h, NormalParams());

            Assert.That(result.DetectedStars, Is.EqualTo(1));
            // A σ≈1.5 Gaussian has HFR ≈ 1.18σ ≈ 1.8 — assert a generous band, not an exact value.
            Assert.That(result.AverageHFR, Is.GreaterThan(0.5).And.LessThan(4.0));
        }

        [Test]
        public void Detect_centroid_lands_on_the_star() {
            int w = 100, h = 100;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 50, 30, amplitude: 6000);

            var result = StarDetector.Detect(frame, w, h, NormalParams());

            Assert.That(result.DetectedStars, Is.EqualTo(1));
            // Position is the row-major flat index of the rounded centroid.
            int pos = (int)result.StarList[0].Position;
            int px = pos % w, py = pos / w;
            Assert.That(px, Is.EqualTo(50).Within(1));
            Assert.That(py, Is.EqualTo(30).Within(1));
        }

        [Test]
        public void Detect_returns_zero_on_a_flat_frame() {
            int w = 64, h = 64;
            var result = StarDetector.Detect(FlatField(w, h), w, h, NormalParams());
            Assert.That(result.DetectedStars, Is.EqualTo(0));
            Assert.That(result.AverageHFR, Is.EqualTo(0));
        }

        [Test]
        public void Detect_rejects_an_edge_truncated_star() {
            int w = 100, h = 100;
            var frame = FlatField(w, h);
            // Centred on the top-left corner — its blob touches the frame edge and must be discarded.
            AddStar(frame, w, h, 0, 0, amplitude: 6000);

            var result = StarDetector.Detect(frame, w, h, NormalParams());
            Assert.That(result.DetectedStars, Is.EqualTo(0));
        }

        [Test]
        public void Detect_rejects_a_saturated_star() {
            int w = 100, h = 100;
            var frame = FlatField(w, h);
            // Peak well above the saturation level (65000) → clipped core → rejected.
            AddStar(frame, w, h, 50, 50, amplitude: 70000, sigma: 2.0);

            var result = StarDetector.Detect(frame, w, h, NormalParams());
            Assert.That(result.DetectedStars, Is.EqualTo(0));
        }

        [Test]
        public void Detect_honours_the_max_number_of_stars_cap() {
            int w = 200, h = 200;
            var frame = FlatField(w, h);
            (int x, int y, double amp)[] stars = {
                (40, 40, 8000), (160, 40, 7000), (100, 100, 6000), (40, 160, 5000),
            };
            foreach (var (cx, cy, amp) in stars) {
                AddStar(frame, w, h, cx, cy, amp);
            }

            var result = StarDetector.Detect(frame, w, h,
                new StarDetectionParams { Sensitivity = 8.0, MaxNumberOfStars = 2 });

            Assert.That(result.DetectedStars, Is.EqualTo(2));
            // The cap keeps the brightest — the two highest-amplitude peaks.
            foreach (var s in result.StarList) {
                Assert.That(s.MaxBrightness, Is.GreaterThan(Background + 5500));
            }
        }

        [Test]
        public void Detect_with_noise_reduction_still_finds_a_star_and_suppresses_salt() {
            // Exercises the Median3x3 pre-filter path: a real star (multi-pixel, survives the median)
            // is still found, while scattered single-pixel salt spikes are smoothed below threshold.
            int w = 120, h = 120;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 60, 60, amplitude: 6000, sigma: 2.0);
            // Sprinkle isolated hot pixels the median filter should erase.
            foreach (var (x, y) in new[] { (20, 20), (95, 30), (30, 95), (90, 90) }) {
                frame[y * w + x] = 60000;
            }

            var result = StarDetector.Detect(frame, w, h,
                new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 1 });

            Assert.That(result.DetectedStars, Is.EqualTo(1));
            Assert.That(result.AverageHFR, Is.GreaterThan(0.5).And.LessThan(5.0));
        }

        [Test]
        public void Detect_throws_on_dimension_mismatch() {
            Assert.Throws<ArgumentException>(() => StarDetector.Detect(new ushort[10], 4, 4, NormalParams()));
        }

        [Test]
        public void Detect_rejects_a_frame_spanning_bright_region() {
            // A large connected bright block (a flat/over-exposed region or nebula) is not a star: it
            // exceeds the area cap and must be discarded — and detection must stay bounded, not blow up.
            int w = 200, h = 200;
            var frame = FlatField(w, h);
            for (int y = 40; y < 140; y++) {
                for (int x = 40; x < 140; x++) {
                    frame[y * w + x] = 6000; // 100×100 = 10000-pixel connected component
                }
            }
            var result = StarDetector.Detect(frame, w, h, NormalParams());
            Assert.That(result.DetectedStars, Is.EqualTo(0));
        }

        [Test]
        public void Detect_honours_a_cancelled_token() {
            int w = 64, h = 64;
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(
                () => StarDetector.Detect(FlatField(w, h), w, h, NormalParams(), cts.Token));
        }

        [Test]
        public async System.Threading.Tasks.Task DetectStars_publishes_analysis_onto_the_raw_frame() {
            int w = 120, h = 120;
            var frame = FlatField(w, h);
            AddStar(frame, w, h, 60, 60, amplitude: 6000);
            AddStar(frame, w, h, 30, 90, amplitude: 5000);

            var raw = new BaseImageData(frame, w, h, bitDepth: 16, isBayered: false,
                new ImageMetaData(), null!, null!, null!);
            var rendered = raw.RenderImage();

            var returned = await rendered.DetectStars(annotateImage: false, StarSensitivity.Normal, NoiseReduction.None);

            Assert.That(returned, Is.SameAs(rendered));
            Assert.That(raw.StarDetectionAnalysis.DetectedStars, Is.EqualTo(2));
            Assert.That(raw.StarDetectionAnalysis.HFR, Is.GreaterThan(0));
            Assert.That(raw.StarDetectionAnalysis.StarList.Count, Is.EqualTo(2));
        }
    }
}
