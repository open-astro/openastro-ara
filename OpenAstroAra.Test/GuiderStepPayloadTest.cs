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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>§63.18 — the <c>guider.step</c> payload the Live-tab guide graph is drawn from.</summary>
    [TestFixture]
    public class GuiderStepPayloadTest {

        private static PhdEventGuideStep Step(double ra, double dec, double raMs, string raDir, double decMs, string decDir) =>
            new() {
                Frame = 42,
                RADistanceRaw = ra,
                DECDistanceRaw = dec,
                RADuration = raMs,
                RADirection = raDir,
                DECDuration = decMs,
                DECDirection = decDir,
                StarMass = 1234,
                SNR = 25.5,
            };

        [Test]
        public void Catalog_lists_the_step_token() {
            Assert.That(WsEventCatalog.GuiderStep, Is.EqualTo("guider.step"));
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.GuiderStep));
        }

        [Test]
        public void Payload_carries_raw_pixels_and_arcsec_when_the_scale_is_known() {
            // PhdEventGuideStep's RA getter already flips the sign (NINA's graph convention); the
            // payload carries the IGuideStep view verbatim, so compare against the step, not the
            // raw setter inputs.
            var step = Step(0.5, -0.25, 120, "West", 80, "North");
            var payload = GuiderService.BuildGuideStepPayload(step, 2.0);
            Assert.That(payload["frame"]!.GetValue<double>(), Is.EqualTo(42));
            Assert.That(payload["ra_raw_px"]!.GetValue<double>(), Is.EqualTo(step.RADistanceRaw));
            Assert.That(payload["dec_raw_px"]!.GetValue<double>(), Is.EqualTo(-0.25));
            Assert.That(payload["ra_arcsec"]!.GetValue<double>(), Is.EqualTo(step.RADistanceRaw * 2.0));
            Assert.That(payload["dec_arcsec"]!.GetValue<double>(), Is.EqualTo(-0.5));
            Assert.That(payload["pixel_scale_arcsec"]!.GetValue<double>(), Is.EqualTo(2.0));
            Assert.That(payload["star_mass"]!.GetValue<double>(), Is.EqualTo(1234));
            Assert.That(payload["snr"]!.GetValue<double>(), Is.EqualTo(25.5));
        }

        [Test]
        public void Pulse_durations_keep_the_signed_direction_convention() {
            // IGuideStep already negates East / South; the payload must not re-sign them.
            var payload = GuiderService.BuildGuideStepPayload(Step(0, 0, 120, "East", 80, "South"), null);
            Assert.That(payload["ra_duration_ms"]!.GetValue<double>(), Is.EqualTo(-120));
            Assert.That(payload["dec_duration_ms"]!.GetValue<double>(), Is.EqualTo(-80));
            var west = GuiderService.BuildGuideStepPayload(Step(0, 0, 120, "West", 80, "North"), null);
            Assert.That(west["ra_duration_ms"]!.GetValue<double>(), Is.EqualTo(120));
            Assert.That(west["dec_duration_ms"]!.GetValue<double>(), Is.EqualTo(80));
        }

        [Test]
        public void No_pixel_scale_means_null_arcsec_not_a_zero() {
            var payload = GuiderService.BuildGuideStepPayload(Step(0.5, 0.5, 0, "West", 0, "North"), null);
            Assert.That(payload["ra_arcsec"], Is.Null);
            Assert.That(payload["dec_arcsec"], Is.Null);
            Assert.That(payload["pixel_scale_arcsec"], Is.Null);
        }

        [Test]
        public void Non_finite_readings_serialize_as_null() {
            var payload = GuiderService.BuildGuideStepPayload(Step(double.NaN, double.PositiveInfinity, 0, "West", 0, "North"), 1.5);
            Assert.That(payload["ra_raw_px"], Is.Null);
            Assert.That(payload["dec_raw_px"], Is.Null);
            Assert.That(payload["ra_arcsec"], Is.Null);
            Assert.That(payload["dec_arcsec"], Is.Null);
            // and the whole object still round-trips through System.Text.Json
            Assert.DoesNotThrow(() => JsonNode.Parse(payload.ToJsonString()));
        }

        [Test]
        public void Catalog_lists_the_marker_token() {
            Assert.That(WsEventCatalog.GuiderEvent, Is.EqualTo("guider.event"));
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.GuiderEvent));
        }

        [Test]
        public void Marker_payload_carries_kind_and_only_the_details_present() {
            var dither = GuiderService.BuildGuiderEventPayload(new PhdGuiderMarkerEventArgs { Kind = "dithered", Dx = 2.5, Dy = -1.0 });
            Assert.That(dither["kind"]!.GetValue<string>(), Is.EqualTo("dithered"));
            Assert.That(dither["dx_px"]!.GetValue<double>(), Is.EqualTo(2.5));
            Assert.That(dither["dy_px"]!.GetValue<double>(), Is.EqualTo(-1.0));
            Assert.That(dither.ContainsKey("error"), Is.False);
            Assert.That(dither.ContainsKey("status"), Is.False);

            var settle = GuiderService.BuildGuiderEventPayload(new PhdGuiderMarkerEventArgs { Kind = "settle_done", Status = 0 });
            Assert.That(settle["status"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(settle.ContainsKey("error"), Is.False);

            var lost = GuiderService.BuildGuiderEventPayload(new PhdGuiderMarkerEventArgs {
                Kind = "star_lost", Frame = 77, StarMass = double.NaN, Snr = 3.2, Error = "low SNR", Status = 2,
            });
            Assert.That(lost["frame"]!.GetValue<int>(), Is.EqualTo(77));
            Assert.That(lost.ContainsKey("star_mass"), Is.False, "NaN is omitted, never serialized");
            Assert.That(lost["snr"]!.GetValue<double>(), Is.EqualTo(3.2));
            Assert.That(lost["error"]!.GetValue<string>(), Is.EqualTo("low SNR"));
            Assert.DoesNotThrow(() => JsonNode.Parse(lost.ToJsonString()));
        }

    }
}
