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
    /// §42.2 — openastro-guider's structured <c>EquipmentReconnected</c> event (#57): the daemon's
    /// best-effort auto-reconnect of a previously-dropped device succeeded. The guide link was never
    /// down, so this is informational — ARA uses it to re-arm the one-shot fault reaction.
    /// </summary>
    public class PhdEventEquipmentReconnected : PhdEvent {

        [JsonProperty(PropertyName = "device_type")]
        public string DeviceType { get; set; } = string.Empty;
    }
}
