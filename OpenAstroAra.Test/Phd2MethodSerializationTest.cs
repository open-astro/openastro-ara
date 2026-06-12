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
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.5 guider-e-1 — wire-shape coverage for the named-object RPC setters. openastro-guider
    /// accepts named OR positional params (API_REFERENCE.md); these assert the documented named-object
    /// form so the §63.5 profile-push (guider-e-2) builds the right JSON. Every request carries a
    /// top-level <c>method</c> + <c>id</c> (from the <see cref="Phd2Method"/> base) and a named
    /// <c>params</c> object.
    /// </summary>
    [TestFixture]
    public class Phd2MethodSerializationTest {

        private static JObject Serialize(Phd2Method msg) =>
            JObject.Parse(JsonConvert.SerializeObject(msg));

        [Test]
        public void Base_envelope_carries_method_and_id() {
            var json = Serialize(new Phd2SetSelectedCamera { Parameters = new() { Camera = "ZWO ASI120MM" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_selected_camera"));
            Assert.That(System.Guid.TryParse(json["id"]!.Value<string>(), out _), Is.True,
                "every request gets a unique GUID id");
        }

        [Test]
        public void SetSelectedCamera_uses_named_camera_param() {
            var json = Serialize(new Phd2SetSelectedCamera { Parameters = new() { Camera = "ZWO ASI120MM" } });
            Assert.That(json["params"]!["camera"]!.Value<string>(), Is.EqualTo("ZWO ASI120MM"));
        }

        [Test]
        public void SetSelectedCameraId_uses_named_camera_id_param() {
            var json = Serialize(new Phd2SetSelectedCameraId { Parameters = new() { CameraId = "cam-7" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_selected_camera_id"));
            Assert.That(json["params"]!["camera_id"]!.Value<string>(), Is.EqualTo("cam-7"));
        }

        [Test]
        public void SetAlgoParam_uses_axis_name_value() {
            var json = Serialize(new Phd2SetAlgoParam {
                Parameters = new() { Axis = "ra", Name = "aggressiveness", Value = 0.75 },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_algo_param"));
            var p = json["params"]!;
            Assert.That(p["axis"]!.Value<string>(), Is.EqualTo("ra"));
            Assert.That(p["name"]!.Value<string>(), Is.EqualTo("aggressiveness"));
            Assert.That(p["value"]!.Value<double>(), Is.EqualTo(0.75));
        }

        [Test]
        public void SetDecGuideMode_uses_named_mode_param() {
            var json = Serialize(new Phd2SetDecGuideMode { Parameters = new() { Mode = "Auto" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_dec_guide_mode"));
            Assert.That(json["params"]!["mode"]!.Value<string>(), Is.EqualTo("Auto"));
        }

        [Test]
        public void SetAlpacaServer_serializes_only_the_fields_that_are_set() {
            var json = Serialize(new Phd2SetAlpacaServer {
                Parameters = new() { Host = "192.168.1.50", Port = 11111, CameraDevice = 0 },
            });
            var p = (JObject)json["params"]!;
            Assert.That(p["host"]!.Value<string>(), Is.EqualTo("192.168.1.50"));
            Assert.That(p["port"]!.Value<int>(), Is.EqualTo(11111));
            Assert.That(p["camera_device"]!.Value<int>(), Is.EqualTo(0));
            // Unset subset fields must NOT be pushed (set_alpaca_server takes "any subset").
            Assert.That(p.ContainsKey("telescope_device"), Is.False);
            Assert.That(p.ContainsKey("rotator_device"), Is.False);
        }

        [Test]
        public void SetProfileSetup_serializes_only_the_fields_that_are_set() {
            var json = Serialize(new Phd2SetProfileSetup {
                Parameters = new() { FocalLength = 250, PixelSize = 2.9 },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_profile_setup"));
            var p = (JObject)json["params"]!;
            Assert.That(p["focal_length"]!.Value<int>(), Is.EqualTo(250));
            Assert.That(p["pixel_size"]!.Value<double>(), Is.EqualTo(2.9));
            // Subset semantics: unset fields are omitted so they aren't overwritten on the guider.
            Assert.That(p.ContainsKey("camera_binning"), Is.False);
            Assert.That(p.ContainsKey("guide_speed"), Is.False);
            Assert.That(p.ContainsKey("calibration_duration"), Is.False);
        }

        [Test]
        public void SetProfileByName_uses_named_name_param() {
            var json = Serialize(new Phd2SetProfileByName { Parameters = new() { Name = "ara-c14-cem120" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_profile_by_name"));
            Assert.That(json["params"]!["name"]!.Value<string>(), Is.EqualTo("ara-c14-cem120"));
        }

        [Test]
        public void CreateProfile_sends_name_and_explicit_select_true_no_clone_source() {
            var json = Serialize(new Phd2CreateProfile { Parameters = new() { Name = "ara-redcat-heq5" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("create_profile"));
            var p = (JObject)json["params"]!;
            Assert.That(p["name"]!.Value<string>(), Is.EqualTo("ara-redcat-heq5"));
            // ARA always selects the new profile (so the §63.5 push lands in it) — sent explicitly.
            Assert.That(p["select"]!.Value<bool>(), Is.True);
            // ARA creates fresh (no clone source); the mutually-exclusive copy_from/copy_from_id are omitted.
            Assert.That(p.ContainsKey("copy_from"), Is.False);
            Assert.That(p.ContainsKey("copy_from_id"), Is.False);
        }

        [Test]
        public void CreateProfile_emits_clone_source_only_when_set() {
            var json = Serialize(new Phd2CreateProfile {
                Parameters = new() { Name = "ara-clone", CopyFrom = "Default", Select = false },
            });
            var p = (JObject)json["params"]!;
            Assert.That(p["copy_from"]!.Value<string>(), Is.EqualTo("Default"));
            Assert.That(p["select"]!.Value<bool>(), Is.False);
            Assert.That(p.ContainsKey("copy_from_id"), Is.False);
        }

        [Test]
        public void BuildDarkLibrary_sends_explicit_count_clear_and_load_omits_unset_bounds() {
            var json = Serialize(new Phd2BuildDarkLibrary {
                Parameters = new() { FrameCount = 10, ClearExisting = true },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("build_dark_library"));
            var p = (JObject)json["params"]!;
            Assert.That(p["frame_count"]!.Value<int>(), Is.EqualTo(10));
            Assert.That(p["clear_existing"]!.Value<bool>(), Is.True);
            Assert.That(p["load_after"]!.Value<bool>(), Is.True, "default true is sent explicitly");
            // Optional bounds + notes omitted when unset.
            Assert.That(p.ContainsKey("min_exposure_ms"), Is.False);
            Assert.That(p.ContainsKey("max_exposure_ms"), Is.False);
            Assert.That(p.ContainsKey("notes"), Is.False);
        }

        [Test]
        public void BuildDarkLibrary_emits_exposure_bounds_and_notes_when_set() {
            var json = Serialize(new Phd2BuildDarkLibrary {
                Parameters = new() { MinExposureMs = 1000, MaxExposureMs = 300000, Notes = "ARA wizard", LoadAfter = false },
            });
            var p = (JObject)json["params"]!;
            Assert.That(p["min_exposure_ms"]!.Value<int>(), Is.EqualTo(1000));
            Assert.That(p["max_exposure_ms"]!.Value<int>(), Is.EqualTo(300000));
            Assert.That(p["notes"]!.Value<string>(), Is.EqualTo("ARA wizard"));
            Assert.That(p["load_after"]!.Value<bool>(), Is.False);
            Assert.That(p["frame_count"]!.Value<int>(), Is.EqualTo(5), "default frame_count");
        }

        [Test]
        public void SetDarkLibraryEnabled_uses_named_enabled_param() {
            var json = Serialize(new Phd2SetDarkLibraryEnabled { Parameters = new() { Enabled = true } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_dark_library_enabled"));
            Assert.That(json["params"]!["enabled"]!.Value<bool>(), Is.True);
        }

        [Test]
        public void GetCalibrationFilesStatus_carries_method_and_id_no_params() {
            var json = Serialize(new Phd2GetCalibrationFilesStatus());
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("get_calibration_files_status"));
            Assert.That(System.Guid.TryParse(json["id"]!.Value<string>(), out _), Is.True);
            Assert.That(json.ContainsKey("params"), Is.False, "the no-param base carries no params object");
        }

        [Test]
        public void DeleteCalibrationFiles_sends_both_flags_explicitly_no_empty_params() {
            // Both default true (daemon default = delete everything); both always serialize so the request is
            // never an ambiguous empty params object. Set one false to keep that file.
            var json = Serialize(new Phd2DeleteCalibrationFiles { Parameters = new() { DeleteDefectMap = false } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("delete_calibration_files"));
            var p = (JObject)json["params"]!;
            Assert.That(p["delete_dark_library"]!.Value<bool>(), Is.True, "default true");
            Assert.That(p["delete_defect_map"]!.Value<bool>(), Is.False);
        }

        [Test]
        public void BuildDefectMapDarks_sends_explicit_exposure_frame_count_and_load_omits_unset_notes() {
            var json = Serialize(new Phd2BuildDefectMapDarks {
                Parameters = new() { ExposureMs = 5000, FrameCount = 20 },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("build_defect_map_darks"));
            var p = (JObject)json["params"]!;
            Assert.That(p["exposure_ms"]!.Value<int>(), Is.EqualTo(5000));
            Assert.That(p["frame_count"]!.Value<int>(), Is.EqualTo(20));
            Assert.That(p["load_after"]!.Value<bool>(), Is.True, "default true is sent explicitly");
            Assert.That(p.ContainsKey("notes"), Is.False);
        }

        [Test]
        public void BuildDefectMapDarks_defaults_match_the_daemon_and_emit_notes_when_set() {
            var json = Serialize(new Phd2BuildDefectMapDarks {
                Parameters = new() { Notes = "ARA wizard", LoadAfter = false },
            });
            var p = (JObject)json["params"]!;
            Assert.That(p["exposure_ms"]!.Value<int>(), Is.EqualTo(3000), "default exposure_ms");
            Assert.That(p["frame_count"]!.Value<int>(), Is.EqualTo(10), "default frame_count");
            Assert.That(p["notes"]!.Value<string>(), Is.EqualTo("ARA wizard"));
            Assert.That(p["load_after"]!.Value<bool>(), Is.False);
        }

        [Test]
        public void SetDefectMapEnabled_uses_named_enabled_param() {
            var json = Serialize(new Phd2SetDefectMapEnabled { Parameters = new() { Enabled = true } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("set_defect_map_enabled"));
            Assert.That(json["params"]!["enabled"]!.Value<bool>(), Is.True);
        }

        [Test]
        public void SetDefectMapEnabled_serializes_the_false_path_too() {
            var json = Serialize(new Phd2SetDefectMapEnabled { Parameters = new() { Enabled = false } });
            Assert.That(json["params"]!["enabled"]!.Value<bool>(), Is.False, "disable must serialize explicit false, not omit");
        }

        [Test]
        public void BuildDefectMapDarks_result_deserializes_from_daemon_payload() {
            const string raw = """
                {"jsonrpc":"2.0","id":"d1","result":{"profile_id":3,"defect_map_path":"/maps/ara-rig.bpm",
                "defect_count":142,"exposure_ms":3000,"frame_count":10}}
                """;
            var response = JsonConvert.DeserializeObject<Phd2BuildDefectMapDarksResponse>(raw);
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.error, Is.Null);
            var result = response.result!;
            Assert.That(result.ProfileId, Is.EqualTo(3));
            Assert.That(result.DefectMapPath, Is.EqualTo("/maps/ara-rig.bpm"));
            Assert.That(result.DefectCount, Is.EqualTo(142));
            Assert.That(result.ExposureMs, Is.EqualTo(3000));
            Assert.That(result.FrameCount, Is.EqualTo(10));
        }

        [Test]
        public void SetProfileSetup_calibration_and_binning_fields_use_guider_names() {
            // Lock the calibration_* names against the authoritative guider contract
            // (openastro-guider/doc/jsonrpc_api.md: calibration_duration 50..60000, calibration_distance
            // 5..200 — NOT the playbook's stale PHD2-era `calibration_step_ms`).
            var json = Serialize(new Phd2SetProfileSetup {
                Parameters = new() {
                    CameraBinning = 1,
                    GuideSpeed = 0.5,
                    CalibrationDuration = 750,
                    CalibrationDistance = 25,
                },
            });
            var p = (JObject)json["params"]!;
            Assert.That(p["camera_binning"]!.Value<int>(), Is.EqualTo(1));
            Assert.That(p["guide_speed"]!.Value<double>(), Is.EqualTo(0.5));
            Assert.That(p["calibration_duration"]!.Value<int>(), Is.EqualTo(750));
            Assert.That(p["calibration_distance"]!.Value<int>(), Is.EqualTo(25));
        }
    }
}
