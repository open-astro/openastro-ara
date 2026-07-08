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

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents {

    public class PhdEventVersion : PhdEvent {

        [JsonProperty]
        public string PHDVersion { get; set; } = string.Empty;

        [JsonProperty]
        public string PHDSubver { get; set; } = string.Empty;

        [JsonProperty]
        public int MsgVersion { get; set; }

        // §63.9 — the openastro-guider fork markers on the "Version" event (PascalCase; the get_version
        // RPC result carries the same values snake_cased). Fork is "openastro-guider" on the fork,
        // empty on stock PHD2; OverlapSupport is a fork-only pipelined-RPC capability.
        [JsonProperty]
        public string Fork { get; set; } = string.Empty;

        [JsonProperty]
        public bool OverlapSupport { get; set; }
    }
}