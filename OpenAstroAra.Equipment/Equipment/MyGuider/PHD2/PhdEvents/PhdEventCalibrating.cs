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

    /// <summary>One calibration step: the guider's <c>Calibrating</c> event (direction <c>West</c>,
    /// <c>East</c>, <c>North</c>, <c>South</c> or a backlash-clearing leg, the step count in that leg and
    /// how far the star has moved from the start, in pixels).</summary>
    public class PhdEventCalibrating : PhdEvent {

        [JsonProperty(PropertyName = "dir")]
        public string? Direction { get; set; }

        [JsonProperty(PropertyName = "step")]
        public int Step { get; set; }

        [JsonProperty(PropertyName = "dist")]
        public double Distance { get; set; }

        [JsonProperty(PropertyName = "State")]
        public string? State { get; set; }
    }
}
