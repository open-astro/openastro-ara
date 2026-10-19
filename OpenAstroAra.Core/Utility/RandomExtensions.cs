#region "copyright"

/*
    Copyright (c) 2026 OpenAstro contributors

    This file is part of OpenAstro Ara.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Core.Utility {

    public static class RandomExtensions {

        /// <summary>
        /// A normally distributed sample (Box-Muller), the one call Ara made into
        /// Accord.Statistics before #1386 retired it: the dither distance in the direct guider.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
            Justification = "Numerical jitter for guider dithering; the caller passes its own System.Random. Nothing security-sensitive is derived from it.")]
        public static double NextGaussian(this Random random, double mean, double stdDev) {
            ArgumentNullException.ThrowIfNull(random);
            // (0, 1]: log of 0 is -inf, and NextDouble() can return exactly 0.
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return mean + stdDev * standardNormal;
        }
    }
}
