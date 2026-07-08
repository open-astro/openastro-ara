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
    /// §45 — wire-shape coverage for the Drift Align RPC family (openastro-guider src/drift_tool.cpp +
    /// event_server.cpp driftalign_* handlers): the six request shapes (only <c>set_phase</c> takes a
    /// param) plus the status object that start/set_phase/drift/adjust/get_status all return.
    /// Serialization-locked like <see cref="Phd2StaticPaMethodsTest"/>; the deserialization tests pin the
    /// full status tree (message + alignment error + star/lock points + scope) and the conditional-emission
    /// contract (a bare {active:false} leaves every optional null).
    /// </summary>
    [TestFixture]
    public class Phd2DriftAlignMethodsTest {

        private static JObject Serialize(Phd2Method msg) =>
            JObject.Parse(JsonConvert.SerializeObject(msg));

        [Test]
        public void SetPhase_sends_the_axis_unconditionally() {
            var json = Serialize(new Phd2DriftAlignSetPhase { Parameters = new() { Phase = "altitude" } });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("driftalign_set_phase"));
            Assert.That(json["params"]!["phase"]!.Value<string>(), Is.EqualTo("altitude"));
        }

        [Test]
        public void Start_drift_adjust_get_status_and_close_are_parameterless_methods() {
            Assert.That(Serialize(new Phd2DriftAlignStart())["method"]!.Value<string>(),
                Is.EqualTo("driftalign_start"));
            Assert.That(Serialize(new Phd2DriftAlignDrift())["method"]!.Value<string>(),
                Is.EqualTo("driftalign_drift"));
            Assert.That(Serialize(new Phd2DriftAlignAdjust())["method"]!.Value<string>(),
                Is.EqualTo("driftalign_adjust"));
            Assert.That(Serialize(new Phd2DriftAlignGetStatus())["method"]!.Value<string>(),
                Is.EqualTo("driftalign_get_status"));

            var close = Serialize(new Phd2DriftAlignClose());
            Assert.That(close["method"]!.Value<string>(), Is.EqualTo("driftalign_close"));
            Assert.That(close["params"], Is.Null, "a parameterless method carries no params node");
            // driftalign_start carries no params node either.
            Assert.That(Serialize(new Phd2DriftAlignStart())["params"], Is.Null);
        }

        [Test]
        public void StatusResponse_deserializes_the_full_status_tree() {
            const string wire = """
            {
              "jsonrpc": "2.0",
              "id": 6,
              "result": {
                "active": true,
                "phase": "azimuth",
                "mode": "drift",
                "drifting": true,
                "can_slew": true,
                "slewing": false,
                "calibrated": true,
                "guiding": true,
                "status_message": "drifting on azimuth",
                "polar_alignment_error": {
                  "error_arcmin": 3.4,
                  "dec_drift_arcsec_per_min": 1.25,
                  "samples": 40
                },
                "current_star": { "x": 640.0, "y": 480.0 },
                "lock_position": { "x": 636.0, "y": 476.0 },
                "scope": {
                  "ra_hours": 5.5877,
                  "dec_degrees": -5.389,
                  "lst_hours": 6.1234
                }
              }
            }
            """;
            var r = JsonConvert.DeserializeObject<Phd2DriftAlignStatusResponse>(wire)!;
            Assert.That(r.error, Is.Null);
            var s = r.result!;
            Assert.That(s.Active, Is.True);
            Assert.That(s.Phase, Is.EqualTo("azimuth"));
            Assert.That(s.Mode, Is.EqualTo("drift"));
            Assert.That(s.Drifting, Is.True);
            Assert.That(s.CanSlew, Is.True);
            Assert.That(s.Slewing, Is.False);
            Assert.That(s.Calibrated, Is.True);
            Assert.That(s.Guiding, Is.True);
            Assert.That(s.StatusMessage, Is.EqualTo("drifting on azimuth"));

            Assert.That(s.PolarAlignmentError!.ErrorArcmin, Is.EqualTo(3.4));
            Assert.That(s.PolarAlignmentError!.DecDriftArcsecPerMin, Is.EqualTo(1.25));
            Assert.That(s.PolarAlignmentError!.Samples, Is.EqualTo(40));

            Assert.That(s.CurrentStar!.X, Is.EqualTo(640.0));
            Assert.That(s.CurrentStar!.Y, Is.EqualTo(480.0));
            Assert.That(s.LockPosition!.X, Is.EqualTo(636.0));
            Assert.That(s.LockPosition!.Y, Is.EqualTo(476.0));

            Assert.That(s.Scope!.RaHours, Is.EqualTo(5.5877));
            Assert.That(s.Scope!.DecDegrees, Is.EqualTo(-5.389));
            Assert.That(s.Scope!.LstHours, Is.EqualTo(6.1234));
        }

        [Test]
        public void StatusResponse_leaves_optionals_null_when_the_daemon_omits_them() {
            // Before start / when inactive the daemon short-circuits the object to just {active:false}.
            // Every scalar, the two nested read objects, and both {x,y} points must stay null.
            const string wire = """
            { "jsonrpc": "2.0", "id": 1, "result": { "active": false } }
            """;
            var r = JsonConvert.DeserializeObject<Phd2DriftAlignStatusResponse>(wire)!;
            var s = r.result!;
            Assert.That(s.Active, Is.False);
            Assert.That(s.Phase, Is.Null);
            Assert.That(s.Mode, Is.Null);
            Assert.That(s.Drifting, Is.Null);
            Assert.That(s.Calibrated, Is.Null);
            Assert.That(s.Guiding, Is.Null);
            Assert.That(s.StatusMessage, Is.Null);
            Assert.That(s.PolarAlignmentError, Is.Null);
            Assert.That(s.CurrentStar, Is.Null);
            Assert.That(s.LockPosition, Is.Null);
            Assert.That(s.Scope, Is.Null);
        }

        [Test]
        public void StatusResponse_active_but_pre_solution_omits_the_gated_reads() {
            // active with the flag block present but no computed error / no valid star / no scope: the
            // header booleans deserialize while the gated sub-objects stay null.
            const string wire = """
            {
              "jsonrpc": "2.0",
              "id": 7,
              "result": {
                "active": true, "phase": "altitude", "mode": "adjust",
                "drifting": false, "can_slew": false, "slewing": false,
                "calibrated": false, "guiding": false
              }
            }
            """;
            var s = JsonConvert.DeserializeObject<Phd2DriftAlignStatusResponse>(wire)!.result!;
            Assert.That(s.Active, Is.True);
            Assert.That(s.Phase, Is.EqualTo("altitude"));
            Assert.That(s.Mode, Is.EqualTo("adjust"));
            Assert.That(s.StatusMessage, Is.Null);
            Assert.That(s.PolarAlignmentError, Is.Null);
            Assert.That(s.CurrentStar, Is.Null);
            Assert.That(s.LockPosition, Is.Null);
            Assert.That(s.Scope, Is.Null);
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
