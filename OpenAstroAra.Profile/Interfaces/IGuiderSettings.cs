#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;

namespace OpenAstroAra.Profile.Interfaces {

    public interface IGuiderSettings : ISettings {
        string GuiderName { get; set; }
        string LastDeviceName { get; set; }
        double DitherPixels { get; set; }
        bool DitherRAOnly { get; set; }
        GuiderScale PHD2GuiderScale { get; set; }
        double MaxY { get; set; }
        int PHD2HistorySize { get; set; }
        int PHD2ServerPort { get; set; }
        string PHD2ServerHost { get; set; }
        int PHD2InstanceNumber { get; set; }
        int SettleTime { get; set; }
        double SettlePixels { get; set; }
        int SettleTimeout { get; set; }
        string PHD2Path { get; set; }
        bool AutoRetryStartGuiding { get; set; }
        int AutoRetryStartGuidingTimeoutSeconds { get; set; }
        bool MetaGuideUseIpAddressAny { get; set; }
        int MetaGuidePort { get; set; }
        int MGENFocalLength { get; set; }
        int MGENPixelMargin { get; set; }
        int MetaGuideMinIntensity { get; set; }
        int MetaGuideDitherSettleSeconds { get; set; }
        bool MetaGuideLockWhenGuiding { get; set; }
        int PHD2ROIPct { get; set; }
        int? PHD2ProfileId { get; set; }
        int SkyGuardServerPort { get; set; }
        string SkyGuardServerHost { get; set; }
        string SkyGuardPath { get; set; }
        int SkyGuardCallbackPort { get; set; }
        bool SkyGuardTimeLapsChecked { get; set; }
        double SkyGuardValueMaxGuiding { get; set; }
        double SkyGuardTimeLapsGuiding { get; set; }
        bool SkyGuardTimeLapsDitherChecked { get; set; }
        double SkyGuardValueMaxDithering { get; set; }
        double SkyGuardTimeLapsDithering { get; set; }
        double SkyGuardTimeOutGuiding { get; set; }
        string GuideChartRightAscensionColor { get; set; }
        string GuideChartDeclinationColor { get; set; }
        bool GuideChartShowCorrections { get; set; }

        // §63.5 guider-engine config — pushed to the guider daemon on connect (set_profile_setup /
        // set_algo_param / set_dec_guide_mode). Stored here so ARA owns the source of truth; the guider-e-2
        // push maps these onto the RPCs.
        /// <summary>Guide scope focal length, mm (0 = unset).</summary>
        int GuideFocalLength { get; set; }
        /// <summary>Guide camera pixel size, µm (0 = unset).</summary>
        double GuidePixelSize { get; set; }
        /// <summary>RA hysteresis aggressiveness, 0..1.</summary>
        double RAAggressiveness { get; set; }
        /// <summary>Dec aggressiveness, 0..1.</summary>
        double DecAggressiveness { get; set; }
        /// <summary>Minimum guide move, pixels (applied to both axes).</summary>
        double MinimumMove { get; set; }
        /// <summary>Dec guide mode: "auto" | "north" | "south" | "off".</summary>
        string DecGuideMode { get; set; }
    }
}