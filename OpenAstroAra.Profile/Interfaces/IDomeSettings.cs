#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;

namespace OpenAstroAra.Profile.Interfaces {

    public interface IDomeSettings : ISettings {
        string Id { get; set; }
        string LastDeviceName { get; set; }
        double ScopePositionEastWestMm { get; set; }
        double ScopePositionNorthSouthMm { get; set; }
        double ScopePositionUpDownMm { get; set; }
        double DomeRadiusMm { get; set; }
        double GemAxisMm { get; set; }
        double LateralAxisMm { get; set; }
        double AzimuthToleranceDegrees { get; set; }
        bool FindHomeBeforePark { get; set; }
        int DomeSyncTimeoutSeconds { get; set; }
        bool SynchronizeDuringMountSlew { get; set; }
        bool SyncSlewDomeWhenMountSlews { get; set; }
        double RotateDegrees { get; set; }
        bool CloseOnUnsafe { get; set; }
        bool ParkMountBeforeShutterMove { get; set; }
        bool RefuseUnsafeShutterMove { get; set; }
        bool RefuseUnparkWithoutShutterOpen { get; set; }
        bool RefuseUnsafeShutterOpenSansSafetyDevice { get; set; }
        bool ParkDomeBeforeShutterMove { get; set; }
        MountType MountType { get; set; }
        double DecOffsetHorizontalMm { get; set; }
        int SettleTimeSeconds { get; set; }
    }
}