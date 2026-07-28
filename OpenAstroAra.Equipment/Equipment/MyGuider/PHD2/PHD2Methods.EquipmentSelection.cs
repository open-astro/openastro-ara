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

    // §63.17 (PR 2) — the per-slot selection setters the daemon exposes beyond the camera ones already in the
    // base PHD2Methods.cs (Phd2SetSelectedCamera / Phd2SetSelectedCameraId / Phd2SetAlpacaServer). All are
    // blocked while equipment is connected (doc/jsonrpc_api.md), so the §63.5 push sends them inside its
    // disconnected window. Values are the daemon's own choice strings, verbatim from get_equipment_choices.
    // Same separate-UTF-8-partial pattern as PHD2Methods.Version — the base file is ISO-8859-1.

    /// <summary><c>set_selected_mount {mount}</c> — select the guide-output mount device.</summary>
    public class Phd2SetSelectedMount : Phd2Method<Phd2SetSelectedMountParameter> {
        public override string Method => "set_selected_mount";
    }

    public class Phd2SetSelectedMountParameter {

        [JsonProperty(PropertyName = "mount")]
        public string Mount { get; set; } = string.Empty;
    }

    /// <summary><c>set_selected_aux_mount {aux_mount}</c> — select the aux (pointing) mount device.</summary>
    public class Phd2SetSelectedAuxMount : Phd2Method<Phd2SetSelectedAuxMountParameter> {
        public override string Method => "set_selected_aux_mount";
    }

    public class Phd2SetSelectedAuxMountParameter {

        [JsonProperty(PropertyName = "aux_mount")]
        public string AuxMount { get; set; } = string.Empty;
    }

    /// <summary><c>set_selected_rotator {rotator}</c> — select the rotator device.</summary>
    public class Phd2SetSelectedRotator : Phd2Method<Phd2SetSelectedRotatorParameter> {
        public override string Method => "set_selected_rotator";
    }

    public class Phd2SetSelectedRotatorParameter {

        [JsonProperty(PropertyName = "rotator")]
        public string Rotator { get; set; } = string.Empty;
    }
}
