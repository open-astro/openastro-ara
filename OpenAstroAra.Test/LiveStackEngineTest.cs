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
using OpenAstroAra.Server.Services.LiveStack;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// RED tests for the ↑ara#1169 live-stack engine (slice 1). They name the planned API in
    /// <c>OpenAstroAra.Server.Services.LiveStack</c> and fail to compile on current code (the types do not exist).
    /// Convention under test: a frame's stars sit at <c>R(rot)·(p − centre) + centre + (dx, dy)</c> relative to the
    /// reference; the engine reports that (dx, dy, rot) in <see cref="StackAddResult"/>.
    /// </summary>
    [TestFixture]
    public class LiveStackEngineTest {

        private const int W = 512, H = 512;
        private const double Bg = 1000, NoiseSigma = 30;

        // Deterministic star field kept ≥ 80 px from every edge so a ±6 px dither never clips a star.
        private static readonly List<(double X, double Y, double Amp)> Stars = MakeStars(60, seed: 7);

        private static List<(double, double, double)> MakeStars(int n, int seed) {
            var r = new Random(seed);
            var l = new List<(double, double, double)>();
            for (int i = 0; i < n; i++) {
                l.Add((80 + r.NextDouble() * (W - 160), 80 + r.NextDouble() * (H - 160), 2500 + r.NextDouble() * 20000));
            }
            return l;
        }

        private static ushort[] Render(List<(double X, double Y, double Amp)> stars, double dx, double dy, double rotDeg, int noiseSeed) {
            var img = new double[W * H];
            var rng = new Random(noiseSeed);
            for (int i = 0; i < img.Length; i++) {
                double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
                img[i] = Bg + NoiseSigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }
            double c = Math.Cos(rotDeg * Math.PI / 180), s = Math.Sin(rotDeg * Math.PI / 180);
            double cx = W / 2.0, cy = H / 2.0;
            foreach (var (x, y, amp) in stars) {
                double px = c * (x - cx) - s * (y - cy) + cx + dx;
                double py = s * (x - cx) + c * (y - cy) + cy + dy;
                for (int yy = (int)py - 9; yy <= (int)py + 9; yy++) {
                    for (int xx = (int)px - 9; xx <= (int)px + 9; xx++) {
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        double d2 = (xx - px) * (xx - px) + (yy - py) * (yy - py);
                        img[yy * W + xx] += amp * Math.Exp(-d2 / (2 * 1.8 * 1.8));
                    }
                }
            }
            var o = new ushort[W * H];
            for (int i = 0; i < o.Length; i++) o[i] = (ushort)Math.Clamp(Math.Round(img[i]), 0, 65535);
            return o;
        }

        private static double StdDev(float[] a, int x0, int y0, int size) {
            double sum = 0, sum2 = 0; int n = 0;
            for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) { double v = a[y * W + x]; sum += v; sum2 += v * v; n++; }
            double m = sum / n; return Math.Sqrt(sum2 / n - m * m);
        }

        // Falsified by: removing the registration step (stacking unshifted) leaves OffsetX/OffsetY at 0, so the
        // 7.3 / −4.6 px assertions fail; a sign flip in the reported offset fails the same way.
        [Test]
        public void Add_recovers_a_known_translation_within_a_third_of_a_pixel() {
            var e = new LiveStackEngine();
            Assert.That(e.Add(Render(Stars, 0, 0, 0, 1), W, H).Outcome, Is.EqualTo(StackAddOutcome.Reference));
            var r = e.Add(Render(Stars, 7.3, -4.6, 0, 2), W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
            Assert.That(r.OffsetX, Is.EqualTo(7.3).Within(0.3));
            Assert.That(r.OffsetY, Is.EqualTo(-4.6).Within(0.3));
            Assert.That(r.RotationDeg, Is.EqualTo(0).Within(0.05));
        }

        // Falsified by: a translation-only model reports RotationDeg 0 and leaves star residuals of several px at
        // the field edge, so the rotation assertion fails (field rotation is real on an alt-az mount).
        [Test]
        public void Add_recovers_a_known_rotation_plus_translation() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            var r = e.Add(Render(Stars, -3.5, 5.2, 1.5, 2), W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
            Assert.That(r.RotationDeg, Is.EqualTo(1.5).Within(0.05));
            Assert.That(r.OffsetX, Is.EqualTo(-3.5).Within(0.5));
            Assert.That(r.OffsetY, Is.EqualTo(5.2).Within(0.5));
        }

        // Falsified by: replacing the running mean with "last frame wins" leaves the stacked background noise at
        // the single-frame level (ratio ≈ 1), failing the ≥ 3.2 bound (sqrt(16) = 4, 20 % allowance for the
        // registration resampling and clipped samples).
        [Test]
        public void Stacking_16_dithered_frames_improves_background_noise_by_about_sqrt_N() {
            var e = new LiveStackEngine();
            var rng = new Random(11);
            for (int i = 0; i < 16; i++) {
                e.Add(Render(Stars, i == 0 ? 0 : (rng.NextDouble() - 0.5) * 10, i == 0 ? 0 : (rng.NextDouble() - 0.5) * 10, 0, 100 + i), W, H);
            }
            Assert.That(e.FramesStacked, Is.EqualTo(16));
            var mean = e.SnapshotMean(out var w, out var h)!;
            Assert.That((w, h), Is.EqualTo((W, H)));
            double single = StdDev(Array.ConvertAll(Render(Stars, 0, 0, 0, 999), v => (float)v), 20, 20, 50);
            double stacked = StdDev(mean, 20, 20, 50);
            Assert.That(single / stacked, Is.GreaterThan(3.2));
        }

        // Falsified by: dropping the sigma clip (plain mean) lets the 60000-ADU streak in frame 9 lift the stacked
        // pixel by ≈ 5900 ADU, failing the 100 ADU bound; clipping during warm-up too would reject real signal.
        [Test]
        public void A_satellite_streak_in_one_post_warm_up_frame_is_clipped_out() {
            var e = new LiveStackEngine(sigma: 3.0, warmUp: 5);
            for (int i = 0; i < 12; i++) {
                var f = Render(Stars, 0, 0, 0, 200 + i);
                if (i == 9) { for (int x = 20; x < 70; x++) f[40 * W + x] = 61000; }
                e.Add(f, W, H);
            }
            var mean = e.SnapshotMean(out _, out _)!;
            Assert.That(mean[40 * W + 45], Is.EqualTo(Bg).Within(100));
            Assert.That(e.RejectedSamples, Is.GreaterThanOrEqualTo(50));
        }

        // Falsified by: stacking every frame regardless of the match leaves FramesStacked at 2 and shifts the mean,
        // failing the Unregistered outcome, the unchanged count and the unchanged mean.
        [Test]
        public void A_frame_that_cannot_be_registered_is_counted_and_never_added() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            var before = e.SnapshotMean(out _, out _)!;
            var other = Render(MakeStars(60, seed: 99), 0, 0, 0, 2);
            var r = e.Add(other, W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Unregistered));
            Assert.That(e.FramesStacked, Is.EqualTo(1));
            Assert.That(e.FramesUnregistered, Is.EqualTo(1));
            Assert.That(e.SnapshotMean(out _, out _)!, Is.EqualTo(before));
        }

        // Falsified by: no consecutive-failure rule leaves the stale reference forever (frames keep being
        // Unregistered, FramesStacked stays 1); the rule re-references after 10 in a row (user slewed).
        [Test]
        public void Ten_consecutive_unregistered_frames_restart_the_stack_on_the_new_field() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            var newField = MakeStars(60, seed: 99);
            StackAddResult last = default!;
            for (int i = 0; i < 11; i++) last = e.Add(Render(newField, 0, 0, 0, 10 + i), W, H);
            Assert.That(last.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
            Assert.That(e.FramesStacked, Is.EqualTo(2));
        }

        // Falsified by: accepting any size would index past the mean buffer or mix geometries; the engine must
        // refuse a frame whose size differs from the reference's.
        [Test]
        public void A_frame_of_a_different_size_is_rejected_and_not_added() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            var r = e.Add(new ushort[256 * 256], 256, 256);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.WrongSize));
            Assert.That(e.FramesStacked, Is.EqualTo(1));
        }

        // Falsified by: no pixel cap lets a 100 MP frame allocate ~1 GB of accumulators on the Pi; the cap makes
        // the engine refuse it up front.
        [Test]
        public void A_frame_over_the_pixel_cap_is_refused_before_any_allocation() {
            var e = new LiveStackEngine(maxPixels: 100_000);
            var r = e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.TooLarge));
            Assert.That(e.SnapshotMean(out _, out _), Is.Null);
        }

        // Falsified by: a Reset that keeps the accumulators leaves FramesStacked > 0 and a non-null mean, and the
        // next frame is registered against the old reference instead of becoming the new one.
        [Test]
        public void Reset_empties_the_stack_and_the_next_frame_becomes_the_reference() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            e.Add(Render(Stars, 2, 2, 0, 2), W, H);
            e.Reset();
            Assert.That(e.FramesStacked, Is.EqualTo(0));
            Assert.That(e.SnapshotMean(out _, out _), Is.Null);
            Assert.That(e.RenderPreview(), Is.Null);
            Assert.That(e.Add(Render(MakeStars(60, seed: 99), 0, 0, 0, 3), W, H).Outcome, Is.EqualTo(StackAddOutcome.Reference));
        }

        // Falsified by: a missing preview path returns null (or non-JPEG bytes) after frames are stacked.
        [Test]
        public void RenderPreview_returns_a_jpeg_once_a_frame_is_stacked() {
            var e = new LiveStackEngine();
            Assert.That(e.RenderPreview(), Is.Null);
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            var jpeg = e.RenderPreview();
            Assert.That(jpeg, Is.Not.Null);
            Assert.That(jpeg![0], Is.EqualTo(0xFF));
            Assert.That(jpeg[1], Is.EqualTo(0xD8));
        }
    }
}
