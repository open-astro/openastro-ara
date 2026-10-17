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
using System.Collections.Generic;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // #1364 item 4 — the guider's headless Guiding Assistant (openastro-guider #83): the
    // measurement PHD2's dialog runs (unguided drift, seeing, optional Dec backlash) and the
    // min-move / exposure / backlash recommendations it draws from it.

    /// <summary><c>guiding_assistant_start { duration_s?, measure_backlash }</c>: needs a selected
    /// star and a calibrated mount; the guider starts guiding itself with output disabled.</summary>
    public class Phd2GuidingAssistantStart : Phd2Method<Phd2GuidingAssistantStartParameters> {
        public override string Method => "guiding_assistant_start";
    }

    public class Phd2GuidingAssistantStartParameters {

        [JsonProperty(PropertyName = "duration_s", NullValueHandling = NullValueHandling.Ignore)]
        public double? DurationSec { get; set; }

        [JsonProperty(PropertyName = "measure_backlash")]
        public bool MeasureBacklash { get; set; }
    }

    public class Phd2GuidingAssistantStop : Phd2Method {
        public override string Method => "guiding_assistant_stop";
    }

    /// <summary><c>guiding_assistant_get_status</c>: state, elapsed, the statistics once two samples are
    /// in, the backlash outcome, and the recommendations once done.</summary>
    public class Phd2GuidingAssistantGetStatus : Phd2Method {
        public override string Method => "guiding_assistant_get_status";
    }

    /// <summary><c>guiding_assistant_apply { which: [...] }</c>: write the named recommendations to the
    /// guider's current profile.</summary>
    public class Phd2GuidingAssistantApply : Phd2Method<Phd2GuidingAssistantApplyParameters> {
        public override string Method => "guiding_assistant_apply";
    }

    public class Phd2GuidingAssistantApplyParameters {

        [JsonProperty(PropertyName = "which")]
        public IReadOnlyList<string> Which { get; set; } = [];
    }

    public class Phd2GuidingAssistantApplyResponse : PhdMethodResponse {
        public Phd2GuidingAssistantApplied? result { get; set; }
    }

    public class Phd2GuidingAssistantApplied {

        [JsonProperty(PropertyName = "applied")]
        public IReadOnlyList<string>? Applied { get; set; }
    }
}
