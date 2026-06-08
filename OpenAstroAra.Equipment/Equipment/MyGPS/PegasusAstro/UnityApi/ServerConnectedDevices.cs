#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using System.Collections.Generic;

namespace OpenAstroAra.Equipment.Equipment.MyGPS.PegasusAstro.UnityApi {

    internal static class ServerConnectedDevices {

        public class Device {

            [JsonProperty("uniqueKey")]
            public string UniqueKey { get; set; } = string.Empty;

            [JsonProperty("name")]
            public string Name { get; set; } = string.Empty;

            [JsonProperty("fullName")]
            public string FullName { get; set; } = string.Empty;

            [JsonProperty("deviceID")]
            public string DeviceID { get; set; } = string.Empty;

            [JsonProperty("firmware")]
            public string Firmware { get; set; } = string.Empty;

            [JsonProperty("revision")]
            public string Revision { get; set; } = string.Empty;
        }

        public class Response {

            [JsonProperty("status")]
            public string Status { get; set; } = string.Empty;

            [JsonProperty("code")]
            public int? Code { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; } = string.Empty;

            [JsonProperty("data")]
            public IReadOnlyList<Device>? Devices { get; set; }
        }
    }
}