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

    // §63.6 (guider-e-4) — named-object RPC requests for the guider's dark-library / calibration-files surface.
    // openastro-guider accepts named OR positional params (API_REFERENCE.md); these use the documented
    // named-object form (doc/jsonrpc_api.md). guider-e-4a ships only the request shapes (serialization-locked);
    // the on-demand build + progress wiring is guider-e-4b. The defect-map RPCs (build_defect_map_darks /
    // set_defect_map_enabled) are the deferred "advanced" half (§63.6) — added when defect maps are surfaced.

    /// <summary>
    /// <c>build_dark_library {frame_count, min_exposure_ms?, max_exposure_ms?, clear_existing, notes?, load_after}</c>
    /// — capture a dark-frame stack for the active profile (requires a connected camera + no active capture).
    /// Result: <c>{profile_id, dark_library_path, frame_count, exposure_count, exposures_ms}</c>.
    /// </summary>
    public class Phd2BuildDarkLibrary : Phd2Method<Phd2BuildDarkLibraryParameter> {
        public override string Method => "build_dark_library";
    }

    public class Phd2BuildDarkLibraryParameter {

        // Daemon range 1..50, default 5. ARA sends it explicitly (default-initialized here) so the wire is
        // unambiguous; the optional exposure bounds + notes serialize only when set.
        [JsonProperty(PropertyName = "frame_count")]
        public int FrameCount { get; set; } = 5;

        [JsonProperty(PropertyName = "min_exposure_ms", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinExposureMs { get; set; }

        [JsonProperty(PropertyName = "max_exposure_ms", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxExposureMs { get; set; }

        [JsonProperty(PropertyName = "clear_existing")]
        public bool ClearExisting { get; set; }

        [JsonProperty(PropertyName = "notes", NullValueHandling = NullValueHandling.Ignore)]
        public string? Notes { get; set; }

        // Daemon default true; sent explicitly so a fresh library is loaded for guiding right after the build.
        [JsonProperty(PropertyName = "load_after")]
        public bool LoadAfter { get; set; } = true;
    }

    /// <summary><c>set_dark_library_enabled {enabled}</c> — toggle dark subtraction (enabling needs a camera).
    /// Result: the same object as <c>get_calibration_files_status</c>.</summary>
    public class Phd2SetDarkLibraryEnabled : Phd2Method<Phd2SetDarkLibraryEnabledParameter> {
        public override string Method => "set_dark_library_enabled";
    }

    public class Phd2SetDarkLibraryEnabledParameter {

        [JsonProperty(PropertyName = "enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary><c>get_calibration_files_status</c> (no params) — dark-library / defect-map existence, load
    /// state, paths, and (camera present) loaded dark count + exposure range.</summary>
    public class Phd2GetCalibrationFilesStatus : Phd2Method {
        public override string Method => "get_calibration_files_status";
    }

    /// <summary><c>delete_calibration_files {delete_dark_library?, delete_defect_map?}</c> — remove the stored
    /// calibration files (both default true). Result: the same object as <c>get_calibration_files_status</c>.</summary>
    public class Phd2DeleteCalibrationFiles : Phd2Method<Phd2DeleteCalibrationFilesParameter> {
        public override string Method => "delete_calibration_files";
    }

    public class Phd2DeleteCalibrationFilesParameter {

        // Both default true (the daemon's documented default = delete everything) and are always serialized, so
        // an all-defaults request sends explicit booleans rather than an ambiguous empty params object. Set one
        // false to keep that file.
        [JsonProperty(PropertyName = "delete_dark_library")]
        public bool DeleteDarkLibrary { get; set; } = true;

        [JsonProperty(PropertyName = "delete_defect_map")]
        public bool DeleteDefectMap { get; set; } = true;
    }
}
