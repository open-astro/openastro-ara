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
using OpenAstroAra.Stretch;

namespace OpenAstroAra.Test {

    /// <summary>§65 fast-preview decimation: box-average correctness and stride selection.</summary>
    [TestFixture]
    public class DecimatorTest {

        [Test]
        public void Stride_targets_the_long_side() {
            Assert.That(Decimator.StrideFor(6248, 4176, 2048), Is.EqualTo(4), "26 MP mono → 4×4 blocks");
            Assert.That(Decimator.StrideFor(1920, 1080, 2048), Is.EqualTo(1), "already small enough");
            Assert.That(Decimator.StrideFor(4096, 4096, 2048), Is.EqualTo(2));
            Assert.That(Decimator.StrideFor(100, 100, 0), Is.EqualTo(1), "no cap = no work");
        }

        [Test]
        public void Boxes_average_and_trailing_pixels_drop() {
            // 5×3 image, stride 2 → 2×1 output; the 5th column and 3rd row drop.
            ushort[] pixels = [
                10, 20, 30, 40, 999,
                30, 40, 50, 60, 999,
                7,  7,  7,  7,  999,
            ];
            var (output, w, h) = Decimator.Decimate(pixels, 5, 3, 2);
            Assert.That((w, h), Is.EqualTo((2, 1)));
            Assert.That(output, Is.EqualTo(new ushort[] { 25, 45 }), "each output is its 2×2 block mean");
        }

        [Test]
        public void Stride_one_is_identity() {
            ushort[] pixels = [1, 2, 3, 4];
            var (output, w, h) = Decimator.Decimate(pixels, 2, 2, 1);
            Assert.That((w, h), Is.EqualTo((2, 2)));
            Assert.That(output, Is.EqualTo(pixels));
        }

        [Test]
        public void Saturated_input_does_not_overflow() {
            var pixels = new ushort[16];
            System.Array.Fill(pixels, ushort.MaxValue);
            var (output, _, _) = Decimator.Decimate(pixels, 4, 4, 4);
            Assert.That(output, Is.EqualTo(new ushort[] { ushort.MaxValue }));
        }
    }
}
