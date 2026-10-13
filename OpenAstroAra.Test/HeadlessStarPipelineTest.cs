#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1147 — the §2105 render tail: OSC frames display as luminance, detections can be drawn.</summary>
    [TestFixture]
    public class HeadlessStarPipelineTest {

        private static ushort[] RedOnlyMosaic(int w, int h) {
            // RGGB: red at even row/even column = 60000, every other site 0 — the harshest CFA checkerboard.
            var px = new ushort[w * h];
            for (var y = 0; y < h; y++) {
                for (var x = 0; x < w; x++) {
                    px[y * w + x] = (y % 2 == 0 && x % 2 == 0) ? (ushort)60000 : (ushort)0;
                }
            }
            return px;
        }

        private static double Contrast(byte[] display) {
            // Mean absolute difference between horizontal neighbours: a CFA checkerboard scores high, a smooth field low.
            double sum = 0;
            for (var i = 1; i < display.Length; i++) {
                sum += Math.Abs(display[i] - display[i - 1]);
            }
            return sum / (display.Length - 1);
        }

        [Test]
        public void A_bayered_frame_with_a_known_pattern_renders_as_luminance_not_the_cfa_checkerboard() {
            int w = 64, h = 64;
            var meta = new ImageMetaData();
            meta.Camera.SensorType = SensorType.RGGB;
            var osc = new BaseImageData(RedOnlyMosaic(w, h), w, h, 16, isBayered: true, meta, null!, null!, null!);
            var mono = new BaseImageData(RedOnlyMosaic(w, h), w, h, 16, isBayered: false, new ImageMetaData(), null!, null!, null!);

            var oscDisplay = osc.RenderBitmapSource();
            var monoDisplay = mono.RenderBitmapSource();

            Assert.That(oscDisplay.Length, Is.EqualTo(w * h), "one byte per pixel, full resolution");
            Assert.That(Contrast(oscDisplay), Is.LessThan(Contrast(monoDisplay) / 4),
                "the debayered luminance is smooth where the raw mosaic is a checkerboard");
        }

        [Test]
        public void A_bayered_frame_without_a_usable_pattern_still_renders_the_raw_mosaic() {
            int w = 16, h = 16;
            foreach (var pattern in new[] { SensorType.Monochrome, SensorType.CMYG, SensorType.LRGB }) {
                var meta = new ImageMetaData();
                meta.Camera.SensorType = pattern;
                var osc = new BaseImageData(RedOnlyMosaic(w, h), w, h, 16, isBayered: true, meta, null!, null!, null!);
                var raw = new BaseImageData(RedOnlyMosaic(w, h), w, h, 16, isBayered: false, new ImageMetaData(), null!, null!, null!);
                Assert.That(osc.RenderBitmapSource(), Is.EqualTo(raw.RenderBitmapSource()), $"{pattern}: no guessing RGGB");
            }
        }

        [Test]
        public void The_circle_annotator_draws_a_ring_around_each_star_on_a_copy() {
            int w = 40, h = 30;
            var display = new byte[w * h];
            var result = new StarDetectionResult {
                DetectedStars = 1,
                StarList = new[] { new DetectedStar { Position = 15 * w + 20, HFR = 2.0 } },
            };
            var annotated = CircleStarAnnotator.Annotate(result, display, w, h);
            Assert.That(display.All(b => b == 0), "the source buffer is untouched");
            Assert.That(annotated[15 * w + 20], Is.EqualTo(0), "the centre is not inked");
            // radius = max(3, round(2 × 2.0)) = 4: the four cardinal points are on the ring.
            Assert.That(annotated[15 * w + 24], Is.EqualTo(CircleStarAnnotator.Ink));
            Assert.That(annotated[15 * w + 16], Is.EqualTo(CircleStarAnnotator.Ink));
            Assert.That(annotated[19 * w + 20], Is.EqualTo(CircleStarAnnotator.Ink));
            Assert.That(annotated[11 * w + 20], Is.EqualTo(CircleStarAnnotator.Ink));
            Assert.That(annotated.Count(b => b == CircleStarAnnotator.Ink), Is.InRange(16, 32), "one thin ring");

            // A star at the frame edge clips instead of throwing.
            var edge = new StarDetectionResult { DetectedStars = 1, StarList = new[] { new DetectedStar { Position = 0, HFR = 1.0 } } };
            Assert.That(() => CircleStarAnnotator.Annotate(edge, display, w, h), Throws.Nothing);
            Assert.That(() => CircleStarAnnotator.Annotate(result, new byte[3], w, h), Throws.ArgumentException);
        }

        [Test]
        public async Task DetectStars_with_annotation_draws_onto_Image_and_keeps_OriginalImage() {
            int w = 320, h = 240;
            var pixels = SyntheticSky.Render(w, h, 1.6, stars: 25);
            var raw = new BaseImageData(pixels, w, h, 16, false, new ImageMetaData(), null!,
                HeadlessStarDetection.Instance, CircleStarAnnotator.Instance);
            var rendered = raw.RenderImage();
            var plain = (byte[])rendered.Image.Clone();

            var back = await rendered.DetectStars(annotateImage: true, StarSensitivity.Normal, NoiseReduction.None, cancelToken: CancellationToken.None);

            Assert.That(back, Is.SameAs(rendered));
            Assert.That(raw.StarDetectionAnalysis.DetectedStars, Is.GreaterThan(5), "the detector ran");
            Assert.That(rendered.OriginalImage, Is.EqualTo(plain), "the plain render survives");
            Assert.That(rendered.Image, Is.Not.EqualTo(plain), "the display buffer carries the rings");
            Assert.That(rendered.Image.Count(b => b == CircleStarAnnotator.Ink), Is.GreaterThan(plain.Count(b => b == 255)));

            // Without an annotator the frame stays plain and nothing throws.
            var bare = new BaseImageData(pixels, w, h, 16, false, new ImageMetaData(), null!, null!, null!).RenderImage();
            await bare.DetectStars(annotateImage: true, StarSensitivity.Normal, NoiseReduction.None, cancelToken: CancellationToken.None);
            Assert.That(bare.Image, Is.EqualTo(bare.OriginalImage));
        }

        [Test]
        public async Task The_headless_detection_adapter_matches_the_static_detector() {
            int w = 320, h = 240;
            var pixels = SyntheticSky.Render(w, h, 1.6, stars: 25);
            var rendered = new BaseImageData(pixels, w, h, 16, false, new ImageMetaData(), null!, null!, null!).RenderImage();
            var parameters = new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0 };
            var viaAdapter = await HeadlessStarDetection.Instance.Detect(rendered, "fits", parameters, null!, CancellationToken.None);
            var direct = StarDetector.Detect(pixels, w, h, parameters, CancellationToken.None);
            Assert.That(viaAdapter.DetectedStars, Is.EqualTo(direct.DetectedStars));
            Assert.That(viaAdapter.AverageHFR, Is.EqualTo(direct.AverageHFR).Within(1e-9));
        }
    }
}
