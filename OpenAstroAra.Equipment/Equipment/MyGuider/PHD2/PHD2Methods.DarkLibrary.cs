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

    // §63.6 (guider-e-4) — named-object RPC requests for the guider's dark-library / calibration-files surface.
    // openastro-guider accepts named OR positional params (API_REFERENCE.md); these use the documented
    // named-object form (doc/jsonrpc_api.md). guider-e-4a ships only the request shapes (serialization-locked);
    // the on-demand build + progress wiring is guider-e-4b. The defect-map RPCs (build_defect_map_darks /
    // set_defect_map_enabled) are the "advanced" half (§63.6), added as guider-e-4c (request/response shapes;
    // service + endpoint wiring is a follow-up, mirroring e-4b).

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

    // ── guider-e-4b: typed responses for the dark-library / calibration-files surface ──

    /// <summary>Result payload of <c>build_dark_library</c>: where the stack was written, how many frames per
    /// exposure, how many distinct exposures were captured, and the exposure durations (ms) covered.</summary>
    public class Phd2BuildDarkLibraryResult {

        [JsonProperty(PropertyName = "profile_id")]
        public int ProfileId { get; set; }

        [JsonProperty(PropertyName = "dark_library_path")]
        public string? DarkLibraryPath { get; set; }

        [JsonProperty(PropertyName = "frame_count")]
        public int FrameCount { get; set; }

        [JsonProperty(PropertyName = "exposure_count")]
        public int ExposureCount { get; set; }

        [JsonProperty(PropertyName = "exposures_ms")]
        public IReadOnlyList<int>? ExposuresMs { get; set; }
    }

    public class Phd2BuildDarkLibraryResponse : PhdMethodResponse {
        public Phd2BuildDarkLibraryResult? result { get; set; }
    }

    /// <summary>The <c>get_calibration_files_status</c> status object: dark-library / defect-map existence,
    /// compatibility, load state, auto-load flags, and (when a camera is connected and darks are loaded) the
    /// loaded dark count + exposure range. Defect-map fields are carried for completeness even though ARA does
    /// not yet surface defect maps (§63.6 deferred half).</summary>
    public class Phd2CalibrationFilesStatus {

        [JsonProperty(PropertyName = "profile_id")]
        public int ProfileId { get; set; }

        [JsonProperty(PropertyName = "dark_library_path")]
        public string? DarkLibraryPath { get; set; }

        [JsonProperty(PropertyName = "defect_map_path")]
        public string? DefectMapPath { get; set; }

        [JsonProperty(PropertyName = "dark_library_exists")]
        public bool DarkLibraryExists { get; set; }

        [JsonProperty(PropertyName = "defect_map_exists")]
        public bool DefectMapExists { get; set; }

        [JsonProperty(PropertyName = "dark_library_compatible")]
        public bool DarkLibraryCompatible { get; set; }

        [JsonProperty(PropertyName = "defect_map_compatible")]
        public bool DefectMapCompatible { get; set; }

        [JsonProperty(PropertyName = "dark_library_loaded")]
        public bool DarkLibraryLoaded { get; set; }

        [JsonProperty(PropertyName = "defect_map_loaded")]
        public bool DefectMapLoaded { get; set; }

        [JsonProperty(PropertyName = "auto_load_darks")]
        public bool AutoLoadDarks { get; set; }

        [JsonProperty(PropertyName = "auto_load_defect_map")]
        public bool AutoLoadDefectMap { get; set; }

        // Present only when a camera is connected and a dark library is loaded; null otherwise.
        [JsonProperty(PropertyName = "dark_count_loaded")]
        public int? DarkCountLoaded { get; set; }

        [JsonProperty(PropertyName = "dark_min_exposure_seconds_loaded")]
        public double? DarkMinExposureSecondsLoaded { get; set; }

        [JsonProperty(PropertyName = "dark_max_exposure_seconds_loaded")]
        public double? DarkMaxExposureSecondsLoaded { get; set; }
    }

    public class Phd2CalibrationFilesStatusResponse : PhdMethodResponse {
        public Phd2CalibrationFilesStatus? result { get; set; }
    }

    // ── §63.6 guider-e-4c: defect-map (bad-pixel) RPCs — the "advanced" half of the calibration surface ──
    // The daemon's handler list (doc/jsonrpc_api.md) exposes build_defect_map_darks + set_defect_map_enabled;
    // rebuild_defect_map appears only in the design reference (no handler yet) so it is intentionally omitted.

    /// <summary>
    /// <c>build_defect_map_darks {exposure_ms, frame_count, notes?, load_after}</c> — capture a master dark and
    /// build a bad-pixel (defect) map for the active profile (requires a connected camera + no active capture).
    /// Result: <c>{profile_id, defect_map_path, defect_count, exposure_ms, frame_count}</c>.
    /// </summary>
    public class Phd2BuildDefectMapDarks : Phd2Method<Phd2BuildDefectMapDarksParameter> {
        public override string Method => "build_defect_map_darks";
    }

    public class Phd2BuildDefectMapDarksParameter {

        // Daemon range 1..600000 ms, default 3000. Sent explicitly (default-initialized) so the wire is unambiguous.
        [JsonProperty(PropertyName = "exposure_ms")]
        public int ExposureMs { get; set; } = 3000;

        // Daemon range 1..50, default 10.
        [JsonProperty(PropertyName = "frame_count")]
        public int FrameCount { get; set; } = 10;

        [JsonProperty(PropertyName = "notes", NullValueHandling = NullValueHandling.Ignore)]
        public string? Notes { get; set; }

        // Daemon default true; sent explicitly so a fresh defect map is loaded for guiding right after the build.
        [JsonProperty(PropertyName = "load_after")]
        public bool LoadAfter { get; set; } = true;
    }

    /// <summary><c>set_defect_map_enabled {enabled}</c> — toggle bad-pixel correction (enabling needs a camera).
    /// Result: the same object as <c>get_calibration_files_status</c>.</summary>
    public class Phd2SetDefectMapEnabled : Phd2Method<Phd2SetDefectMapEnabledParameter> {
        public override string Method => "set_defect_map_enabled";
    }

    public class Phd2SetDefectMapEnabledParameter {

        [JsonProperty(PropertyName = "enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>Result payload of <c>build_defect_map_darks</c>: where the map was written, how many defects it
    /// flags, and the master-dark exposure (ms) + frame count it was built from.</summary>
    public class Phd2BuildDefectMapDarksResult {

        [JsonProperty(PropertyName = "profile_id")]
        public int ProfileId { get; set; }

        [JsonProperty(PropertyName = "defect_map_path")]
        public string? DefectMapPath { get; set; }

        [JsonProperty(PropertyName = "defect_count")]
        public int DefectCount { get; set; }

        [JsonProperty(PropertyName = "exposure_ms")]
        public int ExposureMs { get; set; }

        [JsonProperty(PropertyName = "frame_count")]
        public int FrameCount { get; set; }
    }

    public class Phd2BuildDefectMapDarksResponse : PhdMethodResponse {
        public Phd2BuildDefectMapDarksResult? result { get; set; }
    }
}
