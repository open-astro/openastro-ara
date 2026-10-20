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
              "notifications": { "pushover_token": "azGDORePK8gMaC0QOYAMyEEuzJnyUi", "pushover_user_key": "uQiRzpo4DXghDmr9QzzfQu27cmVRsG", "telegram_bot_token": "", "telegram_chat_id": "123456789", "email": "" },
              "guider_alpaca_host": "observatory",
              "equipment": [ { "name": "ASI2600MM", "serial_number": "1a2b3c4d", "host_name": "pi-observatory.local", "usb_serial": 12345 } ],
              "guide_site": { "latitude_deg": "30.27", "longitude_deg": "-97.74" },
              "storage": { "save_directory": "/home/joey/captures", "note": "see /Users/joey/notes" },
              "guider": { "endpoint": "192.168.1.118:4400", "pulse_ms": 500 }
            }
            """;

        [Test]
        public void Full_leaves_everything_alone_except_credential_shapes() {
            var node = JsonNode.Parse(BugReportRedactor.RedactProfileJson(Profile, BugReportSharingMode.Full))!;
            Assert.Multiple(() => {
                Assert.That(node["site"]!["latitude_deg"]!.GetValue<double>(), Is.EqualTo(30.2672));
                Assert.That(node["site"]!["elevation_m"]!.GetValue<double>(), Is.EqualTo(149.5));
                Assert.That(node["notifications"]!["pushover_token"]!.GetValue<string>(), Is.EqualTo("azGDORePK8gMaC0QOYAMyEEuzJnyUi"), "a token under a token key is not a §54.6 shape; full keeps it");
                Assert.That(node["storage"]!["save_directory"]!.GetValue<string>(), Is.EqualTo("/home/joey/captures"));
                Assert.That(node["guider"]!["endpoint"]!.GetValue<string>(), Is.EqualTo("192.168.1.118:4400"));
            });
            Assert.That(BugReportRedactor.RedactText("at /home/joey on 192.168.1.5", BugReportSharingMode.Full), Is.EqualTo("at /home/joey on 192.168.1.5"));
            var withPat = JsonNode.Parse(BugReportRedactor.RedactProfileJson("{\"note\":\"pat ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123 here\"}", BugReportSharingMode.Full))!;
            Assert.That(withPat["note"]!.GetValue<string>(), Is.EqualTo("pat [REDACTED] here"), "§54.6 applies inside the profile in full mode");
        }

        [Test]
        public void A_string_elevation_is_zeroed_too() {
            var node = JsonNode.Parse(BugReportRedactor.RedactProfileJson("{\"site\":{\"elevation_m\":\"149.5\"}}", BugReportSharingMode.CoarseGps))!;
            Assert.That(node["site"]!["elevation_m"]!.GetValue<double>(), Is.EqualTo(0.0));
        }

        [Test]
        public void Credential_shapes_are_stripped_in_every_mode() {
            // §54.6 — not user-toggleable.
            const string line = "hdr X-OpenAstroAra-Token: abcdefghijklmnopqrstuvwxyz0123 auth Bearer eyJhbGciOiJIUzI1NiJ9.payload+sig/x== pat ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123 slack xoxb-1234567890-abcdef key -----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY----- short Bearer abc";
            foreach (var mode in new[] { BugReportSharingMode.Full, BugReportSharingMode.CoarseGps, BugReportSharingMode.Redacted }) {
                var s = BugReportRedactor.RedactText(line, mode);
                Assert.That(s, Does.Not.Contain("abcdefghijklmnopqrstuvwxyz0123").And.Not.Contain("eyJhbGci").And.Not.Contain("ghp_ABC").And.Not.Contain("xoxb-").And.Not.Contain("MIIE"), mode.ToString());
                Assert.That(s, Does.Contain("X-OpenAstroAra-Token: [REDACTED]").And.Contain("Bearer [REDACTED] pat").And.Contain("short Bearer abc"), "the header name stays; a padded base64 token leaves no tail; a short bearer is not a token");
            }
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
                Assert.That(node["notifications"]!["pushover_user_key"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue), "both halves of a channel");
                Assert.That(node["notifications"]!["telegram_chat_id"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedValue));
                Assert.That(node["guider_alpaca_host"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedHost), "a bare host name under a *_host key");
                Assert.That(node["equipment"]![0]!["serial_number"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedSerial));
                Assert.That(node["equipment"]![0]!["host_name"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedHost));
                Assert.That(node["equipment"]![0]!["usb_serial"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedSerial), "a numeric serial is a serial");
                Assert.That(node["equipment"]![0]!["name"]!.GetValue<string>(), Is.EqualTo("ASI2600MM"), "a model name is diagnostic, not sensitive");
                Assert.That(node["guide_site"]!["latitude_deg"]!.GetValue<double>(), Is.EqualTo(0.0), "a coordinate stored as a string is still a coordinate");
                Assert.That(node["storage"]!["save_directory"]!.GetValue<string>(), Is.EqualTo("/home/[REDACTED-USER]/captures"));
                Assert.That(node["storage"]!["note"]!.GetValue<string>(), Is.EqualTo("see /Users/[REDACTED-USER]/notes"));
                Assert.That(node["guider"]!["endpoint"]!.GetValue<string>(), Is.EqualTo(BugReportRedactor.RedactedHost));
                Assert.That(node["guider"]!["pulse_ms"]!.GetValue<int>(), Is.EqualTo(500), "numbers that are not coordinates are untouched");
            });
        }

        [Test]
        public void Redacted_text_scrubs_user_paths_private_ips_and_mdns_names_but_not_public_addresses() {
            var line = "profile at /home/joey/.local/share, C:\\Users\\Joey\\AppData, bridge 192.168.1.118:11111, sim 10.0.0.7, vpn 172.16.4.2, api 8.8.8.8, host pi-observatory.local, mount astro.pi.lan, sesame cds.unistra.fr";
            var scrubbed = BugReportRedactor.RedactText(line, BugReportSharingMode.Redacted);
            Assert.That(scrubbed, Is.EqualTo("profile at /home/[REDACTED-USER]/.local/share, C:\\Users\\[REDACTED-USER]\\AppData, bridge [REDACTED-IP]:11111, sim [REDACTED-IP], vpn [REDACTED-IP], api 8.8.8.8, host [REDACTED-HOST].local, mount [REDACTED-HOST].lan, sesame cds.unistra.fr"));
            // Inside serialized JSON the backslashes are doubled.
            Assert.That(BugReportRedactor.RedactText("{\"path\":\"C:\\\\Users\\\\Joey\\\\x\"}", BugReportSharingMode.Redacted), Is.EqualTo("{\"path\":\"C:\\\\Users\\\\[REDACTED-USER]\\\\x\"}"));
        }

        [Test]
        public void Unparseable_profile_falls_back_to_text_scrubbing() {
            var scrubbed = BugReportRedactor.RedactProfileJson("not json, path /home/joey/x", BugReportSharingMode.Redacted);
            Assert.That(scrubbed, Is.EqualTo("not json, path /home/[REDACTED-USER]/x"));
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
