#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Equipment.Interfaces;
using System.Collections.Generic;

namespace OpenAstroAra.Equipment.Equipment.MyDome {

    public class DomeInfo : DeviceInfo {
        private ShutterState shutterStatus = ShutterState.ShutterNone;

        public ShutterState ShutterStatus {
            get => shutterStatus;
            set => SetProperty(ref shutterStatus, value);
        }

        private bool driverCanFollow;

        public bool DriverCanFollow {
            get => driverCanFollow;
            set => SetProperty(ref driverCanFollow, value);
        }

        private bool canSetShutter;

        public bool CanSetShutter {
            get => canSetShutter;
            set => SetProperty(ref canSetShutter, value);
        }

        private bool canSetPark;

        public bool CanSetPark {
            get => canSetPark;
            set => SetProperty(ref canSetPark, value);
        }

        private bool canSetAzimuth;

        public bool CanSetAzimuth {
            get => canSetAzimuth;
            set => SetProperty(ref canSetAzimuth, value);
        }

        private bool canSyncAzimuth;

        public bool CanSyncAzimuth {
            get => canSyncAzimuth;
            set => SetProperty(ref canSyncAzimuth, value);
        }

        private bool canPark;

        public bool CanPark {
            get => canPark;
            set => SetProperty(ref canPark, value);
        }

        private bool canFindHome;

        public bool CanFindHome {
            get => canFindHome;
            set => SetProperty(ref canFindHome, value);
        }

        private bool atPark;

        public bool AtPark {
            get => atPark;
            set => SetProperty(ref atPark, value);
        }

        private bool atHome;

        public bool AtHome {
            get => atHome;
            set => SetProperty(ref atHome, value);
        }

        private bool driverFollowing;

        public bool DriverFollowing {
            get => driverFollowing;
            set {
                if (SetProperty(ref driverFollowing, value)) {
                    RaisePropertyChanged(nameof(FollowingType));
                }
            }
        }

        private bool applicationFollowing;

        public bool ApplicationFollowing {
            get => applicationFollowing;
            set {
                if (SetProperty(ref applicationFollowing, value)) {
                    RaisePropertyChanged(nameof(FollowingType));
                }
            }
        }

        public string FollowingType {
            get {
                if (DriverFollowing) {
                    return Loc.Instance["LblDomeFollowingViaDriver"];
                }
                if (ApplicationFollowing) {
                    return Loc.Instance["LblDomeFollowingViaNINA"];
                }
                return Loc.Instance["LblOff"];
            }
        }

        private bool slewing;

        public bool Slewing {
            get => slewing;
            set => SetProperty(ref slewing, value);
        }

        private double azimuth = double.NaN;

        public double Azimuth {
            get => azimuth;
            set {
                if (SetProperty(ref azimuth, value)) {
                    RaisePropertyChanged(nameof(AzimuthDMS));
                }
            }
        }
        public string AzimuthDMS => double.IsNaN(Azimuth) ? "" : AstroUtil.DegreesToDMS(Azimuth);

        private double altitude = double.NaN;

        public double Altitude {
            get => altitude;
            set {
                if (SetProperty(ref altitude, value)) {
                    RaisePropertyChanged(nameof(AltitudeDMS));
                }
            }
        }

        public string AltitudeDMS => double.IsNaN(Altitude) ? "" : AstroUtil.DegreesToDMS(Altitude);

        public IReadOnlyList<string> SupportedActions { get; } = [];

    }
}