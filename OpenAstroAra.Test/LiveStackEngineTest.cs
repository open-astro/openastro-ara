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
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// RED tests for the ↑ara#1169 live-stack engine (slice 1). They name the planned API in
    /// <c>OpenAstroAra.Server.Services.LiveStack</c> and fail to compile on current code (the types do not exist).
    /// Convention under test: a frame's stars sit at <c>R(rot)·(p − centre) + centre + (dx, dy)</c> relative to the
    /// reference; the engine reports that (dx, dy, rot) in <see cref="StackAddResult"/>.
    /// A new engine is enabled for session 0; <c>Add</c> returns <see cref="StackAddOutcome.Dropped"/> when the
    /// engine is disabled or the session id differs (the enabled flag and session live under the engine lock).
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

        private static long RunWithOptionalStreak(bool streak) {
            var e = new LiveStackEngine(sigma: 3.0, warmUp: 5);
            for (int i = 0; i < 12; i++) {
                var f = Render(Stars, 0, 0, 0, 200 + i);
                if (streak && i == 9) { for (int x = 20; x < 70; x++) f[40 * W + x] = 61000; }
                e.Add(f, W, H);
            }
            var mean = e.SnapshotMean(out _, out _)!;
            Assert.That(mean[40 * W + 45], Is.EqualTo(Bg).Within(100));
            return e.RejectedSamples;
        }

        // Falsified by: dropping the sigma clip (plain mean) lets the 60000-ADU streak in frame 9 lift the stacked
        // pixel by ≈ 5900 ADU, failing the 100 ADU bound; clipping during warm-up too would reject real signal.
        // The rejected-sample assertion is a difference against the same run without the streak: a 3-sigma clip
        // rejects ≈ 0.27 % of pure noise on its own, so an absolute count would pass even with the streak kept.
        [Test]
        public void A_satellite_streak_in_one_post_warm_up_frame_is_clipped_out() {
            long withStreak = RunWithOptionalStreak(true);
            long without = RunWithOptionalStreak(false);
            Assert.That(withStreak - without, Is.GreaterThanOrEqualTo(40));
        }

        // Falsified by: an engine that measures (dx, dy, rot) right but never resamples (plain mean of unshifted
        // frames) or resamples with the opposite sign leaves the star peaks smeared over ±5 px (star sigma 1.8 px),
        // so the stacked peak falls far below 80 % of the reference peak at the five brightest stars.
        [Test]
        public void Registration_is_applied_to_the_pixels_so_star_peaks_survive_the_stack() {
            var e = new LiveStackEngine();
            var reference = Render(Stars, 0, 0, 0, 1);
            e.Add(reference, W, H);
            var dithers = new (double X, double Y, double Rot)[] {
                (4.5, -3.2, 0), (-5, 2.7, 0), (3.1, 4.8, 1.5), (-2.4, -4.9, 0), (5, 5, 0), (-4.1, 3.3, 0), (0.8, -5, 0), (2.2, 1.7, 0),
            };
            for (int i = 0; i < dithers.Length; i++) {
                var r = e.Add(Render(Stars, dithers[i].X, dithers[i].Y, dithers[i].Rot, 300 + i), W, H);
                Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
            }
            var mean = e.SnapshotMean(out _, out _)!;
            foreach (var (x, y, _) in Stars.OrderByDescending(s => s.Amp).Take(5)) {
                int idx = (int)Math.Round(y) * W + (int)Math.Round(x);
                Assert.That(mean[idx], Is.GreaterThanOrEqualTo(0.8 * reference[idx]), $"star at ({x:F1}, {y:F1})");
            }
        }

        // Falsified by: a resample that applies the rotation backwards (or skips it) moves each star by about 2 x 1.5 deg
        // (or 1.5 deg) of arc at >= 120 px from the centre, so 6.3 px (or 3.1 px) off a 1.8 px sigma star; the two-frame
        // mean then falls to about 50 % (or 60 %) of the reference star signal, below 80 %. One rotated frame only, so
        // unrotated dithers cannot dilute the error as in the test above.
        [Test]
        public void A_single_rotated_frame_is_resampled_with_the_right_rotation_sign() {
            var e = new LiveStackEngine();
            var reference = Render(Stars, 0, 0, 0, 1);
            e.Add(reference, W, H);
            var r = e.Add(Render(Stars, 1.3, -0.7, 1.5, 2), W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
            var mean = e.SnapshotMean(out _, out _)!;
            var far = Stars.Where(s => Math.Sqrt((s.X - W / 2.0) * (s.X - W / 2.0) + (s.Y - H / 2.0) * (s.Y - H / 2.0)) >= 120)
                .OrderByDescending(s => s.Amp).Take(5).ToList();
            Assert.That(far, Has.Count.EqualTo(5));
            foreach (var (x, y, _) in far) {
                int idx = (int)Math.Round(y) * W + (int)Math.Round(x);
                Assert.That(mean[idx] - Bg, Is.GreaterThanOrEqualTo(0.8 * (reference[idx] - Bg)), $"star at ({x:F1}, {y:F1})");
            }
        }

        // Falsified by: keeping the enabled flag outside the engine lock, or not checking it in Add, lets a frame
        // already in flight after Disable (or after a session change) store a new reference: FramesStacked 1 and a
        // preview appear on a stack that was stopped.
        [Test]
        public void Add_after_Disable_is_dropped_and_leaves_the_stack_empty() {
            var e = new LiveStackEngine();
            e.Add(Render(Stars, 0, 0, 0, 1), W, H);
            e.Disable();
            var r = e.Add(Render(Stars, 0, 0, 0, 2), W, H);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Dropped));
            Assert.That(e.FramesStacked, Is.EqualTo(0));
            Assert.That(e.SnapshotMean(out _, out _), Is.Null);
            Assert.That(e.RenderPreview(), Is.Null);
        }

        // Falsified by: ignoring the session id lets a frame from an ended Live View session (id 1) join the stack
        // bound to the new session (id 2).
        [Test]
        public void Add_from_another_session_is_dropped() {
            var e = new LiveStackEngine();
            e.Enable(session: 2);
            var r = e.Add(Render(Stars, 0, 0, 0, 1), W, H, session: 1);
            Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Dropped));
            Assert.That(e.FramesStacked, Is.EqualTo(0));
            Assert.That(e.RenderPreview(), Is.Null);
            Assert.That(e.Add(Render(Stars, 0, 0, 0, 2), W, H, session: 2).Outcome, Is.EqualTo(StackAddOutcome.Reference));
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
