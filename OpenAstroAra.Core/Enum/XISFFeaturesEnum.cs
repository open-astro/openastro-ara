#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System.ComponentModel;

namespace OpenAstroAra.Core.Enums {

    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    public enum XISFCompressionType {

        [Description("LblNone")]
        NONE = 0,

        [Description("LblCompressionLZ4")]
        LZ4,

        [Description("LblCompressionLZ4HC")]
        LZ4HC,

        [Description("LblCompressionZLib")]
        ZLIB
    }

    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    public enum XISFChecksumType {

        [Description("LblNone")]
        NONE = 0,

        [Description("LblChecksumSHA_1")]
        SHA1,

        [Description("LblChecksumSHA_256")]
        SHA256,

        [Description("LblChecksumSHA_512")]
        SHA512,

        [Description("LblChecksumSHA3_256")]
        Sha3256,

        [Description("LblChecksumSHA3_512")]
        Sha3512
    }
}