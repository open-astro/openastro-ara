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

    internal sealed class UInt64Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ushort[] data = new ushort[rawData.Length / 8];
            for (var i = 0; i < data.Length; i++) {
                var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    rawData.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                data[i] = (ushort)Math.Round(value / (double)ulong.MaxValue * ushort.MaxValue,
                    MidpointRounding.AwayFromZero);
            }
            return data;
        }
    }
}