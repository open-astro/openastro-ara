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
using System.IO;

namespace OpenAstroAra.Image.FileFormat.XISF.DataConverter {

    internal sealed class UInt16Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ArgumentNullException.ThrowIfNull(rawData);
            if (rawData.Length % 2 != 0) {
                throw new InvalidDataException($"XISF: UInt16 block of {rawData.Length} bytes is not a whole number of samples");
            }
            // Explicitly little-endian, like the other converters: the spec fixes the byte order,
            // and a BlockCopy would silently read host order on a big-endian machine.
            ushort[] data = new ushort[rawData.Length / 2];
            var span = rawData.AsSpan();
            for (var i = 0; i < data.Length; i++) {
                data[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2, 2));
            }
            return data;
        }
    }
}