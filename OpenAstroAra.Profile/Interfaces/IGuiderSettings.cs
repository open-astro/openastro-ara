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
        /// <summary>RA hysteresis aggressiveness as the 0..1 fraction PHD2's <c>set_algo_param</c> expects —
        /// i.e. the GUI's 0-100% ÷ 100. guider-e-2 sends this to the RPC directly (no ×100 conversion).</summary>
        double RAAggressiveness { get; set; }
        /// <summary>Dec aggressiveness, 0..1 (same wire scale as <see cref="RAAggressiveness"/>).</summary>
        double DecAggressiveness { get; set; }
        /// <summary>RA minimum guide move, pixels (PHD2's <c>minMove</c> on the RA algorithm). Kept under
        /// its historical name: it was one value for both axes before #1156 split Dec out.</summary>
        double MinimumMove { get; set; }
        /// <summary>Dec minimum guide move, pixels (#1156). Dec usually wants a larger value than RA —
        /// backlash and the slower Dec drift make small corrections pure noise chasing.</summary>
        double DecMinimumMove { get; set; }
        /// <summary>Dec guide mode: "auto" | "north" | "south" | "off".</summary>
        string DecGuideMode { get; set; }
        /// <summary>Guide exposure, ms, sent with PHD2's <c>set_exposure</c> (runtime-safe: it applies on
        /// the next guide frame, guiding continues). 0 = unset, leave the guider's own value.</summary>
        int GuideExposureMs { get; set; }
        /// <summary>Guide camera gain, percent of the camera's range (0..100), sent with the guider's
        /// <c>set_camera_gain</c> once its camera is connected.</summary>
        int GuideCameraGain { get; set; }

        // §63.17 guider equipment selection — pushed to the daemon inside the §63.5 disconnected window
        // (the set_selected_* RPCs are blocked while equipment is connected). Values are the daemon's own
        // choice strings, verbatim from get_equipment_choices; "" = unset (not pushed, daemon keeps its own).
        /// <summary>Guide camera choice string ("" = unset).</summary>
        string GuiderCamera { get; set; }
        /// <summary>Driver-specific camera id disambiguating multiple cameras of the same model ("" = unset).</summary>
        string GuiderCameraId { get; set; }
        /// <summary>#1308 — the Alpaca <c>UniqueID</c> of the guide camera behind an <c>Alpaca Camera [host:port/N]</c>
        /// selection ("" = unset / pre-#1308). The device NUMBER can change across an AlpacaBridge restart; the
        /// push re-resolves N from this before sending and refuses when the camera has moved.</summary>
        string GuiderCameraAlpacaUniqueId { get; set; }
        /// <summary>Guide-output mount choice string ("" = unset).</summary>
        string GuiderMount { get; set; }
        /// <summary>Aux (pointing) mount choice string ("" = unset).</summary>
        string GuiderAuxMount { get; set; }
        /// <summary>Rotator choice string ("" = unset). Only meaningful for an off-axis guider (see
        /// <see cref="GuiderSetupType"/>): PHD2 treats the selected rotator as the GUIDE camera's and turns
        /// its calibration by every rotator move, which is wrong for a separate guide scope.</summary>
        string GuiderRotator { get; set; }
        /// <summary>§63.19 guide train: <c>"guide_scope"</c> (a separate guide scope — the guide camera never
        /// turns with the imaging rotator) or <c>"oag"</c> (off-axis guider — it does). Normalized by
        /// <c>GuiderSettings.NormalizeSetupType</c>.</summary>
        string GuiderSetupType { get; set; }
        /// <summary>Alpaca server host the guider's Alpaca devices live on ("" = unset).</summary>
        string GuiderAlpacaHost { get; set; }
        /// <summary>Alpaca server port (0 = unset).</summary>
        int GuiderAlpacaPort { get; set; }
    }
}
