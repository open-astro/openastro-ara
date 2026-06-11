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
            Assert.That(json["id"]!.Value<string>(), Is.Not.Empty, "every request gets a unique id");
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
