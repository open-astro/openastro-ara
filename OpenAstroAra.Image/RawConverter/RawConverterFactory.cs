#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enum;
using OpenAstroAra.Image.Interfaces;

namespace OpenAstroAra.Image.RawConverter {

    public class RawConverterFactory {

        public static IRawConverter CreateInstance(RawConverterEnum converter, IImageDataFactory imageDataFactory) {
            switch (converter) {
                case RawConverterEnum.DCRAW:
                    return new DCRaw(imageDataFactory);

                case RawConverterEnum.FREEIMAGE:
                    return new FreeImageConverter(imageDataFactory);

                default:
                    return new FreeImageConverter(imageDataFactory);
            }
        }
    }
}