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

    internal sealed class Float32Converter : IDataConverter {

        public ushort[] Convert(byte[] rawData) {
            ushort[] data = new ushort[rawData.Length / 4];
            for (var i = 0; i < data.Length; i++) {
                var bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    rawData.AsSpan(i * sizeof(float), sizeof(float)));
                var value = BitConverter.Int32BitsToSingle(bits);
                data[i] = ScaleNormalized(value);
            }
            return data;
        }

        private static ushort ScaleNormalized(float value) {
            if (!float.IsFinite(value) || value <= 0) return ushort.MinValue;
            if (value >= 1) return ushort.MaxValue;
            return (ushort)Math.Round(value * ushort.MaxValue, MidpointRounding.AwayFromZero);
        }
    }
}