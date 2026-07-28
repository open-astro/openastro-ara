#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.17 (PR 1) — the send-site validation guarding <c>discover_alpaca_servers</c>, the discovery
    /// receive-timeout derivation, and deserialization of the daemon's equipment-choices / discovery payloads.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderEquipmentChoicesTest {

        private static readonly string[] TwoCameras = ["Alpaca Camera", "Simulator"];
        private static readonly string[] OneMount = ["On-camera"];
        private static readonly string[] OneAuxMount = ["Alpaca Telescope"];
        private static readonly string[] OneRotator = ["Alpaca Rotator"];
        private static readonly string[] OneCamera = ["Simulator"];
        private static readonly string[] TwoServers = ["192.168.1.154:6800", "192.168.1.20:11111"];

        // ── DiscoverAlpacaServersRequest: send-site validation ──

        [Test]
        public void Discover_request_accepts_boundaries_and_carries_params() {
            var req = PHD2Guider.DiscoverAlpacaServersRequest(numQueries: 1, timeoutSeconds: 30);
            Assert.That(req.Parameters!.NumQueries, Is.EqualTo(1));
            Assert.That(req.Parameters!.TimeoutSeconds, Is.EqualTo(30));
            Assert.That(PHD2Guider.DiscoverAlpacaServersRequest(20, 1).Parameters!.NumQueries, Is.EqualTo(20));
        }

        [Test]
        public void Discover_request_rejects_a_combined_sweep_over_the_synchronous_cap() {
            // Individually daemon-valid fields whose product exceeds 60 s must be rejected — the REST
            // /discover endpoint is synchronous, so a dispatched sweep has to finish promptly.
            Assert.Throws<ArgumentException>(() => PHD2Guider.DiscoverAlpacaServersRequest(20, 30));
            Assert.Throws<ArgumentException>(() => PHD2Guider.DiscoverAlpacaServersRequest(3, 30));
            // Exactly at the cap (and defaults for the other field) is fine.
            Assert.That(PHD2Guider.DiscoverAlpacaServersRequest(2, 30).Parameters!.TimeoutSeconds, Is.EqualTo(30));
            Assert.That(PHD2Guider.DiscoverAlpacaServersRequest(20, 3).Parameters!.NumQueries, Is.EqualTo(20));
        }

        [Test]
        public void Discover_request_rejects_out_of_range_params() {
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.DiscoverAlpacaServersRequest(0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.DiscoverAlpacaServersRequest(21, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.DiscoverAlpacaServersRequest(null, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => PHD2Guider.DiscoverAlpacaServersRequest(null, 31));
        }

        [Test]
        public void Discover_request_omits_null_params_from_the_wire() {
            // Null fields must not serialize — the daemon's defaults apply only when the keys are absent.
            var wire = JObject.Parse(JsonConvert.SerializeObject(PHD2Guider.DiscoverAlpacaServersRequest(null, null)));
            var parameters = wire["params"] as JObject;
            Assert.That(parameters?.Property("num_queries", StringComparison.Ordinal), Is.Null);
            Assert.That(parameters?.Property("timeout_seconds", StringComparison.Ordinal), Is.Null);
        }

        // ── DiscoverReceiveTimeoutMs: matched to the sweep duration ──

        [Test]
        public void Discover_receive_timeout_scales_with_effective_params() {
            // Worst admissible case under the 60 s combined cap: 2 × 30 s sweep + 30 s grace.
            Assert.That(PHD2Guider.DiscoverReceiveTimeoutMs(2, 30), Is.EqualTo(90000));
            // Daemon defaults (2 × 2 s) apply for omitted fields.
            Assert.That(PHD2Guider.DiscoverReceiveTimeoutMs(null, null), Is.EqualTo(34000));
            Assert.That(PHD2Guider.DiscoverReceiveTimeoutMs(5, null), Is.EqualTo(40000));
        }

        // ── Result deserialization ──

        [Test]
        public void Equipment_choices_result_deserializes_all_slots_including_caps_AO_key() {
            const string json = """
                {"result":{"camera":["Alpaca Camera","Simulator"],"mount":["On-camera"],"aux_mount":["Alpaca Telescope"],"AO":[],"rotator":["Alpaca Rotator"]}}
                """;
            var response = JsonConvert.DeserializeObject<Phd2GetEquipmentChoicesResponse>(json)!;
            Assert.That(response.result!.Camera, Is.EqualTo(TwoCameras));
            Assert.That(response.result!.Mount, Is.EqualTo(OneMount));
            Assert.That(response.result!.AuxMount, Is.EqualTo(OneAuxMount));
            Assert.That(response.result!.AdaptiveOptics, Is.Empty);
            Assert.That(response.result!.Rotator, Is.EqualTo(OneRotator));
        }

        [Test]
        public void Equipment_choices_result_tolerates_missing_slots() {
            // A daemon build without rotator/AO support may omit slots entirely — absent must mean null, not throw.
            var response = JsonConvert.DeserializeObject<Phd2GetEquipmentChoicesResponse>(
                """{"result":{"camera":["Simulator"]}}""")!;
            Assert.That(response.result!.Camera, Is.EqualTo(OneCamera));
            Assert.That(response.result!.Rotator, Is.Null);
            Assert.That(response.result!.AdaptiveOptics, Is.Null);
        }

        [Test]
        public void Discover_result_deserializes_host_port_strings() {
            var response = JsonConvert.DeserializeObject<Phd2DiscoverAlpacaServersResponse>(
                """{"result":["192.168.1.154:6800","192.168.1.20:11111"]}""")!;
            Assert.That(response.result, Is.EqualTo(TwoServers));
        }
    }
}
