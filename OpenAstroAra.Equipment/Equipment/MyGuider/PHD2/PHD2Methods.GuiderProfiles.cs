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

    // §63.4 (guider-e-3) — named-object RPC requests for the per-ARA-profile ↔ PHD2-profile mapping.
    // openastro-guider accepts named OR positional params (API_REFERENCE.md); these use the documented
    // named-object form (doc/jsonrpc_api.md). The inherited PHD2Methods.cs already covers id-based
    // selection (`set_profile` {id}) and listing (`get_profiles`); these add by-name select + create so
    // ARA can map an ARA profile to its `ara-<slug>` PHD2 profile, creating it on first connect.

    /// <summary><c>set_profile_by_name {name}</c> — select an existing PHD2 profile by name (result <c>0</c>).</summary>
    public class Phd2SetProfileByName : Phd2Method<Phd2SetProfileByNameParameter> {
        public override string Method => "set_profile_by_name";
    }

    public class Phd2SetProfileByNameParameter {

        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// <c>create_profile {name, copy_from?|copy_from_id?, select?}</c> — create (and by default select) a new
    /// PHD2 profile; result <c>{id,name,selected}</c>. ARA creates its <c>ara-&lt;slug&gt;</c> profile fresh
    /// (no clone source) and lets the §63.5 push (guider-e-2) populate it, so <c>copy_from</c>/<c>copy_from_id</c>
    /// stay unset (and serialize only when set, since the guider treats them as mutually exclusive).
    /// </summary>
    public class Phd2CreateProfile : Phd2Method<Phd2CreateProfileParameter> {
        public override string Method => "create_profile";
    }

    public class Phd2CreateProfileParameter {

        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "copy_from", NullValueHandling = NullValueHandling.Ignore)]
        public string? CopyFrom { get; set; }

        [JsonProperty(PropertyName = "copy_from_id", NullValueHandling = NullValueHandling.Ignore)]
        public int? CopyFromId { get; set; }

        // The guider defaults select to true; ARA always wants the newly-created profile active (so the
        // §63.5 push lands in it), so send it explicitly rather than relying on the daemon's default.
        [JsonProperty(PropertyName = "select")]
        public bool Select { get; set; } = true;
    }

    /// <summary>
    /// <c>delete_profile {name, delete_dark_files}</c> — remove a PHD2 profile by name (result <c>0</c>).
    /// The §63.4 delete hook fires this when its ARA profile is deleted, with
    /// <c>delete_dark_files=true</c> per the playbook table so the orphaned rig's dark library and defect
    /// map go with it. The daemon rejects deleting its SELECTED profile — and because the selected twin
    /// tracks the last guider CONNECT (an ARA profile switch alone doesn't re-map it), the caller checks
    /// <c>SelectedProfile</c> and skips that case rather than sending a doomed RPC.
    /// </summary>
    public class Phd2DeleteProfile : Phd2Method<Phd2DeleteProfileParameter> {
        public override string Method => "delete_profile";
    }

    public class Phd2DeleteProfileParameter {

        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "delete_dark_files")]
        public bool DeleteDarkFiles { get; set; } = true;
    }
}
