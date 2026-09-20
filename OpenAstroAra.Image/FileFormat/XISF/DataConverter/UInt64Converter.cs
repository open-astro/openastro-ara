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
    /// XISF UInt64 samples span [0, 2^64-1], mapped linearly onto [0, 1]. Rescaled to 16 bits
    /// with rounding. Reads little-endian as the spec mandates.
    /// </summary>
    internal sealed class UInt64Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ArgumentNullException.ThrowIfNull(rawData);
            ushort[] data = new ushort[rawData.Length / 8];
            var span = rawData.AsSpan();
            for (var i = 0; i < data.Length; i++) {
                ulong v = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(i * 8, 8));
                data[i] = (ushort)Math.Round(v / (double)ulong.MaxValue * ushort.MaxValue);
            }
            return data;
        }
    }
}
