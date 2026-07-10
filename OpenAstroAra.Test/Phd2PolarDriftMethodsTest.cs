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
    /// §45 — wire-shape coverage for the Polar Drift RPC family (openastro-guider
    /// src/polardrift_toolwin.cpp + event_server.cpp polardrift_* handlers): the four request
    /// shapes plus the flat status object that start/get_status/stop all return. Serialization-locked
    /// like <see cref="Phd2StaticPaMethodsTest"/>; the deserialization tests pin the full drift solution
    /// and the conditional-emission contract (a bare {active:false} leaves every optional null).
    /// </summary>
    [TestFixture]
    public class Phd2PolarDriftMethodsTest {

        private static JObject Serialize(Phd2Method msg) =>
            JObject.Parse(JsonConvert.SerializeObject(msg));

        [Test]
        public void PolarDriftStart_serializes_both_optional_params() {
            var json = Serialize(new Phd2PolarDriftStart {
                Parameters = new() { Hemisphere = "south", Mirrored = true },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("polardrift_start"));
            Assert.That(json["params"]!["hemisphere"]!.Value<string>(), Is.EqualTo("south"));
            Assert.That(json["params"]!["mirrored"]!.Value<bool>(), Is.True);
        }

        [Test]
        public void PolarDriftStart_with_nothing_set_sends_an_empty_params_object() {
            // Both params are optional — omitting them keeps the tool's current/profile hemisphere and
            // mirror flag. Neither may leak onto the wire.
            var json = Serialize(new Phd2PolarDriftStart { Parameters = new() });
            Assert.That(((JObject)json["params"]!).Count, Is.EqualTo(0));
        }

        [Test]
        public void GetStatus_stop_and_close_are_parameterless_methods() {
            Assert.That(Serialize(new Phd2PolarDriftGetStatus())["method"]!.Value<string>(),
                Is.EqualTo("polardrift_get_status"));
            Assert.That(Serialize(new Phd2PolarDriftStop())["method"]!.Value<string>(),
                Is.EqualTo("polardrift_stop"));

            var close = Serialize(new Phd2PolarDriftClose());
            Assert.That(close["method"]!.Value<string>(), Is.EqualTo("polardrift_close"));
            Assert.That(close["params"], Is.Null, "a parameterless method carries no params node");
        }

        [Test]
        public void StatusResponse_deserializes_the_full_drift_solution() {
            const string wire = """
            {
              "jsonrpc": "2.0",
              "id": 4,
              "result": {
                "active": true,
                "drifting": true,
                "hemisphere": "north",
                "mirrored": false,
                "pixel_scale": 3.76,
                "num_samples": 12,
                "elapsed_s": 45.5,
                "offset_px": 8.4,
                "error_arcmin": 2.1,
                "pole_direction_deg": 137.0,
                "current_star": { "x": 512.0, "y": 384.0 },
                "target": { "x": 508.0, "y": 380.0 }
              }
            }
            """;
            var r = JsonConvert.DeserializeObject<Phd2PolarDriftStatusResponse>(wire)!;
            Assert.That(r.error, Is.Null);
            var s = r.result!;
            Assert.That(s.Active, Is.True);
            Assert.That(s.Drifting, Is.True);
            Assert.That(s.Hemisphere, Is.EqualTo("north"));
            Assert.That(s.Mirrored, Is.False);
            Assert.That(s.PixelScale, Is.EqualTo(3.76));
            Assert.That(s.NumSamples, Is.EqualTo(12));
            Assert.That(s.ElapsedS, Is.EqualTo(45.5));
            Assert.That(s.OffsetPx, Is.EqualTo(8.4));
            Assert.That(s.ErrorArcmin, Is.EqualTo(2.1));
            Assert.That(s.PoleDirectionDeg, Is.EqualTo(137.0));
            Assert.That(s.CurrentStar!.X, Is.EqualTo(512.0));
            Assert.That(s.CurrentStar!.Y, Is.EqualTo(384.0));
            Assert.That(s.Target!.X, Is.EqualTo(508.0));
            Assert.That(s.Target!.Y, Is.EqualTo(380.0));
        }

        [Test]
        public void StatusResponse_leaves_optionals_null_when_the_daemon_omits_them() {
            // Before start / when inactive the daemon short-circuits the object to just {active:false}.
            // The drift solution and both {x,y} points must stay null, not materialize as empty POCOs.
            const string wire = """
            { "jsonrpc": "2.0", "id": 2, "result": { "active": false } }
            """;
            var r = JsonConvert.DeserializeObject<Phd2PolarDriftStatusResponse>(wire)!;
            var s = r.result!;
            Assert.That(s.Active, Is.False);
            Assert.That(s.Drifting, Is.Null);
            Assert.That(s.Hemisphere, Is.Null);
            Assert.That(s.NumSamples, Is.Null);
            Assert.That(s.ElapsedS, Is.Null);
            Assert.That(s.OffsetPx, Is.Null);
            Assert.That(s.ErrorArcmin, Is.Null);
            Assert.That(s.PoleDirectionDeg, Is.Null);
            Assert.That(s.CurrentStar, Is.Null);
            Assert.That(s.Target, Is.Null);
        }

        [Test]
        public void StatusResponse_before_two_samples_has_the_header_but_no_solution() {
            // active + drifting with num_samples <= 1: the daemon emits the header fields but withholds
            // offset/error/direction/star/target until it has enough samples to fit a drift.
            const string wire = """
            {
              "jsonrpc": "2.0",
              "id": 5,
              "result": {
                "active": true, "drifting": true, "hemisphere": "north",
                "mirrored": false, "pixel_scale": 3.76, "num_samples": 1, "elapsed_s": 3.0
              }
            }
            """;
            var s = JsonConvert.DeserializeObject<Phd2PolarDriftStatusResponse>(wire)!.result!;
            Assert.That(s.NumSamples, Is.EqualTo(1));
            Assert.That(s.ElapsedS, Is.EqualTo(3.0));
            Assert.That(s.OffsetPx, Is.Null, "no drift fit until num_samples > 1");
            Assert.That(s.ErrorArcmin, Is.Null);
            Assert.That(s.CurrentStar, Is.Null);
            Assert.That(s.Target, Is.Null);
        }

        [Test]
        public void Close_result_deserializes_as_the_generic_zero_ack() {
            const string wire = """
            { "jsonrpc": "2.0", "id": 8, "result": 0 }
            """;
            var r = JsonConvert.DeserializeObject<GenericPhdMethodResponse>(wire)!;
            Assert.That(r.error, Is.Null);
            Assert.That(r.result, Is.Not.Null);
        }
    }
}
