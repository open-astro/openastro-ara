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

    // #1364 (epic item 1) — the live guide-camera view: what the guider is looking at, which star it
    // holds, and the few RPCs a user needs to choose another one from Ara instead of the guider's page.

    /// <summary>Response for <c>get_search_region</c> (the existing <c>Phd2GetSearchRegion</c> message): the
    /// half-size in pixels of the box the guider searches for its star each frame.</summary>
    public class Phd2IntResponse : PhdMethodResponse {
        public int? result { get; set; }
    }

    /// <summary><c>get_camera_frame_size</c> → <c>[width, height]</c> of the guide camera's frame.</summary>
    public class Phd2IntListResponse : PhdMethodResponse {
        public IReadOnlyList<int>? result { get; set; }
    }

    public class Phd2DeselectStar : Phd2Method {
        public override string Method => "deselect_star";
    }

    /// <summary><c>get_guider_options</c>: the guider's star-tracking options; only
    /// <c>multistar_enabled</c> is read for the live view.</summary>
    public class Phd2GetGuiderOptions : Phd2Method {
        public override string Method => "get_guider_options";
    }

    public class Phd2GuiderOptionsResponse : PhdMethodResponse {
        public Phd2GuiderOptions? result { get; set; }
    }

    public class Phd2GuiderOptions {

        [JsonProperty(PropertyName = "multistar_enabled")]
        public bool? MultiStarEnabled { get; set; }
    }
}
