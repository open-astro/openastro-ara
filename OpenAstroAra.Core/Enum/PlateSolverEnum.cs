#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System.ComponentModel;

namespace OpenAstroAra.Core.Enum {

    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    public enum PlateSolver {

        [Description("LblAstrometryNet")]
        AstrometryNet,

        [Description("LblLocalPlatesolver")]
        LOCAL,

        [Description("LblPlatesolve2")]
        PLATESOLVE2,

        [Description("LblPlatesolve3")]
        PLATESOLVE3,

        [Description("LblASPS")]
        ASPS,

        [Description("LblASTAPShort")]
        ASTAP,

        [Description("LblTSXImageLink")]
        TSXImageLink,

        [Description("LblPinPoint")]
        PINPONT
    }
}