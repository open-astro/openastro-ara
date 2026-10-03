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
using OpenAstroAra.Server.Services;
using System;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>The development-only synthetic sky behind OPENASTROARA_SYNTHETIC_SKY: its option parsing, its
    /// V-shaped HFR law, and that the §59 detector reads its rendered stars at roughly the requested HFR.</summary>
    [TestFixture]
    public class SyntheticSkyTest {

        [Test]
        public void Parse_reads_the_three_keys_and_ignores_the_rest() {
            var o = SyntheticSky.Parse("best=12000, hfr=2.5 ,scale=250,junk=1,nope");
            Assert.That(o.BestPosition, Is.EqualTo(12000));
            Assert.That(o.HfrAtFocus, Is.EqualTo(2.5));
            Assert.That(o.StepsPerPixel, Is.EqualTo(250));
            Assert.That(SyntheticSky.Parse(""), Is.EqualTo(new SyntheticSkySettings()));
            Assert.That(SyntheticSky.Parse("1"), Is.EqualTo(new SyntheticSkySettings()));
            Assert.That(SyntheticSky.Parse("hfr=-1").HfrAtFocus, Is.EqualTo(new SyntheticSkySettings().HfrAtFocus), "a non-positive HFR is ignored");
        }

        [Test]
        public void Hfr_is_minimal_at_best_focus_and_grows_with_distance() {
            var o = new SyntheticSkySettings(BestPosition: 10_000, HfrAtFocus: 1.4, StepsPerPixel: 400);
            Assert.That(SyntheticSky.HfrAt(10_000, o), Is.EqualTo(1.4));
            Assert.That(SyntheticSky.HfrAt(10_400, o), Is.EqualTo(Math.Sqrt(1.4 * 1.4 + 1)).Within(1e-9));
            Assert.That(SyntheticSky.HfrAt(9_600, o), Is.EqualTo(SyntheticSky.HfrAt(10_400, o)), "symmetric");
            Assert.That(SyntheticSky.HfrAt(12_000, o), Is.GreaterThan(SyntheticSky.HfrAt(11_000, o)));
        }

        [Test]
        public void Rendered_stars_are_detected_at_roughly_the_requested_hfr() {
            var tight = Detect(SyntheticSky.Render(400, 300, 1.5));
            var blurred = Detect(SyntheticSky.Render(400, 300, 4.0));
            Assert.That(tight.DetectedStars, Is.GreaterThan(10));
            Assert.That(blurred.DetectedStars, Is.GreaterThan(3));
            Assert.That(tight.AverageHFR, Is.EqualTo(1.5).Within(0.6));
            Assert.That(blurred.AverageHFR, Is.GreaterThan(tight.AverageHFR + 1.0), "defocus reads as a wider HFR");
        }

        [Test]
        public void The_same_seed_renders_the_same_field_with_different_noise() {
            var a = SyntheticSky.Render(400, 300, 2.0, frameSeed: 1);
            var b = SyntheticSky.Render(400, 300, 2.0, frameSeed: 2);
            var ra = Detect(a);
            var rb = Detect(b);
            Assert.That(rb.DetectedStars, Is.EqualTo(ra.DetectedStars).Within(6), "same stars, only the noise differs");
            Assert.That(a, Is.Not.EqualTo(b), "the noise differs per frame");
        }

        [Test]
        public void Guide_frames_drift_through_focus_and_back() {
            var g = new SyntheticGuideFrames();
            Assert.That(g.HfrAt(TimeSpan.Zero), Is.EqualTo(1.6).Within(1e-9));
            Assert.That(g.HfrAt(TimeSpan.FromSeconds(45)), Is.EqualTo(3.6).Within(1e-9));
            Assert.That(g.HfrAt(TimeSpan.FromSeconds(90)), Is.EqualTo(1.6).Within(1e-6));
            var (pixels, w, h) = g.Next();
            Assert.That(pixels.Length, Is.EqualTo(w * h));
            Assert.That(Detect(pixels, w, h).DetectedStars, Is.GreaterThan(5));
        }

        private static StarDetectionResult Detect(ushort[] pixels, int w = 400, int h = 300) =>
            StarDetector.Detect(pixels, w, h, new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true }, CancellationToken.None);
    }
}
