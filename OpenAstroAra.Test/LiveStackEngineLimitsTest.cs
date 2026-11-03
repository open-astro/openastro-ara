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
using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1169: the counter cap and the sigma floor of the live-stack engine (plan R3 and the n = 65535 case).</summary>
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Seeded System.Random makes the synthetic star field deterministic; test-only.")]
    [TestFixture]
    public class LiveStackEngineLimitsTest {

        internal const int W = 256, H = 256;

        internal static ushort[] Field(int noiseSeed) {
            var stars = new Random(5);
            var img = new double[W * H];
            var rng = new Random(noiseSeed);
            for (int i = 0; i < img.Length; i++) { img[i] = 1000 + (rng.NextDouble() - 0.5) * 100; }
            for (int s = 0; s < 20; s++) {
                double px = 40 + stars.NextDouble() * (W - 80), py = 40 + stars.NextDouble() * (H - 80), amp = 4000 + stars.NextDouble() * 15000;
                for (int y = (int)py - 8; y <= (int)py + 8; y++) {
                    for (int x = (int)px - 8; x <= (int)px + 8; x++) {
                        img[y * W + x] += amp * Math.Exp(-((x - px) * (x - px) + (y - py) * (y - py)) / (2 * 1.8 * 1.8));
                    }
                }
            }
            var o = new ushort[W * H];
            for (int i = 0; i < o.Length; i++) { o[i] = (ushort)Math.Clamp(Math.Round(img[i]), 0, 65535); }
            return o;
        }

        // Falsified by: a counter that wraps (ushort overflow to 0) or keeps adding at the cap changes the pixel's mean
        // (a wrapped n = 0 divides the delta by 1 and takes the sample whole); the neighbour pixel must still move.
        [Test]
        public void A_pixel_at_the_sample_cap_keeps_its_mean_while_the_rest_keeps_stacking() {
            var e = new LiveStackEngine();
            Assert.That(e.Add(Field(1), W, H).Outcome, Is.EqualTo(StackAddOutcome.Reference));
            for (int i = 2; i <= 7; i++) { e.Add(Field(i), W, H); } // past warm-up, so the clip has a real variance
            int capped = 10 * W + 10, other = 10 * W + 11;
            e.SetPixelCountForTest(capped, ushort.MaxValue);
            var before = e.SnapshotMean(out _, out _)!;
            for (int i = 8; i <= 10; i++) { Assert.That(e.Add(Field(i), W, H).Outcome, Is.EqualTo(StackAddOutcome.Stacked)); }
            var after = e.SnapshotMean(out _, out _)!;
            Assert.That(after[capped], Is.EqualTo(before[capped]));
            Assert.That(after[other], Is.Not.EqualTo(before[other]));
        }

        // Falsified by: counting n = 1 pixels (m2 = 0) in the median drags the floor to 0 and lets a lucky-agreeing
        // pixel clip everything; the floor must come from the n >= 2 pixels only (here sigma 10), and be 0 when fewer
        // than 100 of the sampled pixels qualify.
        [Test]
        public void Sigma_floor_uses_only_pixels_with_two_samples_and_needs_100_of_them() {
            var m2 = new float[4096]; var n = new ushort[4096];
            Array.Fill(n, (ushort)1);
            for (int i = 0; i < 99; i++) { n[i] = 5; m2[i] = 4 * 100f; } // sigma 10
            Assert.That(LiveStackEngine.SigmaFloor(m2, n), Is.EqualTo(0), "99 qualifying pixels: no floor");
            n[99] = 5; m2[99] = 4 * 100f;
            Assert.That(LiveStackEngine.SigmaFloor(m2, n), Is.EqualTo(10).Within(1e-4), "100 qualifying pixels: median of those only");
        }
    }
}
