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

namespace OpenAstroAra.Test {

    /// <summary>§45.4 — the binning tier per guide-camera frame size, and the profile override.</summary>
    [TestFixture]
    public class PolarAlignBinningTest {

        [TestCase(1280, 960, 1, TestName = "ASI120MM (1.2 MP) stays 1x1")]
        [TestCase(1936, 1096, 1, TestName = "ASI290MM (2.1 MP) stays 1x1")]
        [TestCase(1936, 1216, 1, TestName = "ASI174MM (2.4 MP) stays 1x1")]
        [TestCase(3096, 2080, 2, TestName = "6.4 MP sensor bins 2x2")]
        [TestCase(4144, 2822, 3, TestName = "ASI294MM (11.7 MP) bins 3x3")]
        [TestCase(6248, 4176, 4, TestName = "ASI2600 (26 MP) bins 4x4")]
        [TestCase(9576, 6388, 4, TestName = "QHY600 (61 MP) is capped at 4x4")]
        [TestCase(0, 0, 1, TestName = "an unknown size is 1x1")]
        public void Tier_follows_the_sensor_area(int width, int height, int expected)
            => Assert.That(PolarAlignBinning.ForFrame(width, height), Is.EqualTo(expected));

        [Test]
        public void Auto_resolves_from_the_frame_and_an_override_wins() {
            Assert.That(PolarAlignBinning.Resolve(PolarAlignBinning.Auto, (4144, 2822)), Is.EqualTo(3));
            Assert.That(PolarAlignBinning.Resolve(PolarAlignBinning.Auto, null), Is.EqualTo(1), "no frame size → 1×1");
            Assert.That(PolarAlignBinning.Resolve(2, (4144, 2822)), Is.EqualTo(2), "the profile's value is the user's override");
            Assert.That(PolarAlignBinning.Resolve(9, null), Is.EqualTo(PolarAlignBinning.Max), "clamped to the §45.4 cap");
        }
    }
}
