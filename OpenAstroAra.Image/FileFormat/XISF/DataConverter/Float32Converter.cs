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
    /// XISF Float32 samples are nominally in [0, 1] (the <c>bounds</c> attribute may say otherwise;
    /// the caller applies it). Out-of-range values are clamped and NaN reads as 0, so a stray
    /// sample cannot wrap into garbage.
    /// </summary>
    internal sealed class Float32Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ArgumentNullException.ThrowIfNull(rawData);
            ushort[] data = new ushort[rawData.Length / 4];
            var span = rawData.AsSpan();
            for (var i = 0; i < data.Length; i++) {
                float v = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 4, 4));
                data[i] = FloatSample.ToUInt16(v);
            }
            return data;
        }
    }
}
