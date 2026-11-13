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
using OpenAstroAra.Stretch;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1787: a synthetic one-shot-colour star field. The scene is built at full resolution per channel
    /// (R x1.0, G x0.8, B x0.6 of one shared star list), moved by the given shift and rotation about the frame centre,
    /// and only then sampled through an RGGB mosaic, so the CFA phase stays fixed on the sensor as it does in the sky.</summary>
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Seeded System.Random makes the synthetic star field deterministic; test-only.")]
    internal static class OscScene {
        internal const int W = 512, H = 512;
        internal const double GainR = 1.0, GainG = 0.8, GainB = 0.6;
        internal const double Sky = 1000;

        /// <param name="trailRow">A full-width bright line on this full-resolution row (a satellite trail), or -1.</param>
        internal static ushort[] Mosaic(int noiseSeed, double shiftX = 0, double shiftY = 0, double rotDeg = 0, int trailRow = -1) {
            var stars = new Random(5);
            var rng = new Random(noiseSeed);
            var scene = new double[W * H];
            for (int i = 0; i < scene.Length; i++) { scene[i] = 1; }
            double c = Math.Cos(rotDeg * Math.PI / 180), s = Math.Sin(rotDeg * Math.PI / 180), cx = W / 2.0, cy = H / 2.0;
            for (int n = 0; n < 24; n++) {
                double px = 80 + stars.NextDouble() * (W - 160), py = 80 + stars.NextDouble() * (H - 160), amp = 4000 + stars.NextDouble() * 15000;
                double qx = c * (px - cx) - s * (py - cy) + cx + shiftX, qy = s * (px - cx) + c * (py - cy) + cy + shiftY;
                for (int y = (int)qy - 9; y <= (int)qy + 9; y++) {
                    for (int x = (int)qx - 9; x <= (int)qx + 9; x++) {
                        if (x < 0 || y < 0 || x >= W || y >= H) { continue; }
                        scene[y * W + x] += amp / Sky * Math.Exp(-((x - qx) * (x - qx) + (y - qy) * (y - qy)) / (2 * 3.2 * 3.2));
                    }
                }
            }
            var o = new ushort[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    double gain = (y & 1, x & 1) switch { (0, 0) => GainR, (1, 1) => GainB, _ => GainG }; // RGGB
                    double v = Sky * gain * scene[y * W + x] + (rng.NextDouble() - 0.5) * 60;
                    if (y == trailRow) { v += 20000 * gain; }
                    o[y * W + x] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
                }
            }
            return o;
        }

        /// <summary>The mono equivalent (the green plane, 256x256 grid) for the guard that the mono path is unchanged.</summary>
        internal static ushort[] Mono(int noiseSeed, double rotDeg) {
            var mosaic = Mosaic(noiseSeed, 0, 0, rotDeg);
            var (_, g, _, _, _) = Debayer.SuperPixel(mosaic, W, H, BayerPattern.RGGB);
            return g;
        }
    }

    /// <summary>↑ara#1787: the engine's colour mode and its 180-degree fallback (the Session Live Stack).</summary>
    [TestFixture]
    public class LiveStackEngineColourTest {

        private const int GW = OscScene.W / 2, GH = OscScene.H / 2;

        private static double Median(float[] plane, int rowFrom, int rowTo) {
            var v = plane.Skip(rowFrom * GW).Take((rowTo - rowFrom) * GW).OrderBy(x => x).ToArray();
            return v[v.Length / 2];
        }

        // Falsified by: stacking only the luminance (or one channel) leaves G and B at the R level; resampling the
        // channels with their own transform instead of the luminance one smears the stars in G and B.
        [Test]
        public void Colour_mode_stacks_shifted_rotated_frames_and_keeps_each_channel_level() {
            var e = new LiveStackEngine();
            Assert.That(e.AddColour(OscScene.Mosaic(1), OscScene.W, OscScene.H, BayerPattern.RGGB).Outcome, Is.EqualTo(StackAddOutcome.Reference));
            for (int i = 2; i <= 8; i++) {
                var r = e.AddColour(OscScene.Mosaic(i, shiftX: i * 0.8, shiftY: -i * 0.5, rotDeg: i * 0.15), OscScene.W, OscScene.H, BayerPattern.RGGB);
                Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Stacked), $"frame {i}");
            }
            var planes = e.SnapshotColourMean(out int w, out int h)!;
            Assert.Multiple(() => {
                Assert.That((w, h), Is.EqualTo((GW, GH)), "the superpixel grid, half the sensor each way");
                Assert.That(e.FramesStacked, Is.EqualTo(8));
                Assert.That(Median(planes[0], 20, 40), Is.EqualTo(OscScene.Sky * OscScene.GainR).Within(20));
                Assert.That(Median(planes[1], 20, 40), Is.EqualTo(OscScene.Sky * OscScene.GainG).Within(20));
                Assert.That(Median(planes[2], 20, 40), Is.EqualTo(OscScene.Sky * OscScene.GainB).Within(20));
            });
        }

        // Falsified by: a colour clip that runs on the cell average only (not per channel) lets a trail through in the
        // channel whose own variance is wide; removing the clip leaves the trail row at 20000 x gain / 9.
        [Test]
        public void A_trail_in_one_frame_is_clipped_in_every_channel() {
            var e = new LiveStackEngine();
            for (int i = 1; i <= 9; i++) { e.AddColour(OscScene.Mosaic(i), OscScene.W, OscScene.H, BayerPattern.RGGB); }
            var trailed = e.AddColour(OscScene.Mosaic(10, trailRow: 300), OscScene.W, OscScene.H, BayerPattern.RGGB);
            var planes = e.SnapshotColourMean(out _, out _)!;
            Assert.Multiple(() => {
                Assert.That(trailed.Outcome, Is.EqualTo(StackAddOutcome.Stacked));
                Assert.That(e.RejectedSamples, Is.GreaterThan(GW), "a whole row of superpixels clipped");
                Assert.That(Median(planes[0], 150, 151), Is.EqualTo(OscScene.Sky * OscScene.GainR).Within(60));
                Assert.That(Median(planes[1], 150, 151), Is.EqualTo(OscScene.Sky * OscScene.GainG).Within(60));
                Assert.That(Median(planes[2], 150, 151), Is.EqualTo(OscScene.Sky * OscScene.GainB).Within(60));
            });
        }

        // Falsified by: dropping the 180-degree retry makes this frame Unregistered (the rotation vote only reaches 10 degrees).
        [Test]
        public void A_frame_rotated_180_degrees_registers_as_flipped_and_is_added_sharp() {
            var e = new LiveStackEngine();
            e.AddColour(OscScene.Mosaic(1), OscScene.W, OscScene.H, BayerPattern.RGGB);
            e.AddColour(OscScene.Mosaic(2, shiftX: 0.6), OscScene.W, OscScene.H, BayerPattern.RGGB);
            float refPeak = e.SnapshotColourMean(out _, out _)![1].Max();

            var r = e.AddColour(OscScene.Mosaic(3, shiftX: 1.2, rotDeg: 180), OscScene.W, OscScene.H, BayerPattern.RGGB);
            float peak = e.SnapshotColourMean(out _, out _)![1].Max();
            Assert.Multiple(() => {
                Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Flipped));
                Assert.That(e.FramesStacked, Is.EqualTo(3));
                Assert.That(e.FramesUnregistered, Is.EqualTo(0));
                Assert.That(Math.Abs(r.RotationDeg), Is.EqualTo(180).Within(1));
                Assert.That(peak, Is.GreaterThan(refPeak * 0.8f), "a mis-registered half would blur the star peaks");
            });
        }

        // Falsified by: applying the 180-degree retry to the mono path changes Live View stacking (a flipped frame
        // would register there); the mono path keeps counting it as unregistered.
        [Test]
        public void The_mono_path_still_counts_a_180_degree_frame_as_unregistered() {
            var e = new LiveStackEngine();
            e.Add(OscScene.Mono(1, 0), GW, GH);
            e.Add(OscScene.Mono(2, 0), GW, GH);
            var r = e.Add(OscScene.Mono(3, 180), GW, GH);
            Assert.Multiple(() => {
                Assert.That(r.Outcome, Is.EqualTo(StackAddOutcome.Unregistered));
                Assert.That(e.FramesUnregistered, Is.EqualTo(1));
            });
        }

        // Falsified by: counting the full sensor (not the superpixel grid) against the cap refuses a 26 MP sensor
        // (6.5 MP grid) that the contract admits; counting nothing admits a 100 MP one.
        [Test]
        public void The_pixel_cap_counts_the_superpixel_grid() {
            var e = new LiveStackEngine(maxPixels: GW * GH); // exactly the grid of the 512x512 sensor
            Assert.That(e.AddColour(OscScene.Mosaic(1), OscScene.W, OscScene.H, BayerPattern.RGGB).Outcome, Is.EqualTo(StackAddOutcome.Reference));
            var small = new LiveStackEngine(maxPixels: GW * GH - 1);
            Assert.That(small.AddColour(OscScene.Mosaic(1), OscScene.W, OscScene.H, BayerPattern.RGGB).Outcome, Is.EqualTo(StackAddOutcome.TooLarge));
        }
    }
}