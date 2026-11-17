#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>
    /// What the PHD2 adapter does on the wire for the capture seam, with a real <see cref="GuiderService"/> over the TCP
    /// <see cref="FakeGuider"/> (an adapter test, so the socket is allowed here, ADR 0016 rule 5). The Polar Align
    /// routine's reaction to each answer is in <see cref="PolarAlignServiceTest"/>, on the in-process
    /// <see cref="FakeGuideCapture"/>; the cases both fakes must agree on are in <see cref="GuideCaptureContract"/>.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback socket
    [Category("bench")]
    public class Phd2GuideCaptureLinkTest {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        // NUnit reuses one fixture instance for every case: SetUp gives each its own state.
        private GuiderService? _guider;

        [SetUp]
        public void SetUp() => _guider = null;

        [TearDown]
        public void TearDown() {
            // The test's own `await using` disposes the TCP fake.
            _guider?.Dispose();
        }

        private async Task<IGuideCaptureLink> ConnectedLinkAsync(FakeGuider tcp) {
            tcp.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            tcp.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            _guider = new GuiderService(new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await _guider.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", tcp.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => _guider.ConnectedGuiderOrNull() is { Connected: true } c && c.ConnectedRpcPort == tcp.Port)
                .ConfigureAwait(false);
            return ((IGuideCapture)_guider).ConnectedLink();
        }

        // The guider's connect handshake has no event of its own to wait for: poll the service until it reports Connected.
        private static async Task WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(Timeout);
            while (!condition()) {
                await Task.Delay(20, cts.Token).ConfigureAwait(false);
            }
        }

        private static JsonObject FrameComplete(string filename) => new() {
            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g", ["Inst"] = 1, ["Success"] = true,
            ["Path"] = "/var/lib/openastro-guider/" + filename, ["Filename"] = filename,
        };

        // #1749 — a guider that loops until stop_capture.
        private static ConcurrentQueue<string> OnLoopingGuider(FakeGuider tcp, string state, bool stops = true) {
            var calls = new ConcurrentQueue<string>();
            var looping = 1;
            tcp.OnRpc("get_app_state", _ => JsonValue.Create(Volatile.Read(ref looping) == 1 ? state : "Stopped"));
            tcp.OnRpc("stop_capture", _ => {
                calls.Enqueue("stop_capture");
                if (stops) {
                    Volatile.Write(ref looping, 0);
                }
                return JsonValue.Create(0);
            });
            return calls;
        }

        [TestCase("Looping")]
        [TestCase("Selected")]
        public async Task A_looping_guide_camera_is_sent_stop_capture_and_the_loop_stop_reports_success(string state) {
            await using var tcp = FakeGuider.Start();
            var stops = OnLoopingGuider(tcp, state);
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            var stopped = await link.StopLoopForCaptureAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);

            Assert.That(stopped, Is.True);
            Assert.That(stops, Has.Count.EqualTo(1), "stop_capture was sent once");
        }

        [Test]
        public async Task A_stopped_guide_camera_gets_no_stop_capture_and_the_loop_stop_reports_success() {
            await using var tcp = FakeGuider.Start();
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            var stopped = await link.StopLoopForCaptureAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);

            Assert.That(stopped, Is.True);
            Assert.That(tcp.ReceivedMethods, Does.Not.Contain("stop_capture"));
        }

        [Test]
        public async Task A_loop_that_does_not_stop_is_reported_as_not_stopped() {
            await using var tcp = FakeGuider.Start();
            var stops = OnLoopingGuider(tcp, "Looping", stops: false);
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            var stopped = await link.StopLoopForCaptureAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None).ConfigureAwait(false);

            Assert.That(stopped, Is.False);
            Assert.That(stops, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task The_loop_stop_shares_one_time_limit_between_its_two_steps() {
            // #1749 asks for at most 10 s; a slow stop_capture must not earn the state wait a fresh limit.
            await using var tcp = FakeGuider.Start();
            tcp.OnRpc("get_app_state", _ => JsonValue.Create("Looping"));
            tcp.OnRpc("stop_capture", _ => { Thread.Sleep(800); return JsonValue.Create(0); });
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var stopped = await link.StopLoopForCaptureAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);

            Assert.That(stopped, Is.False);
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(1600)));
        }

        [Test]
        public async Task A_start_right_after_a_stop_waits_for_the_capture_the_guider_is_still_exposing() {
            // #1711: Stop abandons the capture the guider is still exposing; a Start within seconds sent a second
            // capture_single_frame, which the guider refused ("capture is currently active"). The wait ends when the
            // guider announces the earlier capture's end on SingleFrameComplete, and not before.
            await using var tcp = FakeGuider.Start();
            tcp.OnRpc("capture_single_frame", JsonValue.Create(0)); // ack, complete later
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);
            await link.CaptureFrameAsync(50, null, CancellationToken.None).ConfigureAwait(false);

            var idle = link.WaitForCaptureIdleAsync(CancellationToken.None);
            Assert.That(idle.IsCompleted, Is.False, "the capture is still exposing");

            await tcp.BroadcastAsync(FrameComplete("frame_1.fits")).ConfigureAwait(false);
            await idle.WaitAsync(Timeout).ConfigureAwait(false);
        }

        [Test]
        public async Task A_capture_that_never_ends_is_given_up_on_and_the_wait_says_the_guide_camera_is_busy() {
            await using var tcp = FakeGuider.Start();
            tcp.OnRpc("capture_single_frame", JsonValue.Create(0)); // ack, never complete
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);
            _guider!.RequireConnectedGuider().CaptureBusyGrace = TimeSpan.FromMilliseconds(300);
            await link.CaptureFrameAsync(50, null, CancellationToken.None).ConfigureAwait(false);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.WaitForCaptureIdleAsync(CancellationToken.None).WaitAsync(Timeout)).ConfigureAwait(false);

            Assert.That(ex!.Message, Does.Contain("guide camera is still busy"));
        }

        [Test]
        public async Task The_capture_asks_for_a_daemon_side_save_and_the_http_port_derives_from_the_rpc_port() {
            // §45 capture-fetch: no path on the wire (the daemon's sandbox rejects a foreign one), and the saved
            // frame is fetched from the daemon's HTTP capture endpoint, whose port derives from the RPC port.
            await using var tcp = FakeGuider.Start();
            var requests = new ConcurrentQueue<JsonObject>();
            tcp.OnRpc("capture_single_frame", req => {
                requests.Enqueue((JsonObject)(req["params"] as JsonObject ?? new JsonObject()).DeepClone());
                return JsonValue.Create(0);
            });
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            await link.CaptureFrameAsync(50, 2, CancellationToken.None).ConfigureAwait(false);

            Assert.That(requests.TryDequeue(out var request), Is.True);
            Assert.That(request!.ContainsKey("path"), Is.False, "captures must not pass a foreign path");
            Assert.That(link.RpcPort, Is.EqualTo(tcp.Port));
            // The real HTTP derivation, pinned with REALISTIC daemon ports (instance 1 and 2).
            Assert.That(PHD2Guider.CaptureFrameUri("rc91.lan", 4400, "f.fits").ToString(),
                Is.EqualTo("http://rc91.lan:8080/api/capture/f.fits"));
            Assert.That(PHD2Guider.CaptureFrameUri("rc91.lan", 4401, "f.fits").Port, Is.EqualTo(8081));
        }

        [TestCase("[1280, 960]", 1280, 960)]
        [TestCase("[0, 0]", 0, 0)]
        public async Task The_frame_size_answer_is_a_size_only_when_both_sides_are_positive(string answer, int width, int height) {
            // #1730: a guider with no camera frame yet answers [0, 0] — the log said "guide camera frame 0×0".
            await using var tcp = FakeGuider.Start();
            tcp.OnRpc("get_camera_frame_size", JsonNode.Parse(answer));
            var link = await ConnectedLinkAsync(tcp).ConfigureAwait(false);

            var size = await link.GetCameraFrameSizeAsync(CancellationToken.None).ConfigureAwait(false);

            if (width > 0) {
                Assert.That(size, Is.EqualTo((width, height)));
            } else {
                Assert.That(size, Is.Null, "[0, 0] is no size");
            }
        }
    }
}