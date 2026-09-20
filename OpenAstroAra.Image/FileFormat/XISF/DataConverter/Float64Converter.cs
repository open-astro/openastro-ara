#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

using System.Buffers.Binary;

namespace OpenAstroAra.Image.FileFormat.XISF.DataConverter {

    /// <summary>
    /// XISF Float64 samples, handled exactly like Float32: clamped to [0, 1], NaN reads as 0.
    /// </summary>
    internal sealed class Float64Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ArgumentNullException.ThrowIfNull(rawData);
            ushort[] data = new ushort[rawData.Length / 8];
            var span = rawData.AsSpan();
            for (var i = 0; i < data.Length; i++) {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(i * 8, 8));
                data[i] = FloatSample.ToUInt16(v);
            }
            return data;
        }
    }
}
