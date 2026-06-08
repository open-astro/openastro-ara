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

namespace OpenAstroAra.Core.Enums {

    [TypeConverter(typeof(EnumDescriptionTypeConverter))]
    public enum ImageHistory {

        [Description("LblNone")]
        NONE,

        [Description("LblHFR")]
        HFR,

        [Description("LblStars")]
        Stars,

        [Description("LblMedian")]
        Median,

        [Description("LblMean")]
        Mean,

        [Description("LblStDev")]
        StDev,

        [Description("LblMAD")]
        MAD,

        [Description("LblTemperature")]
        Temperature,

        [Description("LblRms")]
        Rms
    }
}