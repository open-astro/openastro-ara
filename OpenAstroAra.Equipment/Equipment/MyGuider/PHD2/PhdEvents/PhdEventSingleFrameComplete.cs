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
    /// §45 — the guider's <c>SingleFrameComplete</c> event, announcing that a fire-and-forget
    /// <c>capture_single_frame</c> finished (the RPC itself acks <c>0</c> immediately). PascalCase on
    /// the wire, like the other PHD2 events. <c>Success</c> is always present; <c>Error</c> rides along
    /// only on failure; <c>Path</c> only when the frame was saved (<c>save</c>/<c>path</c> was set) — the
    /// saved-FITS location ARA hands to its plate solver.
    /// </summary>
    public class PhdEventSingleFrameComplete : PhdEvent {

        [JsonProperty(PropertyName = "Success")]
        public bool Success { get; set; }

        [JsonProperty(PropertyName = "Error")]
        public string? Error { get; set; }

        [JsonProperty(PropertyName = "Path")]
        public string? Path { get; set; }
    }
}
