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

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §63.9 fork identification — the synchronous get_version handshake openastro-guider added in
    // its ARA-integration PR (#57, PHD2-GAP gap 3). The catch-up "Version" event still arrives on
    // connect, but this RPC lets the connect path do the "connect → check version → decide" step
    // authoritatively instead of racing the event stream. Same separate-UTF-8-partial pattern as
    // PHD2Methods.PolarAlign — the base PHD2Methods.cs is ISO-8859-1 and is not extended in place.
    //
    // Casing caveat baked into the two shapes below: the RPC RESULT is snake_case (fork,
    // overlap_support), while the "Version" EVENT is PascalCase (Fork, OverlapSupport) — same
    // values, different keys — so ARA reads both (see PhdEventVersion + PHD2Guider.IdentifyGuiderFork).

    /// <summary><c>get_version</c> (no params) — the fork/version handshake. Result carries
    /// <c>{version, phd_version, phd_subver, msg_version, overlap_support, fork}</c>; <c>fork</c> is
    /// <c>"openastro-guider"</c> on the fork and absent on stock PHD2.</summary>
    public class Phd2GetVersion : Phd2Method {
        public override string Method => "get_version";
    }

    public class Phd2GetVersionResponse : PhdMethodResponse {
        public Phd2GetVersionResult? result { get; set; }
    }

    public class Phd2GetVersionResult {

        // The user-facing FULLVER string.
        [JsonProperty(PropertyName = "version")]
        public string? Version { get; set; }

        [JsonProperty(PropertyName = "phd_version")]
        public string? PhdVersion { get; set; }

        [JsonProperty(PropertyName = "phd_subver")]
        public string? PhdSubver { get; set; }

        [JsonProperty(PropertyName = "msg_version")]
        public int? MsgVersion { get; set; }

        // The daemon supports overlapped/pipelined RPCs — a fork capability, absent on stock PHD2.
        [JsonProperty(PropertyName = "overlap_support")]
        public bool? OverlapSupport { get; set; }

        // "openastro-guider" on the fork; null/absent on upstream PHD2.
        [JsonProperty(PropertyName = "fork")]
        public string? Fork { get; set; }
    }
}
