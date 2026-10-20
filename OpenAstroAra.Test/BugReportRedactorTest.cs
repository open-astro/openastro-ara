#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>§54.4 — what each sharing mode scrubs from a bundle's text and profile.</summary>
    [TestFixture]
    public class BugReportRedactorTest {

        private const string Profile = """
            {
              "site": { "name": "Backyard", "latitude_deg": 30.2672, "longitude_deg": -97.7431, "elevation_m": 149.5 },
              "notifications": { "pushover_token": "azGDORePK8gMaC0QOYAMyEEuzJnyUi", "telegram_bot_token": "", "email": "" },
              "equipment": [ { "name": "ASI2600MM", "serial_number": "1a2b3c4d", "host_name": "pi-observatory.local", "usb_serial": 12345 } ],
              "storage": { "save_directory": "/home/joey/captures", "note": "see /Users/joey/notes" },
              "guider": { "endpoint": "192.168.1.118:4400", "pulse_ms": 500 }
            }
            """;

        [Test]
        public void Full_leaves_everything_alone() {
            Assert.That(BugReportRedactor.RedactProfileJson(Profile, BugReportSharingMode.Full), Is.EqualTo(Profile));
            Assert.That(BugReportRedactor.RedactText("at /home/joey on 192.168.1.5", BugReportSharingMode.Full), Is.EqualTo("at /home/joey on 192.168.1.5"));
        }

        [Test]
        public void Coarse_gps_rounds_coordinates_to_whole_degrees_and_drops_elevation_only() {
            var node = JsonNode.Parse(BugReportRedactor.RedactProfileJson(Profile, BugReportSharingMode.CoarseGps))!;
            Assert.Multiple(() => {
                Assert.That(node["site"]!["latitude_deg"]!.GetValue<double>(), Is.EqualTo(30.0));
                Assert.That(node["site"]!["longitude_deg"]!.GetValue<double>(), Is.EqualTo(-98.0), "nearest whole degree, ~111 km");
                Assert.That(node["site"]!["elevation_m"]!.GetValue<double>(), Is.EqualTo(0.0));
                Assert.That(node["notifications"]!["pushover_token"]!.GetValue<string>(), Is.EqualTo("azGDORePK8gMaC0QOYAMyEEuzJnyUi"), "coarse GPS keeps everything else");
                Assert.That(node["storage"]!["save_directory"]!.GetValue<string>(), Is.EqualTo("/home/joey/captures"));
            });
        }

        [Test]
        public void Redacted_scrubs_coordinates_secrets_serials_hosts_and_user_paths() {
            var node = JsonNode.Parse(BugReportRedactor.RedactProfileJson(Profile, BugReportSharingMode.Redacted))!;
            Assert.Multiple(() => {
                Assert.That(node["site"]!["latitude_deg"]!.GetValue<double>(), Is.EqualTo(0.0));
                Assert.That(node["site"]!["longitude_deg"]!.GetValue<double>(), Is.EqualTo(0.0));
                Assert.That(node["site"]!["elevation_m"]!.GetValue<double>(), Is.EqualTo(0.0));
                Assert.That(node["site"]!["name"]!.GetValue<string>(), Is.EqualTo("Backyard"), "a site name is not on the §54 list");
                Assert.That(node["notifications"]!["pushover_token"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue));
                Assert.That(node["notifications"]!["telegram_bot_token"]!.GetValue<string>(), Is.EqualTo(""), "an empty secret stays empty: the reader learns it is unset");
                Assert.That(node["equipment"]![0]!["serial_number"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue));
                Assert.That(node["equipment"]![0]!["host_name"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue));
                Assert.That(node["equipment"]![0]!["usb_serial"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue), "a numeric serial is a serial");
                Assert.That(node["equipment"]![0]!["name"]!.GetValue<string>(), Is.EqualTo("ASI2600MM"), "a model name is diagnostic, not sensitive");
                Assert.That(node["storage"]!["save_directory"]!.GetValue<string>(), Is.EqualTo("/home/[user]/captures"));
                Assert.That(node["storage"]!["note"]!.GetValue<string>(), Is.EqualTo("see /Users/[user]/notes"));
                Assert.That(node["guider"]!["endpoint"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue));
                Assert.That(node["guider"]!["pulse_ms"]!.GetValue<int>(), Is.EqualTo(500), "numbers that are not coordinates are untouched");
            });
        }

        [Test]
        public void Redacted_text_scrubs_user_paths_private_ips_and_mdns_names_but_not_public_addresses() {
            var line = "profile at /home/joey/.local/share, C:\\Users\\Joey\\AppData, bridge 192.168.1.118:11111, sim 10.0.0.7, vpn 172.16.4.2, api 8.8.8.8, host pi-observatory.local, sesame cds.unistra.fr";
            var scrubbed = BugReportRedactor.RedactText(line, BugReportSharingMode.Redacted);
            Assert.That(scrubbed, Is.EqualTo("profile at /home/[user]/.local/share, C:\\Users\\[user]\\AppData, bridge [private-ip]:11111, sim [private-ip], vpn [private-ip], api 8.8.8.8, host [host].local, sesame cds.unistra.fr"));
            // Inside serialized JSON the backslashes are doubled.
            Assert.That(BugReportRedactor.RedactText("{\"path\":\"C:\\\\Users\\\\Joey\\\\x\"}", BugReportSharingMode.Redacted), Is.EqualTo("{\"path\":\"C:\\\\Users\\\\[user]\\\\x\"}"));
        }

        [Test]
        public void Unparseable_profile_falls_back_to_text_scrubbing() {
            var scrubbed = BugReportRedactor.RedactProfileJson("not json, path /home/joey/x", BugReportSharingMode.Redacted);
            Assert.That(scrubbed, Is.EqualTo("not json, path /home/[user]/x"));
        }

        [TestCase(null, BugReportSharingMode.Full)]
        [TestCase("", BugReportSharingMode.Full)]
        [TestCase("full", BugReportSharingMode.Full)]
        [TestCase("Coarse_GPS", BugReportSharingMode.CoarseGps)]
        [TestCase(" redacted ", BugReportSharingMode.Redacted)]
        public void Wire_values_parse(string? wire, BugReportSharingMode expected) {
            Assert.That(BugReportRedactor.Parse(wire), Is.EqualTo(expected));
            Assert.That(BugReportRedactor.Parse(BugReportRedactor.ToWire(expected)), Is.EqualTo(expected));
        }

        [Test]
        public void An_unknown_wire_value_is_null() {
            Assert.That(BugReportRedactor.Parse("everything"), Is.Null);
        }
    }
}
