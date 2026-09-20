#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;


namespace OpenAstroAra.Image.FileFormat.XISF.DataConverter {

    /// <summary>
    /// Shared floating-point sample mapping for the Float32/Float64 converters.
    /// </summary>
    internal static class FloatSample {

        public static ushort ToUInt16(double v) {
            if (double.IsNaN(v)) { return 0; }
            if (v <= 0d) { return 0; }
            if (v >= 1d) { return ushort.MaxValue; }
            return (ushort)Math.Round(v * ushort.MaxValue);
        }
    }
}
