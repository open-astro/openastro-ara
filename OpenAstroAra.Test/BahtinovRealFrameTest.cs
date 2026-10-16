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
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The analyzer on a real mask star, not a synthetic one: a 256×256 crop (little-endian uint16, row
    /// major) from a 2 s RedCat 51 / Uranus-C PRO frame, 2026-10-16. At 2.4"/px the spikes are SHORT — strong
    /// inside r≈30 px (200–350 ADU over a 3232 ADU sky), below the 24 ADU noise per pixel beyond r≈40 — and
    /// the three lie at ≈13°, 153° and 172° (central 172°). The first analyzer, tuned on long synthetic
    /// spikes, masked the core out to radius/3 and kept only pixels 2σ over their ring, so it found no
    /// pattern in this frame all night.
    /// </summary>
    [TestFixture]
    public class BahtinovRealFrameTest {

        private const int Size = 256;

        private static ushort[] LoadCrop() {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "bahtinov-redcat51-2s-256.u16");
            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.EqualTo(Size * Size * 2), path);
            var px = new ushort[Size * Size];
            Buffer.BlockCopy(bytes, 0, px, 0, bytes.Length);
            return px;
        }

        [Test]
        public void The_spike_stretch_lifts_the_arms_into_the_visible_greys() {
            var px = LoadCrop();
            var gray = OpenAstroAra.Server.Services.BahtinovFocusService.StretchSpikes(px);
            // Along the central spike (172°) at r = 20…60 px the stretched arm is well above the sky, and
            // brighter than the same radii on an empty direction (90°).
            double Mean(double angleDeg) {
                double sum = 0;
                var n = 0;
                var t = angleDeg * Math.PI / 180;
                for (var r = 20; r <= 60; r++) {
                    foreach (var sign in new[] { 1, -1 }) {
                        var x = (int)Math.Round(128 + sign * r * Math.Cos(t));
                        var y = (int)Math.Round(128 + sign * r * Math.Sin(t));
                        sum += gray[y * Size + x];
                        n++;
                    }
                }
                return sum / n;
            }
            var arm = Mean(172);
            var sky = Mean(90);
            Assert.That(arm, Is.GreaterThan(40), "the arm is visible, not black");
            Assert.That(arm, Is.GreaterThan(sky * 2), $"arm {arm:F0} vs sky {sky:F0}");
            Assert.That(gray[128 * Size + 128], Is.EqualTo(255), "the core clips white");
        }

        /// <summary>Two Gaussian stars added to the real crop, 60 px above and 95 px below the mask star
        /// (what the picked star's neighbourhood looked like later that night, when every detection was lost). Both
        /// fainter than the mask star, so the finder still picks it.</summary>
        private static ushort[] WithNeighbours(ushort[] px) {
            var copy = (ushort[])px.Clone();
            foreach (var (sx, sy, peak, sigma) in new[] { (128.0, 68.0, 3000.0, 1.4), (140.0, 223.0, 4000.0, 1.5) }) {
                for (var y = 0; y < Size; y++) {
                    for (var x = 0; x < Size; x++) {
                        var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                        var v = copy[y * Size + x] + peak * Math.Exp(-d2 / (2 * sigma * sigma));
                        copy[y * Size + x] = (ushort)Math.Min(65535, Math.Round(v));
                    }
                }
            }
            return copy;
        }

        [Test]
        public void Other_stars_in_the_crop_are_found_and_masked() {
            var px = WithNeighbours(LoadCrop());
            var crop = Array.ConvertAll(px, v => (double)v);
            var others = BahtinovAnalyzer.OtherStars(crop, Size, 128.5, 128.5);
            Assert.That(others, Has.Count.EqualTo(2), string.Join(" ", others));
            Assert.That(others, Does.Contain((128, 68)));
            Assert.That(others, Does.Contain((140, 223)));
            Assert.That(BahtinovAnalyzer.OtherStars(Array.ConvertAll(LoadCrop(), v => (double)v), Size, 128.5, 128.5), Is.Empty,
                "the real crop has the mask star alone");
        }

        [TestCase(1.2, 0.0)]
        [TestCase(1.5, 8.0)]
        [TestCase(2.5, 3.0)]
        [TestCase(4.0, 0.0)]
        public void A_bright_mask_stars_own_spikes_are_not_taken_for_neighbours(double blurPx, double offsetPx) {
            // A saturated synthetic mask star with long spikes: before the compactness test 17–25 points on
            // its own spike ridges cleared 15σ and were masked out of the fit (review of #1365).
            var px = OpenAstroAra.Server.Services.SyntheticSky.RenderBahtinov(Size, Size, 128, 128, offsetPx, blurPx,
                fieldStars: false, frameSeed: 3);
            var crop = Array.ConvertAll(px, v => (double)v);
            Assert.That(BahtinovAnalyzer.OtherStars(crop, Size, 128.5, 128.5), Is.Empty);
            var m = BahtinovAnalyzer.Analyze(px, Size, Size, out var problem);
            Assert.That(m, Is.Not.Null, $"no measurement: {problem}");
            Assert.That(m!.OffsetPx, Is.EqualTo(offsetPx).Within(0.3));
        }

        [Test]
        public void Neighbouring_stars_in_the_crop_do_not_spoil_the_measurement() {
            var alone = BahtinovAnalyzer.Analyze(LoadCrop(), Size, Size, out _)!;
            var m = BahtinovAnalyzer.Analyze(WithNeighbours(LoadCrop()), Size, Size, out var problem);
            Assert.That(m, Is.Not.Null, $"no measurement with neighbours: {problem}");
            Assert.That(m!.Central.AngleDeg, Is.EqualTo(alone.Central.AngleDeg).Within(1.5));
            Assert.That(m.OffsetPx, Is.EqualTo(alone.OffsetPx).Within(1.0));
        }

        [Test]
        public void A_picked_star_keeps_its_lock_against_a_brighter_neighbour() {
            // A star twice as bright 60 px above the picked one: inside the default search window, outside
            // the picked one's.
            var px = (ushort[])LoadCrop().Clone();
            for (var y = 0; y < Size; y++) {
                for (var x = 0; x < Size; x++) {
                    var d2 = (x - 128.0) * (x - 128.0) + (y - 68.0) * (y - 68.0);
                    px[y * Size + x] = (ushort)Math.Min(65535, px[y * Size + x] + 20000 * Math.Exp(-d2 / (2 * 1.8 * 1.8)));
                }
            }
            var stolen = BahtinovAnalyzer.FindBrightStar(px, Size, Size, (128, 128), BahtinovAnalyzer.DefaultSearchRadius);
            Assert.That(stolen!.Value.Y, Is.EqualTo(68).Within(2), "the default window takes the brighter neighbour");
            var kept = BahtinovAnalyzer.FindBrightStar(px, Size, Size, (128, 128), BahtinovAnalyzer.PickedSearchRadius);
            Assert.That(kept!.Value.Y, Is.EqualTo(128).Within(2), "the picked window stays on the picked star");
            var m = BahtinovAnalyzer.Analyze(px, Size, Size, out var problem, (128, 128), searchRadius: BahtinovAnalyzer.PickedSearchRadius);
            Assert.That(m, Is.Not.Null, $"no measurement: {problem}");
            Assert.That(m!.StarY, Is.EqualTo(128).Within(2));
        }

        [Test]
        public void The_star_is_found_at_the_crop_centre() {
            var px = LoadCrop();
            var star = BahtinovAnalyzer.FindBrightStar(px, Size, Size);
            Assert.That(star, Is.Not.Null);
            Assert.That(star!.Value.X, Is.EqualTo(128).Within(2));
            Assert.That(star.Value.Y, Is.EqualTo(128).Within(2));
        }

        [Test]
        public void Short_spikes_on_a_small_image_scale_are_measured() {
            var px = LoadCrop();
            var m = BahtinovAnalyzer.Analyze(px, Size, Size, out var problem);
            Assert.That(m, Is.Not.Null, $"no measurement: {problem}");
            var angles = new[] { m!.OuterA.AngleDeg, m.Central.AngleDeg, m.OuterB.AngleDeg }.OrderBy(a => a).ToArray();
            Assert.That(angles[0], Is.EqualTo(13).Within(3), "first outer spike");
            Assert.That(angles[1], Is.EqualTo(153).Within(3), "second outer spike");
            Assert.That(angles[2], Is.EqualTo(172).Within(3), "central spike");
            Assert.That(m.Central.AngleDeg, Is.EqualTo(172).Within(3), "the central spike is the one opposite the widest gap");
            // Not yet in focus that night: the central spike sat about a dozen pixels off the X. The exact
            // value is what later detector changes must not drift from.
            Assert.That(Math.Abs(m.OffsetPx), Is.EqualTo(12.0).Within(2.0));
            Assert.That(m.PeakAdu, Is.GreaterThan(10_000));
        }
    }
}
