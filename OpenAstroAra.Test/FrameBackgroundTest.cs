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
using OpenAstroAra.Server.Services;
using System;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>#1140 P2 — the per-frame sky background: a 5th percentile that a nebula cannot lift.</summary>
    [TestFixture]
    public class FrameBackgroundTest {

        [Test]
        public void P05_and_median_are_exact_rank_statistics() {
            // 0..99 once each: rank 5 of 100 is value 4; the median rank (n+1)/2 = 50 is the LOWER middle value, 49.
            var pixels = Enumerable.Range(0, 100).Select(v => (ushort)v).ToArray();
            var (p05, median) = FrameBackground.Measure(pixels);
            Assert.That(p05, Is.EqualTo(4));
            Assert.That(median, Is.EqualTo(49));
        }

        [Test]
        public void A_frame_filling_nebula_lifts_the_median_but_not_the_percentile() {
            // 1000 sky pixels at 520 ADU; 1500 nebula pixels at 1400 ADU (60 % of the field). The
            // median sits in the nebula; the darkest 5 % is still sky. This is the dry run's point.
            var pixels = Enumerable.Repeat((ushort)520, 1000).Concat(Enumerable.Repeat((ushort)1400, 1500)).ToArray();
            var (p05, median) = FrameBackground.Measure(pixels);
            Assert.That(p05, Is.EqualTo(520));
            Assert.That(median, Is.EqualTo(1400));
        }

        [Test]
        public void Empty_frame_measures_nothing_and_rails_are_honoured() {
            var (p05, median) = FrameBackground.Measure(ReadOnlySpan<ushort>.Empty);
            Assert.That(double.IsNaN(p05) && double.IsNaN(median));
            var saturated = Enumerable.Repeat(ushort.MaxValue, 50).ToArray();
            Assert.That(FrameBackground.Measure(saturated), Is.EqualTo((65535.0, 65535.0)));
        }
    }
}
