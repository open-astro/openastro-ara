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
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public sealed class DomeSettings : Settings, IDomeSettings {

        public DomeSettings() {
            SetDefaultValues();
        }

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            Id = "No_Device";
            LastDeviceName = "";
            ScopePositionEastWestMm = 0.0;
            ScopePositionNorthSouthMm = 0.0;
            ScopePositionUpDownMm = 0.0;
            DomeRadiusMm = 0.0;
            GemAxisMm = 0.0;
            AzimuthToleranceDegrees = 2.0;
            FindHomeBeforePark = false;
            DomeSyncTimeoutSeconds = 120;
            SettleTimeSeconds = 1;
            SyncSlewDomeWhenMountSlews = false;
            SynchronizeDuringMountSlew = false;
        }

        private string id = string.Empty;

        [DataMember]
        public string Id {
            get => id;
            set {
                if (id != value) {
                    id = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string lastDeviceName = string.Empty;

        [DataMember]
        public string LastDeviceName {
            get => lastDeviceName;
            set {
                if (lastDeviceName != value) {
                    lastDeviceName = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double scopePositionEastWest_mm;

        [DataMember(Name = "ScopePositionEastWest_mm")]
        public double ScopePositionEastWestMm {
            get => scopePositionEastWest_mm;
            set {
                if (scopePositionEastWest_mm != value) {
                    scopePositionEastWest_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double scopePositionNorthSouth_mm;

        [DataMember(Name = "ScopePositionNorthSouth_mm")]
        public double ScopePositionNorthSouthMm {
            get => scopePositionNorthSouth_mm;
            set {
                if (scopePositionNorthSouth_mm != value) {
                    scopePositionNorthSouth_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double scopePositionUpDown_mm;

        [DataMember(Name = "ScopePositionUpDown_mm")]
        public double ScopePositionUpDownMm {
            get => scopePositionUpDown_mm;
            set {
                if (scopePositionUpDown_mm != value) {
                    scopePositionUpDown_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double domeRadius_mm;

        [DataMember(Name = "DomeRadius_mm")]
        public double DomeRadiusMm {
            get => domeRadius_mm;
            set {
                if (domeRadius_mm != value) {
                    domeRadius_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double gemAxis_mm;

        [DataMember(Name = "GemAxis_mm")]
        public double GemAxisMm {
            get => gemAxis_mm;
            set {
                if (gemAxis_mm != value) {
                    gemAxis_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double lateralAxis_mm;

        [DataMember(Name = "LateralAxis_mm")]
        public double LateralAxisMm {
            get => lateralAxis_mm;
            set {
                if (lateralAxis_mm != value) {
                    lateralAxis_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double azimuthTolerance_degrees = 2.0;

        [DataMember(Name = "AzimuthTolerance_degrees")]
        public double AzimuthToleranceDegrees {
            get => azimuthTolerance_degrees;
            set {
                if (azimuthTolerance_degrees != value) {
                    azimuthTolerance_degrees = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool findHomeBeforePark;

        [DataMember]
        public bool FindHomeBeforePark {
            get => findHomeBeforePark;
            set {
                if (findHomeBeforePark != value) {
                    findHomeBeforePark = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int domeSyncTimeoutSeconds = 120;

        [DataMember]
        public int DomeSyncTimeoutSeconds {
            get => domeSyncTimeoutSeconds;
            set {
                if (domeSyncTimeoutSeconds != value) {
                    domeSyncTimeoutSeconds = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool synchronizeDuringMountSlew;

        [DataMember]
        public bool SynchronizeDuringMountSlew {
            get => synchronizeDuringMountSlew;
            set {
                if (synchronizeDuringMountSlew != value) {
                    synchronizeDuringMountSlew = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool syncSlewDomeWhenMountSlews;

        [DataMember]
        public bool SyncSlewDomeWhenMountSlews {
            get => syncSlewDomeWhenMountSlews;
            set {
                if (syncSlewDomeWhenMountSlews != value) {
                    syncSlewDomeWhenMountSlews = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double manualSlewDegrees = 10.0;

        [DataMember]
        public double RotateDegrees {
            get => manualSlewDegrees;
            set {
                if (manualSlewDegrees != value) {
                    manualSlewDegrees = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool closeOnUnsafe;

        [DataMember]
        public bool CloseOnUnsafe {
            get => closeOnUnsafe;
            set {
                if (closeOnUnsafe != value) {
                    closeOnUnsafe = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool parkMountBeforeShutterMove;

        [DataMember]
        public bool ParkMountBeforeShutterMove {
            get => parkMountBeforeShutterMove;
            set {
                if (parkMountBeforeShutterMove != value) {
                    parkMountBeforeShutterMove = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool refuseUnsafeShutterMove;

        [DataMember]
        public bool RefuseUnsafeShutterMove {
            get => refuseUnsafeShutterMove;
            set {
                if (refuseUnsafeShutterMove != value) {
                    refuseUnsafeShutterMove = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool refuseUnsafeShutterOpenSansSafetyDevice;

        [DataMember]
        public bool RefuseUnsafeShutterOpenSansSafetyDevice {
            get => refuseUnsafeShutterOpenSansSafetyDevice;
            set {
                if (refuseUnsafeShutterOpenSansSafetyDevice != value) {
                    refuseUnsafeShutterOpenSansSafetyDevice = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool refuseUnparkWithoutShutterOpen;

        [DataMember]
        public bool RefuseUnparkWithoutShutterOpen {
            get => refuseUnparkWithoutShutterOpen;
            set {
                if (refuseUnparkWithoutShutterOpen != value) {
                    refuseUnparkWithoutShutterOpen = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool parkDomeBeforeShutterMove;

        [DataMember]
        public bool ParkDomeBeforeShutterMove {
            get => parkDomeBeforeShutterMove;
            set {
                if (parkDomeBeforeShutterMove != value) {
                    parkDomeBeforeShutterMove = value;
                    RaisePropertyChanged();
                }
            }
        }

        private MountType mountType = MountType.EQUATORIAL;

        [DataMember]
        public MountType MountType {
            get => mountType;
            set {
                if (mountType != value) {
                    mountType = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double decOffsetHorizontal_mm;

        [DataMember(Name = "DecOffsetHorizontal_mm")]
        public double DecOffsetHorizontalMm {
            get => decOffsetHorizontal_mm;
            set {
                if (decOffsetHorizontal_mm != value) {
                    decOffsetHorizontal_mm = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int settleTimeSeconds;

        [DataMember]
        public int SettleTimeSeconds {
            get => settleTimeSeconds;
            set {
                if (settleTimeSeconds != value) {
                    settleTimeSeconds = value;
                    RaisePropertyChanged();
                }
            }
        }
    }
}