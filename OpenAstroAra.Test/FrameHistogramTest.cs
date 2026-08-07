#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Linq;
using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>§12c.2 raw histogram: binning, stats, and the clip fractions.</summary>
    [TestFixture]
    public class FrameHistogramTest {

        [Test]
        public void Bias_level_darks_are_not_black_clipped() {
            // Every pixel near a ~100 ADU bias floor: all land in bin 0, but
            // none are truly clipped — the warning must stay quiet.
            ushort[] pixels = [98, 100, 102, 104];
            var h = SqliteFrameRepository.ComputeHistogram(pixels);
            Assert.That(h.Bins[0], Is.EqualTo(4));
            Assert.That(h.LowClipFraction, Is.Zero, "bias level is not clipping");
            Assert.That(h.HighClipFraction, Is.Zero);
        }

        [Test]
        public void Bins_stats_and_clip_fractions() {
            // 2 black (bin 0), 1 mid (32768 → bin 64), 1 saturated (bin 127).
            ushort[] pixels = [0, 0, 32768, 65535];
            var h = SqliteFrameRepository.ComputeHistogram(pixels);
            Assert.That(h.Bins, Has.Count.EqualTo(128));
            Assert.That(h.Bins.Sum(), Is.EqualTo(4), "every pixel lands in exactly one bin");
            Assert.That(h.Bins[0], Is.EqualTo(2));
            Assert.That(h.Bins[64], Is.EqualTo(1));
            Assert.That(h.Bins[127], Is.EqualTo(1));
            Assert.That((h.MinAdu, h.MaxAdu), Is.EqualTo((0, 65535)));
            Assert.That(h.MeanAdu, Is.EqualTo((0 + 0 + 32768 + 65535) / 4.0).Within(1e-9));
            Assert.That(h.LowClipFraction, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(h.HighClipFraction, Is.EqualTo(0.25).Within(1e-9));
        }

        [Test]
        public void Empty_input_degrades_to_zeros() {
            var h = SqliteFrameRepository.ComputeHistogram([]);
            Assert.That(h.Bins.Sum(), Is.Zero);
            Assert.That((h.MinAdu, h.MaxAdu, h.MeanAdu), Is.EqualTo((0, 0, 0.0)));
        }

        [Test]
        public void Median_mad_sd_and_rail_counts_are_exact() {
            // 100..104 once each → median 102 (3rd of 5), deviations
            // {2,1,0,1,2} → MAD 1; plus a repeated min for the count.
            ushort[] pixels = [100, 100, 101, 102, 103, 104];
            var h = SqliteFrameRepository.ComputeHistogram(pixels);
            Assert.That(h.Median, Is.EqualTo(101), "lower median of n=6 (3rd ordered value)");
            Assert.That(h.Mad, Is.EqualTo(1), "deviations {1,1,0,1,2,3} → 3rd ordered is 1");
            Assert.That(h.MeanAdu, Is.EqualTo(610 / 6.0).Within(1e-9));
            Assert.That(h.StdDev, Is.EqualTo(1.4907).Within(0.001));
            Assert.That((h.MinAdu, h.MinCount), Is.EqualTo((100, 2L)));
            Assert.That((h.MaxAdu, h.MaxCount), Is.EqualTo((104, 1L)));
        }
    }
}
