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
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>#1299 — the headless Bahtinov analysis against the synthetic mask star: zero at best focus,
    /// the sign flips through it, linear with defocus, and no pattern without a mask.</summary>
    [TestFixture]
    public class BahtinovAnalyzerTest {

        private const int W = 800;
        private const int H = 600;

        private static BahtinovMeasurement Measure(double offset, double blur = 1.5, double rotation = 23, int seed = 1,
                (double X, double Y)? reference = null, int starX = 430, int starY = 310) {
            var pixels = SyntheticSky.RenderBahtinov(W, H, starX, starY, offset, blur, rotationDeg: rotation, frameSeed: seed);
            var m = BahtinovAnalyzer.Analyze(pixels, W, H, out var problem, referenceNormal: reference);
            Assert.That(problem, Is.EqualTo(BahtinovProblem.None), $"offset {offset}, rotation {rotation}");
            return m!;
        }

        [Test]
        public void Offset_is_zero_at_best_focus() {
            var m = Measure(0);
            Assert.That(m.OffsetPx, Is.EqualTo(0).Within(0.1));
            Assert.That(m.StarX, Is.EqualTo(430).Within(1));
            Assert.That(m.StarY, Is.EqualTo(310).Within(1));
            Assert.That(m.SpreadDeg, Is.EqualTo(30).Within(0.5));
            Assert.That(m.Central.AngleDeg, Is.EqualTo(23).Within(0.3));
        }

        [TestCase(-6.0)]
        [TestCase(-3.0)]
        [TestCase(-1.0)]
        [TestCase(-0.4)]
        [TestCase(0.4)]
        [TestCase(1.0)]
        [TestCase(3.0)]
        [TestCase(6.0)]
        public void Offset_tracks_the_rendered_offset_with_its_sign(double offset) {
            var blur = 1.5 + Math.Abs(offset) / 3;
            var m = Measure(offset, blur);
            Assert.That(m.OffsetPx, Is.EqualTo(offset).Within(0.15 + 0.03 * Math.Abs(offset)));
        }

        [Test]
        public void Offset_is_linear_in_defocus() {
            var xs = Enumerable.Range(-8, 17).Select(i => i * 0.5).ToArray();
            var ys = xs.Select(x => Measure(x, 1.5 + Math.Abs(x) / 3, seed: (int)(x * 10) + 100).OffsetPx).ToArray();
            var mx = xs.Average();
            var my = ys.Average();
            var slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / xs.Sum(x => (x - mx) * (x - mx));
            var residual = xs.Zip(ys, (x, y) => Math.Abs(y - (my + slope * (x - mx)))).Max();
            Assert.That(slope, Is.EqualTo(1).Within(0.05));
            Assert.That(residual, Is.LessThan(0.25));
        }

        [TestCase(0.3)]
        [TestCase(179.6)]
        [TestCase(90)]
        [TestCase(137)]
        public void The_sign_holds_at_any_rotation_once_a_reference_normal_is_set(double rotation) {
            var first = Measure(2, rotation: rotation);
            var reference = (first.NormalX, first.NormalY);
            // The central spike's angle wobbles across 0°/180° frame to frame; the sign must not.
            foreach (var wobble in new[] { -0.6, 0.6 }) {
                var m = Measure(2, rotation: rotation + wobble, reference: reference);
                Assert.That(Math.Sign(m.OffsetPx), Is.EqualTo(Math.Sign(first.OffsetPx)), $"wobble {wobble}");
                Assert.That(Math.Abs(m.OffsetPx), Is.EqualTo(2).Within(0.2));
            }
        }

        [Test]
        public void A_star_without_a_mask_has_no_pattern() {
            var pixels = SyntheticSky.Render(W, H, 1.6, stars: 60);
            Assert.That(BahtinovAnalyzer.Analyze(pixels, W, H, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo(BahtinovProblem.NoPattern));
        }

        [Test]
        public void An_empty_frame_has_no_star() {
            var pixels = Enumerable.Repeat((ushort)1200, W * H).ToArray();
            Assert.That(BahtinovAnalyzer.Analyze(pixels, W, H, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo(BahtinovProblem.NoStar));
        }

        [Test]
        public void A_star_at_the_edge_is_refused() {
            var pixels = SyntheticSky.RenderBahtinov(W, H, 10, 300, 1, 1.5, fieldStars: false);
            Assert.That(BahtinovAnalyzer.Analyze(pixels, W, H, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo(BahtinovProblem.NearEdge));
        }

        [Test]
        public void The_star_lock_ignores_a_brighter_star_outside_the_window() {
            var pixels = SyntheticSky.RenderBahtinov(W, H, 430, 310, 1, 1.5, fieldStars: false);
            // A brighter, larger blob far away.
            for (var y = 80; y < 100; y++) {
                for (var x = 80; x < 100; x++) {
                    pixels[y * W + x] = ushort.MaxValue;
                }
            }
            Assert.That(BahtinovAnalyzer.FindBrightStar(pixels, W, H), Is.EqualTo((89, 89)).Or.EqualTo((90, 90)));
            var near = BahtinovAnalyzer.FindBrightStar(pixels, W, H, near: (425, 305));
            Assert.That(near, Is.Not.Null);
            Assert.That(near!.Value.X, Is.EqualTo(430).Within(1));
        }
    }
}
