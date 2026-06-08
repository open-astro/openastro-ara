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
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Equipment.Equipment.MyGPS {
    public class GnssFactory : IGnssFactory {
        private readonly IProfileService profileService;

        public GnssFactory(IProfileService profileService) {
            this.profileService = profileService;
        }

        public IGnss? GetGnssSource(GnssSource gnss) {
            // PegausAstroUranusMeteo + PrimaLuceLabEagle drivers are
            // vendor-specific impls dropped per Phase 2 Alpaca-only collapse.
            return gnss switch {
                GnssSource.NmeaSerial => new NMEAGps(),
                GnssSource.Gpsd => new Gpsd(profileService),
                _ => null,
            };
        }


        public IGnss? GetGnssSource() {
            return GetGnssSource(profileService.ActiveProfile.GnssSettings.GnssSource);
        }
    }
}