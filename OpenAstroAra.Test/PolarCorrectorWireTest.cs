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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace OpenAstroAra.Test {

    /// <summary>#1972 — the PolarAlign actions on the wire: the real Alpaca Switch adapter as the
    /// carrier, against a scripted loopback device.</summary>
    [TestFixture]
    [Category("bench")] // loopback-only, runs in the default job too
    public class PolarCorrectorWireTest {

        private const string ActionPath = "/api/v1/switch/0/action";

        private const string FullInfo = """
            {"tiers":[1,2],"deviceName":"Bench PA","deviceVersion":"0.1",
             "axes":{"az":{"maxMove":60,"resolution":0.05,"range":[-120,120]},"alt":{"maxMove":30,"resolution":0.1}},
             "simultaneousMoves":true,"movesWhileTracking":false,"movesWhileSlewing":false,"canStop":true,
             "settleSeconds":2.5,"someFutureField":{"x":1}}
            """;

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "pa-box", Name: "Bench PA", Type: DeviceType.Switch,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // The Alpaca reply carries the action's text as a JSON string Value.
        private static string Reply(string text) => JsonSerializer.Serialize(text);

        private static (ISwitchDevice Device, PolarAlignActionsCorrector Corrector) Over(ScriptedAlpacaDevice box) {
            var device = AlpacaSwitchDevice.Create(Device(box));
            var carrier = new LiveDriverActions(LiveDriverActions.Read(() => device.SupportedActions), () => true, device.Action);
            return (device, new PolarAlignActionsCorrector(carrier));
        }

        private static (string? Action, string? Parameters) Sent(ScriptedAlpacaDevice box, int index) {
            var put = box.Puts.Where(p => p.Path == ActionPath).ElementAt(index);
            var form = HttpUtility.ParseQueryString(put.Body);
            return (form["Action"], form["Parameters"]);
        }

        [Test]
        public async Task Move_sends_the_action_name_and_the_axis_and_signed_arcminutes() {
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/supportedactions", StringComparison.Ordinal) ? """["PolarAlign:Info"]""" : null,
                path => path == ActionPath ? Reply(string.Empty) : null);
            var (device, corrector) = Over(box);
            using (device) {
                corrector.Move(PolarCorrectorAxis.Azimuth, -1.25);
                corrector.Move(PolarCorrectorAxis.Altitude, 3);
                corrector.Stop();
                corrector.Home();
            }

            Assert.Multiple(() => {
                Assert.That(Sent(box, 0), Is.EqualTo(("PolarAlign:Move", """{"axis":"az","arcmin":-1.25}""")));
                Assert.That(Sent(box, 1), Is.EqualTo(("PolarAlign:Move", """{"axis":"alt","arcmin":3}""")));
                Assert.That(Sent(box, 2).Action, Is.EqualTo("PolarAlign:Stop"));
                Assert.That(Sent(box, 2).Parameters, Is.Empty);
                Assert.That(Sent(box, 3).Action, Is.EqualTo("PolarAlign:Home"));
                Assert.That(Sent(box, 3).Parameters, Is.Empty);
            });
        }

        [Test]
        public async Task The_carrier_reads_the_supported_actions_the_driver_lists() {
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/supportedactions", StringComparison.Ordinal) ? """["polaralign:info","Other"]""" : null);
            using var device = AlpacaSwitchDevice.Create(Device(box));

            Assert.That(PolarAlignActionsCorrector.IsOfferedBy(device.SupportedActions), Is.True);
        }

        [Test]
        public async Task Info_and_status_parse_with_every_optional_field_and_ignore_unknown_ones() {
            const string status = """{"az":{"moving":true,"position":-12.5,"fault":"stalled","extra":1},"alt":{"moving":false,"position":0}}""";
            var reply = Reply(FullInfo);
            await using var box = ScriptedAlpacaDevice.Start(putResponder: _ => reply);
            var (device, corrector) = Over(box);
            PolarCorrectorInfo info;
            PolarCorrectorStatus state;
            using (device) {
                info = corrector.Info();
                reply = Reply(status);
                state = corrector.Status();
            }

            Assert.Multiple(() => {
                Assert.That(Sent(box, 0), Is.EqualTo(("PolarAlign:Info", string.Empty)));
                Assert.That(Sent(box, 1), Is.EqualTo(("PolarAlign:Status", string.Empty)));
                Assert.That(info.HostDriven, Is.True);
                Assert.That(info.DeviceName, Is.EqualTo("Bench PA"));
                Assert.That(info.Azimuth, Is.EqualTo(new PolarCorrectorAxisInfo(60, 0.05)));
                Assert.That(info.Altitude, Is.EqualTo(new PolarCorrectorAxisInfo(30, 0.1)));
                Assert.That(info.SimultaneousMoves, Is.True);
                Assert.That(info.MovesWhileTracking, Is.False);
                Assert.That(info.CanStop, Is.True);
                Assert.That(info.SettleSeconds, Is.EqualTo(2.5));
                Assert.That(state.Azimuth, Is.EqualTo(new PolarCorrectorAxisStatus(true, -12.5, "stalled")));
                Assert.That(state.Altitude, Is.EqualTo(new PolarCorrectorAxisStatus(false, 0, null)), "a reported 0 is a position");
            });
        }

        [Test]
        public async Task Info_and_status_without_the_optional_fields_leave_them_absent() {
            var reply = Reply("""{"tiers":[1],"axes":{"az":{"maxMove":60},"alt":{"maxMove":30}}}""");
            await using var box = ScriptedAlpacaDevice.Start(putResponder: _ => reply);
            var (device, corrector) = Over(box);
            PolarCorrectorInfo info;
            PolarCorrectorStatus state;
            using (device) {
                info = corrector.Info();
                reply = Reply("""{"az":{"moving":false},"alt":{"moving":true}}""");
                state = corrector.Status();
            }

            Assert.Multiple(() => {
                Assert.That(info.Azimuth, Is.EqualTo(new PolarCorrectorAxisInfo(60, 0)), "no resolution is 0 (no calibration)");
                Assert.That(info.HostDriven, Is.True);
                Assert.That(state.Azimuth, Is.EqualTo(new PolarCorrectorAxisStatus(false, null, null)));
                Assert.That(state.Altitude, Is.EqualTo(new PolarCorrectorAxisStatus(true, null, null)));
                Assert.That(state.Moving, Is.True);
            });
        }

        [Test]
        public async Task An_InvalidOperation_error_on_Move_is_not_calibrated() {
            await using var box = ScriptedAlpacaDevice.Start(putResponder: _ => ScriptedAlpacaDevice.Error(0x40B, "no calibration factors"));
            var (device, corrector) = Over(box);
            using (device) {
                var ex = Assert.Throws<PolarCorrectorException>(() => corrector.Move(PolarCorrectorAxis.Azimuth, 1));
                Assert.That(ex!.Failure, Is.EqualTo(PolarCorrectorFailure.NotCalibrated));
                Assert.That(ex.Message, Does.Contain("no calibration factors"));
            }
        }

        [Test]
        public async Task Another_driver_error_on_Move_is_refused_with_the_devices_text() {
            await using var box = ScriptedAlpacaDevice.Start(putResponder: _ => ScriptedAlpacaDevice.Error("axis at its limit"));
            var (device, corrector) = Over(box);
            using (device) {
                var ex = Assert.Throws<PolarCorrectorException>(() => corrector.Move(PolarCorrectorAxis.Altitude, 1));
                Assert.That(ex!.Failure, Is.EqualTo(PolarCorrectorFailure.Refused));
                Assert.That(ex.Message, Does.Contain("axis at its limit"));
            }
        }

        [Test]
        public async Task A_dead_socket_is_a_lost_link() {
            var box = ScriptedAlpacaDevice.Start();
            var (device, corrector) = Over(box);
            await box.DisposeAsync();
            using (device) {
                var ex = Assert.Throws<PolarCorrectorException>(() => corrector.Stop());
                Assert.That(ex!.Failure, Is.EqualTo(PolarCorrectorFailure.LinkLost));
            }
        }

        // ─── Pure parse ───

        [Test]
        public void Info_without_tier_1_is_not_host_driven() {
            var info = PolarAlignActionsCorrector.ParseInfo("""{"tiers":[2],"axes":{"az":{"maxMove":60},"alt":{"maxMove":30}}}""");
            Assert.That(info.HostDriven, Is.False);
        }

        [Test]
        public void Info_without_tiers_is_not_host_driven() {
            var info = PolarAlignActionsCorrector.ParseInfo("""{"axes":{"az":{"maxMove":60},"alt":{"maxMove":30}}}""");
            Assert.That(info.HostDriven, Is.False);
        }

        [Test]
        public void An_axis_without_maxMove_is_absent() {
            var info = PolarAlignActionsCorrector.ParseInfo("""{"tiers":[1],"axes":{"az":{"resolution":0.1},"alt":{"maxMove":30}}}""");
            Assert.Multiple(() => {
                Assert.That(info.Azimuth, Is.Null);
                Assert.That(info.HostDriven, Is.False);
            });
        }

        // A reply that does not say whether an axis moves must never read as "not moving": the routine
        // would take a move in flight for a finished one.
        [TestCase("{}")]
        [TestCase("""{"az":{"moving":false}}""")]
        [TestCase("""{"az":{"position":3},"alt":{"moving":false}}""")]
        [TestCase("""{"az":{"moving":"no"},"alt":{"moving":false}}""")]
        public void A_status_that_does_not_say_whether_each_axis_moves_is_a_protocol_failure(string reply) {
            Assert.That(Assert.Throws<PolarCorrectorException>(() => PolarAlignActionsCorrector.ParseStatus(reply))!.Failure,
                Is.EqualTo(PolarCorrectorFailure.Protocol));
        }

        [TestCase("not json")]
        [TestCase("[1,2]")]
        [TestCase("")]
        public void A_reply_that_is_not_a_JSON_object_is_a_protocol_failure(string reply) {
            Assert.Multiple(() => {
                Assert.That(Assert.Throws<PolarCorrectorException>(() => PolarAlignActionsCorrector.ParseInfo(reply))!.Failure,
                    Is.EqualTo(PolarCorrectorFailure.Protocol));
                Assert.That(Assert.Throws<PolarCorrectorException>(() => PolarAlignActionsCorrector.ParseStatus(reply))!.Failure,
                    Is.EqualTo(PolarCorrectorFailure.Protocol));
            });
        }
    }
}
