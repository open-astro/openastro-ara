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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.18 — <see cref="PHD2Guider.MarkerEvent"/>: the PHD2 event messages that are not guide
    /// steps, mapped to <see cref="PhdGuiderMarkerEventArgs"/>. Drives the REAL client listener
    /// (socket read → JSON parse → ProcessEvent) from the bench's <see cref="FakeGuider"/>, so each
    /// kind token and every field is checked against the PHD2 wire names, not hand-built args
    /// (<see cref="GuiderStepPayloadTest"/> covers the step from the args to the WS payload).
    /// One connection for the fixture; tests run in order inside it and each drains the queue first.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback sockets against the fake guider
    public class PHD2GuiderMarkerEventTest {

        private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(10);

        private FakeGuider _fake = null!;
        private PHD2Guider _guider = null!;
        private readonly BlockingCollection<PhdGuiderMarkerEventArgs> _markers = new();

        [OneTimeSetUp]
        public async Task ConnectToTheFake() {
            _fake = FakeGuider.Start();
            _fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            _fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));

            var profiles = new HeadlessProfileService();
            profiles.ActiveProfile.GuiderSettings.PHD2ServerHost = "127.0.0.1";
            profiles.ActiveProfile.GuiderSettings.PHD2ServerPort = _fake.Port;
            _guider = new PHD2Guider(profiles);
            _guider.MarkerEvent += (_, marker) => _markers.Add(marker);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var connected = await _guider.Connect(cts.Token).ConfigureAwait(false);
            Assert.That(connected, Is.True, "the real PHD2 client never opened its event stream to the fake");
        }

        [OneTimeTearDown]
        public async Task Disconnect() {
            _guider?.Dispose();
            if (_fake is not null) {
                await _fake.DisposeAsync().ConfigureAwait(false);
            }
            _markers.Dispose();
        }

        [SetUp]
        public void DrainQueue() {
            while (_markers.TryTake(out _)) {
            }
        }

        private static JsonObject Event(string name, Action<JsonObject>? fill = null) {
            var e = new JsonObject {
                ["Event"] = name,
                ["Timestamp"] = 0.0,
                ["Host"] = "fake-guider",
                ["Inst"] = 1,
            };
            fill?.Invoke(e);
            return e;
        }

        private async Task<PhdGuiderMarkerEventArgs> PushAndReceive(JsonObject phdEvent) {
            await _fake.BroadcastAsync(phdEvent).ConfigureAwait(false);
            return NextMarker();
        }

        private PhdGuiderMarkerEventArgs NextMarker() {
            Assert.That(_markers.TryTake(out var marker, MarkerTimeout), Is.True,
                "PHD2Guider raised no MarkerEvent for the pushed PHD2 event");
            return marker!;
        }

        // ── Field-less transitions: the kind token is the whole mapping ──

        [TestCase("Resumed", "resumed")]
        [TestCase("Paused", "paused")]
        [TestCase("StartCalibration", "calibration_started")]
        [TestCase("CalibrationComplete", "calibration_complete")]
        [TestCase("GuidingStopped", "guiding_stopped")]
        [TestCase("StartGuiding", "guiding_started")]
        [TestCase("LockPositionLost", "lock_position_lost")]
        public async Task A_session_transition_maps_to_its_kind_token(string phdEvent, string kind) {
            var marker = await PushAndReceive(Event(phdEvent)).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo(kind));
            Assert.That(marker.Error, Is.Null);
            Assert.That(marker.Status, Is.Null);
            Assert.That(marker.Distance, Is.Null);
        }

        // ── Events that carry fields ──

        [Test]
        public async Task GuidingDithered_maps_dx_and_dy() {
            var marker = await PushAndReceive(Event("GuidingDithered", e => {
                e["dx"] = 2.5;
                e["dy"] = -1.25;
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("dithered"));
            Assert.That(marker.Dx, Is.EqualTo(2.5));
            Assert.That(marker.Dy, Is.EqualTo(-1.25));
        }

        [Test]
        public async Task Settling_maps_distance_time_and_settle_time() {
            var marker = await PushAndReceive(Event("Settling", e => {
                e["Distance"] = 0.75;
                e["Time"] = 2.5; // PHD2 reports fractional seconds
                e["SettleTime"] = 10.0;
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("settling"));
            Assert.That(marker.Distance, Is.EqualTo(0.75));
            Assert.That(marker.TimeSec, Is.EqualTo(2.5));
            Assert.That(marker.SettleTimeSec, Is.EqualTo(10.0));
        }

        [Test]
        public async Task SettleDone_success_maps_status_zero_and_no_error() {
            var marker = await PushAndReceive(Event("SettleDone", e => e["Status"] = 0)).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("settle_done"));
            Assert.That(marker.Status, Is.EqualTo(0));
            Assert.That(marker.Error, Is.Null, "an absent Error must not surface as an empty string");
        }

        [Test]
        public async Task SettleDone_failure_maps_status_and_error_text() {
            var marker = await PushAndReceive(Event("SettleDone", e => {
                e["Status"] = 1;
                e["Error"] = "timed-out waiting for guider to settle";
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("settle_done"));
            Assert.That(marker.Status, Is.EqualTo(1));
            Assert.That(marker.Error, Is.EqualTo("timed-out waiting for guider to settle"));
        }

        [Test]
        public async Task CalibrationFailed_maps_Reason_to_error() {
            var marker = await PushAndReceive(Event("CalibrationFailed", e => {
                e["Reason"] = "star did not move enough";
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("calibration_failed"));
            Assert.That(marker.Error, Is.EqualTo("star did not move enough"));
        }

        [Test]
        public async Task StarLost_maps_AvgDist_to_distance_ErrorCode_to_status_and_Status_to_error() {
            // PHD2's StarLost names are crossed relative to the marker: the integer code is
            // ErrorCode (→ Status) and the human text is Status (→ Error).
            var marker = await PushAndReceive(Event("StarLost", e => {
                e["Frame"] = 42;
                e["Time"] = 7;
                e["StarMass"] = 1234.5;
                e["SNR"] = 3.25;
                e["AvgDist"] = 1.75;
                e["ErrorCode"] = 3;
                e["Status"] = "star lost - low SNR";
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("star_lost"));
            Assert.That(marker.Frame, Is.EqualTo(42));
            Assert.That(marker.StarMass, Is.EqualTo(1234.5));
            Assert.That(marker.Snr, Is.EqualTo(3.25));
            Assert.That(marker.Distance, Is.EqualTo(1.75), "AvgDist → Distance");
            Assert.That(marker.Status, Is.EqualTo(3), "ErrorCode → Status");
            Assert.That(marker.Error, Is.EqualTo("star lost - low SNR"), "Status (text) → Error");
        }

        [Test]
        public async Task StarLost_with_empty_status_text_has_no_error() {
            var marker = await PushAndReceive(Event("StarLost", e => {
                e["Frame"] = 1;
                e["AvgDist"] = 0.5;
                e["ErrorCode"] = 0;
                e["Status"] = "";
            })).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("star_lost"));
            Assert.That(marker.Status, Is.EqualTo(0));
            Assert.That(marker.Error, Is.Null, "an empty Status must not surface as an empty error string");
        }

        // ── Non-marker events stay off the marker stream ──

        [Test]
        public async Task Guide_steps_and_state_events_raise_no_marker() {
            await _fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.2)).ConfigureAwait(false);
            await _fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            await _fake.BroadcastAsync(Event("StarSelected", e => { e["X"] = 10.0; e["Y"] = 20.0; })).ConfigureAwait(false);
            await _fake.BroadcastAsync(Event("LoopingExposures", e => e["Frame"] = 5)).ConfigureAwait(false);
            // The listener processes lines in order, so the sentinel's marker arriving first
            // proves none of the events before it raised one.
            var marker = await PushAndReceive(Event("Resumed")).ConfigureAwait(false);

            Assert.That(marker.Kind, Is.EqualTo("resumed"));
            Assert.That(_markers.Count, Is.Zero);
        }
    }
}
