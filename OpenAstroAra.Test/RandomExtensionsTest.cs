#region "copyright"

/*
    Copyright (c) 2026 OpenAstro contributors

    This file is part of OpenAstro Ara.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using System;
using System.Linq;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class RandomExtensionsTest {

        [Test]
        public void NextGaussian_has_the_requested_mean_and_spread() {
            // Seeded, so the sample statistics are deterministic. 200k draws: the mean's standard
            // error is 2.5/sqrt(200000) ≈ 0.006, so 0.05 is an eight-sigma band.
            var random = new Random(20261018);
            var samples = Enumerable.Range(0, 200_000).Select(_ => random.NextGaussian(mean: 3.0, stdDev: 2.5)).ToArray();

            var mean = samples.Average();
            var stdDev = Math.Sqrt(samples.Sum(s => (s - mean) * (s - mean)) / (samples.Length - 1));

            Assert.That(mean, Is.EqualTo(3.0).Within(0.05));
            Assert.That(stdDev, Is.EqualTo(2.5).Within(0.05));
            Assert.That(samples.Count(s => Math.Abs(s - 3.0) > 3 * 2.5), Is.LessThan(samples.Length * 0.005), "beyond three sigma");
            Assert.That(samples.All(double.IsFinite), "every draw finite");
        }
    }
}
