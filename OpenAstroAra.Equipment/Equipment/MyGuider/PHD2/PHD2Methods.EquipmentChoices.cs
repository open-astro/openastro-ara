#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using Newtonsoft.Json;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §63.17 (PR 1) — named-object RPC requests for the guider's equipment-selection surface: what devices the
    // daemon can offer (get_equipment_choices) and which Alpaca servers it can see on the network
    // (discover_alpaca_servers). Same separate-UTF-8-partial pattern as PHD2Methods.Version — the base
    // PHD2Methods.cs is ISO-8859-1 and is not extended in place.

    /// <summary><c>get_equipment_choices</c> (no params) — the device names the daemon can offer per slot.
    /// Result: <c>{camera: string[], mount: string[], aux_mount: string[], AO: string[], rotator: string[]}</c>.</summary>
    public class Phd2GetEquipmentChoices : Phd2Method {
        public override string Method => "get_equipment_choices";
    }

    public class Phd2GetEquipmentChoicesResponse : PhdMethodResponse {
        public Phd2EquipmentChoices? result { get; set; }
    }

    /// <summary>The <c>get_equipment_choices</c> result: per-slot device-name lists exactly as the daemon's own
    /// equipment dialog would offer them. Values are the daemon's display strings and are passed back verbatim
    /// to the <c>set_selected_*</c> setters (§63.17 PR 2).</summary>
    public class Phd2EquipmentChoices {

        [JsonProperty(PropertyName = "camera")]
        public IReadOnlyList<string>? Camera { get; set; }

        [JsonProperty(PropertyName = "mount")]
        public IReadOnlyList<string>? Mount { get; set; }

        [JsonProperty(PropertyName = "aux_mount")]
        public IReadOnlyList<string>? AuxMount { get; set; }

        // The daemon serializes this slot's key in caps ("AO"), unlike the other snake_case keys.
        [JsonProperty(PropertyName = "AO")]
        public IReadOnlyList<string>? AdaptiveOptics { get; set; }

        [JsonProperty(PropertyName = "rotator")]
        public IReadOnlyList<string>? Rotator { get; set; }
    }

    /// <summary><c>discover_alpaca_servers {num_queries?, timeout_seconds?}</c> — UDP-discover Alpaca servers
    /// on the daemon's network (blocking for roughly <c>num_queries × timeout_seconds</c>). Result: an array of
    /// <c>"host:port"</c> strings. Returns an app error when the daemon build lacks Alpaca support.</summary>
    public class Phd2DiscoverAlpacaServers : Phd2Method<Phd2DiscoverAlpacaServersParameter> {
        public override string Method => "discover_alpaca_servers";
    }

    public class Phd2DiscoverAlpacaServersParameter {

        // Daemon range 1..20, default 2; serialized only when set so an all-defaults request stays daemon-defaulted.
        [JsonProperty(PropertyName = "num_queries", NullValueHandling = NullValueHandling.Ignore)]
        public int? NumQueries { get; set; }

        // Daemon range 1..30 s, default 2 s; serialized only when set.
        [JsonProperty(PropertyName = "timeout_seconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? TimeoutSeconds { get; set; }
    }

    public class Phd2DiscoverAlpacaServersResponse : PhdMethodResponse {
        public IReadOnlyList<string>? result { get; set; }
    }

    /// <summary><c>get_alpaca_camera_pixelsize {host?, port?, device_number?}</c> — read a camera's sensor
    /// pixel size (µm) straight from its Alpaca driver (<c>camera/N/pixelsizex</c>, falling back to
    /// <c>pixelsizey</c>). Omitted params default to the daemon profile's stored Alpaca camera. Result:
    /// <c>{pixel_size, host, port, device_number}</c>; an app error when the camera is unreachable or the
    /// driver reports no usable size. Feeds the setup wizard's pixel-size autofill (§63.20).</summary>
    public class Phd2GetAlpacaCameraPixelSize : Phd2Method<Phd2GetAlpacaCameraPixelSizeParameter> {
        public override string Method => "get_alpaca_camera_pixelsize";
    }

    public class Phd2GetAlpacaCameraPixelSizeParameter {

        [JsonProperty(PropertyName = "host", NullValueHandling = NullValueHandling.Ignore)]
        public string? Host { get; set; }

        [JsonProperty(PropertyName = "port", NullValueHandling = NullValueHandling.Ignore)]
        public int? Port { get; set; }

        [JsonProperty(PropertyName = "device_number", NullValueHandling = NullValueHandling.Ignore)]
        public int? DeviceNumber { get; set; }
    }

    public class Phd2GetAlpacaCameraPixelSizeResponse : PhdMethodResponse {
        public Phd2AlpacaCameraPixelSize? result { get; set; }
    }

    public class Phd2AlpacaCameraPixelSize {

        [JsonProperty(PropertyName = "pixel_size")]
        public double PixelSize { get; set; }
    }
}
