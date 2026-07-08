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
    /// §45 — wire-shape coverage for the Static PA RPC family (openastro-guider
    /// design/POLAR_ALIGNMENT_DESIGN.md, API_REFERENCE.md "Static PA"): the five request
    /// shapes plus the shared status object all of start/measure/get_status/stop return.
    /// Serialization-locked like <see cref="Phd2PolarAlignMethodsTest"/>; the deserialization
    /// tests pin the nested result tree (calced sample) and the conditional-emission contract
    /// (a bare {active:false} leaves every optional null).
    /// </summary>
    [TestFixture]
    public class Phd2StaticPaMethodsTest {

        private static JObject Serialize(Phd2Method msg) =>
            JObject.Parse(JsonConvert.SerializeObject(msg));

        [Test]
        public void StaticPaStart_serializes_every_parameter() {
            var json = Serialize(new Phd2StaticPaStart {
                Parameters = new() {
                    Auto = true, Hemisphere = "north", RefStar = 2,
                    HourAngle = 1.5, FlipCamera = false,
                },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("staticpa_start"));
            var p = json["params"]!;
            Assert.That(p["auto"]!.Value<bool>(), Is.True);
            Assert.That(p["hemisphere"]!.Value<string>(), Is.EqualTo("north"));
            Assert.That(p["ref_star"]!.Value<int>(), Is.EqualTo(2));
            Assert.That(p["hour_angle"]!.Value<double>(), Is.EqualTo(1.5));
            Assert.That(p["flip_camera"]!.Value<bool>(), Is.False);
        }

        [Test]
        public void StaticPaStart_with_nothing_set_sends_an_empty_params_object() {
            // Every field is optional — omitting all of them lets the daemon pick its defaults
            // (auto off, current hemisphere, first ref star). None may leak onto the wire.
            var json = Serialize(new Phd2StaticPaStart { Parameters = new() });
            Assert.That(((JObject)json["params"]!).Count, Is.EqualTo(0));
        }

        [Test]
        public void StaticPaMeasure_sends_position_unconditionally() {
            var json = Serialize(new Phd2StaticPaMeasure { Parameters = new() { Position = 3 } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("staticpa_measure"));
            Assert.That(json["params"]!["position"]!.Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void GetStatus_stop_and_close_are_parameterless_methods() {
            Assert.That(Serialize(new Phd2StaticPaGetStatus())["method"]!.Value<string>(),
                Is.EqualTo("staticpa_get_status"));
            Assert.That(Serialize(new Phd2StaticPaStop())["method"]!.Value<string>(),
                Is.EqualTo("staticpa_stop"));

            var close = Serialize(new Phd2StaticPaClose());
            Assert.That(close["method"]!.Value<string>(), Is.EqualTo("staticpa_close"));
            Assert.That(close["params"], Is.Null, "a parameterless method carries no params node");
        }

        [Test]
        public void StatusResponse_deserializes_the_full_calced_result_tree() {
            const string wire = """
            {
              "jsonrpc": "2.0",
              "id": 7,
              "result": {
                "active": true,
                "aligning": false,
                "auto": true,
                "can_slew": true,
                "hemisphere": "north",
                "hour_angle": 1.5,
                "flip_camera": false,
                "pixel_scale": 3.76,
                "camera_angle": 91.2,
                "ref_star": 1,
                "ref_stars": [
                  { "index": 0, "name": "Polaris", "ra": 2.53, "dec": 89.26, "mag": 1.98 },
                  { "index": 1, "name": "HD 5914", "ra": 1.48, "dec": 86.25, "mag": 5.4 }
                ],
                "measured_points": [
                  { "position": 1, "x": 100.0, "y": 200.0 },
                  { "position": 2, "x": 150.0, "y": 210.0 }
                ],
                "rotation": {
                  "required_deg": 30.0, "rotated_deg": 12.5, "step": 1,
                  "required_steps": 3, "slewing": true
                },
                "calced": true,
                "centre": { "x": 512.0, "y": 384.0, "radius_px": 0.0 },
                "adjustment": {
                  "alt_error_px": 4.0, "az_error_px": 3.0, "total_error_px": 5.0,
                  "alt_error_arcmin": 15.0, "az_error_arcmin": 11.3, "total_error_arcmin": 18.8,
                  "az_vector": { "x": 1.0, "y": 0.0 },
                  "alt_vector": { "x": 0.0, "y": 1.0 }
                },
                "ref_star_target": { "x": 520.0, "y": 380.0 },
                "current_star": { "x": 516.0, "y": 383.0 },
                "live_adjustment": {
                  "alt_error_px": 1.0, "az_error_px": 4.0, "total_error_px": 4.12,
                  "alt_error_arcmin": 3.8, "az_error_arcmin": 15.0, "total_error_arcmin": 15.5,
                  "az_vector": { "x": 0.9, "y": 0.1 },
                  "alt_vector": { "x": 0.1, "y": 0.9 }
                }
              }
            }
            """;
            var r = JsonConvert.DeserializeObject<Phd2StaticPaStatusResponse>(wire)!;
            Assert.That(r.error, Is.Null);
            var s = r.result!;
            Assert.That(s.Active, Is.True);
            Assert.That(s.Auto, Is.True);
            Assert.That(s.CanSlew, Is.True);
            Assert.That(s.Hemisphere, Is.EqualTo("north"));
            Assert.That(s.HourAngle, Is.EqualTo(1.5));
            Assert.That(s.PixelScale, Is.EqualTo(3.76));
            Assert.That(s.CameraAngle, Is.EqualTo(91.2));
            Assert.That(s.RefStar, Is.EqualTo(1));

            Assert.That(s.RefStars, Has.Count.EqualTo(2));
            Assert.That(s.RefStars![1].Name, Is.EqualTo("HD 5914"));
            Assert.That(s.RefStars![0].Dec, Is.EqualTo(89.26));

            Assert.That(s.MeasuredPoints, Has.Count.EqualTo(2));
            Assert.That(s.MeasuredPoints![1].Position, Is.EqualTo(2));
            Assert.That(s.MeasuredPoints![1].X, Is.EqualTo(150.0));

            Assert.That(s.Rotation!.RequiredDeg, Is.EqualTo(30.0));
            Assert.That(s.Rotation!.RequiredSteps, Is.EqualTo(3));
            Assert.That(s.Rotation!.Slewing, Is.True);

            Assert.That(s.Calced, Is.True);
            Assert.That(s.Centre!.X, Is.EqualTo(512.0));
            Assert.That(s.Centre!.RadiusPx, Is.EqualTo(0.0));

            Assert.That(s.Adjustment!.TotalErrorPx, Is.EqualTo(5.0));
            Assert.That(s.Adjustment!.TotalErrorArcmin, Is.EqualTo(18.8));
            Assert.That(s.Adjustment!.AzVector!.X, Is.EqualTo(1.0));
            Assert.That(s.Adjustment!.AltVector!.Y, Is.EqualTo(1.0));

            Assert.That(s.RefStarTarget!.X, Is.EqualTo(520.0));
            Assert.That(s.CurrentStar!.Y, Is.EqualTo(383.0));
            Assert.That(s.LiveAdjustment!.AzErrorPx, Is.EqualTo(4.0));
            Assert.That(s.LiveAdjustment!.AltVector!.Y, Is.EqualTo(0.9));
        }

        [Test]
        public void StatusResponse_leaves_optionals_null_when_the_daemon_omits_them() {
            // Before start / when inactive the daemon short-circuits the object to just {active:false}.
            // Every nested/optional field must stay null rather than materializing an empty POCO.
            const string wire = """
            { "jsonrpc": "2.0", "id": 3, "result": { "active": false } }
            """;
            var r = JsonConvert.DeserializeObject<Phd2StaticPaStatusResponse>(wire)!;
            var s = r.result!;
            Assert.That(s.Active, Is.False);
            Assert.That(s.Calced, Is.Null);
            Assert.That(s.Hemisphere, Is.Null);
            Assert.That(s.RefStars, Is.Null);
            Assert.That(s.MeasuredPoints, Is.Null);
            Assert.That(s.Rotation, Is.Null);
            Assert.That(s.Centre, Is.Null);
            Assert.That(s.Adjustment, Is.Null);
            Assert.That(s.RefStarTarget, Is.Null);
            Assert.That(s.CurrentStar, Is.Null);
            Assert.That(s.LiveAdjustment, Is.Null);
        }

        [Test]
        public void Close_result_deserializes_as_the_generic_zero_ack() {
            const string wire = """
            { "jsonrpc": "2.0", "id": 9, "result": 0 }
            """;
            var r = JsonConvert.DeserializeObject<GenericPhdMethodResponse>(wire)!;
            Assert.That(r.error, Is.Null);
            Assert.That(r.result, Is.Not.Null);
        }
    }
}
