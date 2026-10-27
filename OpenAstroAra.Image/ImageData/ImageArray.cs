#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.Interfaces;
using System;

namespace OpenAstroAra.Image.ImageData {

    public class ImageArray : IImageArray {

        private ImageArray() {
        }

        public ImageArray(ushort[] flatArray) {
            this.FlatArray = flatArray;
        }

        public ushort[] FlatArray { get; private set; } = null!;
        public int[]? FlatArrayInt { get => null; }
    }

    public class ImageArrayInt : IImageArray {

        private ImageArrayInt() {
        }

        public ImageArrayInt(int[] flatArray) {
            this.FlatArrayInt = flatArray;
        }

        private ushort[]? cachedFlatArray;
        public ushort[] FlatArray {
            get {
                var cached = cachedFlatArray;
                if (cached != null)
                    return cached;

                if (FlatArrayInt == null)
                    return Array.Empty<ushort>();

                // Build into a local and publish once, so a concurrent reader never observes a
                // partially-filled array.
                var built = new ushort[FlatArrayInt.Length];
                for (int i = 0; i < FlatArrayInt.Length; i++) {
                    // Ensure the conversion is safe
                    if (FlatArrayInt[i] < 0 || FlatArrayInt[i] > ushort.MaxValue) {
                        built[i] = ushort.MaxValue;
                    } else {
                        built[i] = (ushort)FlatArrayInt[i];
                    }
                }

                return System.Threading.Interlocked.CompareExchange(ref cachedFlatArray, built, null) ?? built;
            }
        }

        public int[]? FlatArrayInt { get; private set; }
    }
}