#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents {

    /// <summary>
    /// §42.2 — openastro-guider's structured equipment-fault event (#57), emitted in addition to the
    /// generic <c>Alert</c> so a client can react without string-parsing. Fields are snake_case on the
    /// wire. <c>device_type</c> is effectively <c>"camera"</c> today; <c>reconnecting</c> = the daemon is
    /// making a best-effort auto-reconnect (throttled ~3/min, and silently abandoned when exhausted with
    /// no terminal event — open-astro/openastro-guider#66, so ARA pairs it with its own watchdog later).
    /// </summary>
    public class PhdEventEquipmentDisconnected : PhdEvent {

        [JsonProperty(PropertyName = "device_type")]
        public string DeviceType { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "reconnecting")]
        public bool Reconnecting { get; set; }
    }
}
