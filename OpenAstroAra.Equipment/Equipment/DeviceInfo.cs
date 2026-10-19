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
using System.Linq;
using System.Reflection;

namespace OpenAstroAra.Equipment.Equipment {

    public class DeviceInfo : BaseINPC {
        private bool connected;

        public bool Connected {
            get => connected;
            set => SetProperty(ref connected, value);
        }

        private string name = string.Empty;

        public string Name {
            get => name;
            set => SetProperty(ref name, value);
        }

        private string displayName = string.Empty;

        public string DisplayName {
            get => displayName;
            set => SetProperty(ref displayName, value);
        }

        private string description = string.Empty;

        public string Description {
            get => description;
            set => SetProperty(ref description, value);
        }

        private string driverInfo = string.Empty;

        public string DriverInfo {
            get => driverInfo;
            set => SetProperty(ref driverInfo, value);
        }

        private string driverVersion = string.Empty;

        public string DriverVersion {
            get => driverVersion;
            set => SetProperty(ref driverVersion, value);
        }

        private string deviceId = string.Empty;

        public string DeviceId {
            get => deviceId;
            set => SetProperty(ref deviceId, value);
        }

        public static T CreateDefaultInstance<T>() where T : DeviceInfo, new() {
            return new T() {
                Connected = false
            };
        }

        public void Reset() {
            var defaultInstance = (DeviceInfo)Activator.CreateInstance(this.GetType())!;
            defaultInstance.Connected = false;
            this.CopyFrom(defaultInstance);
        }

        public void CopyFrom(DeviceInfo other) {
            foreach (PropertyInfo property in this.GetType().GetProperties().Where(p => p.CanWrite)) {
                property.SetValue(this, property.GetValue(other, null), null);
            }
        }
    }
}