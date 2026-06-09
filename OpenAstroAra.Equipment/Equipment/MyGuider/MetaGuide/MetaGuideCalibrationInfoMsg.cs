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
using System;
using System.Globalization;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.MetaGuide {

    public class MetaGuideCalibrationInfoMsg : MetaGuideBaseMsg {

        private MetaGuideCalibrationInfoMsg() {
        }

        public static MetaGuideCalibrationInfoMsg? Create(string[] args) {
            if (args.Length < 11) {
                return null;
            }
            try {
                return new MetaGuideCalibrationInfoMsg() {
                    WestAngle = double.Parse(args[5], CultureInfo.InvariantCulture),
                    Parity = int.Parse(args[6], CultureInfo.InvariantCulture),
                    WestX = double.Parse(args[7], CultureInfo.InvariantCulture),
                    WestY = double.Parse(args[8], CultureInfo.InvariantCulture),
                    NorthX = double.Parse(args[9], CultureInfo.InvariantCulture),
                    NorthY = double.Parse(args[10], CultureInfo.InvariantCulture)
                };
            } catch (Exception ex) when (ex is FormatException or OverflowException) {
                Logger.Error(ex);
                return null;
            }
        }

        public double WestAngle { get; private set; }
        public int Parity { get; private set; }
        public double WestX { get; private set; }
        public double WestY { get; private set; }
        public double NorthX { get; private set; }
        public double NorthY { get; private set; }
    }
}