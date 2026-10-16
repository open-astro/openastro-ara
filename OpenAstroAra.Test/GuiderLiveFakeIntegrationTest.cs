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
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1364 item 1 — the live guide-camera view against the bench's <see cref="FakeGuider"/>: the
    /// real <see cref="GuiderService"/> / <c>PHD2Guider</c> over the loopback PHD2 wire protocol, so the
    /// RPC payloads and the connected read path are pinned, not only the disconnected contract.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback sockets: not part of the quick unit run
    [Category("bench")]
    public class GuiderLiveFakeIntegrationTest {

        private sealed class NoFrames : IPolarAlignFrameFetcher {
            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) => throw new System.Net.Http.HttpRequestException("404");
        }

        /// <summary>A guider whose HTTP side is wedged: the fetcher's own 20 s HttpClient timeout surfaces
        /// as a TaskCanceledException while the request's token is still live.</summary>
        private sealed class TimingOutFrames : IPolarAlignFrameFetcher {
            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) => Task.CompletedTask;
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) => throw new TaskCanceledException("HttpClient.Timeout");
        }

        private static async Task<(FakeGuider Fake, GuiderService Svc)> ConnectedAsync(Action<FakeGuider>? setup = null) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "My Equipment" });
            fake.OnRpc("get_profiles", _ => new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "My Equipment" }));
            fake.OnRpc("create_profile", _ => new JsonObject { ["id"] = 5, ["name"] = "Ara", ["selected"] = true });
            fake.OnRpc("get_connected", JsonValue.Create(true));
            setup?.Invoke(fake);
            var svc = new GuiderService(new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().PixelScale > 0).ConfigureAwait(false), Is.True, "precondition: connected");
            return (fake, svc);
        }

        private static void LiveRpcs(FakeGuider fake) {
            fake.OnRpc("get_camera_frame_size", _ => new JsonArray(1280, 960));
            fake.OnRpc("get_lock_position", _ => new JsonArray(640.123, 480.456));
            fake.OnRpc("get_search_region", JsonValue.Create(15));
            fake.OnRpc("get_guider_options", _ => new JsonObject { ["multistar_enabled"] = true, ["FastRecenter"] = true });
            fake.OnRpc("get_star_centroids", _ => new JsonArray(
                new JsonObject { ["x"] = 640.12345, ["y"] = 480.45678, ["snr"] = 42.0, ["mass"] = 12000.0, ["hfd"] = 2.1 },
                new JsonObject { ["x"] = 100.009, ["y"] = 200.0, ["snr"] = 11.0 },
                new JsonObject { ["y"] = 5.0 })); // no x: dropped
        }

        [Test]
        public async Task The_connected_view_reads_every_field_and_rounds_the_positions() {
            var (fake, svc) = await ConnectedAsync(LiveRpcs).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            await fake.BroadcastAsync(PhdEvents.AppState("Looping")).ConfigureAwait(false);
            var step = PhdEvents.GuideStep(0.1, -0.2);
            step["SNR"] = 38.5;
            step["StarMass"] = 11500.0;
            await fake.BroadcastAsync(step).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.LatestStarStats.Snr is not null).ConfigureAwait(false), Is.True,
                "the guide step's SNR and mass feed the view");
            // GuideStep folds the guider into Guiding: the star stats are reported while it guides.
            Assert.That(await WaitUntilAsync(() => svc.ConnectedGuiderOrNull()?.AppState?.State == "Guiding").ConfigureAwait(false), Is.True);

            var view = await new GuiderLiveService(svc, new NoFrames()).GetAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(view.Connected, Is.True);
            Assert.That(view.AppState, Is.EqualTo("Guiding"));
            Assert.That(view.HasFrame, Is.True, "anything but Stopped has a picture");
            Assert.That((view.FrameWidth, view.FrameHeight), Is.EqualTo((1280, 960)));
            Assert.That((view.LockX, view.LockY), Is.EqualTo((640.12, 480.46)));
            Assert.That(view.SearchRegion, Is.EqualTo(15));
            Assert.That(view.MultiStarEnabled, Is.True);
            Assert.That(view.Snr, Is.EqualTo(38.5));
            Assert.That(view.StarMass, Is.EqualTo(11500));
            Assert.That(view.Stars.Select(s => (s.X, s.Y)), Is.EqualTo(new[] { (640.12, 480.46), (100.01, 200.0) }),
                "rounded to 0.01 px; a centroid without x is dropped");
            Assert.That(view.Stars[0].Hfd, Is.EqualTo(2.1));

            // Looping again: the last guide frame's stats no longer describe anything.
            await fake.BroadcastAsync(PhdEvents.AppState("Looping")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.ConnectedGuiderOrNull()?.AppState?.State == "Looping").ConfigureAwait(false), Is.True);
            var looping = await new GuiderLiveService(svc, new NoFrames()).GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That((looping.Snr, looping.StarMass), Is.EqualTo(((double?)null, (double?)null)));
        }

        private sealed class RecordingSink : OpenAstroAra.Core.Utility.Notification.INotifierSink {
            public readonly ConcurrentQueue<string> Messages = new();
            public void Notify(bool isError, string? header, string message) => Messages.Enqueue(message);
        }

        [Test]
        [NonParallelizable] // Notifier.Use is process-global
        public async Task Refused_live_reads_raise_no_notification() {
            var (fake, svc) = await ConnectedAsync(f => {
                f.OnRpc("get_camera_frame_size", _ => throw new InvalidOperationException("camera not connected (live view)"));
                f.OnRpc("get_star_centroids", _ => throw new InvalidOperationException("no image available (live view)"));
                f.OnRpc("get_lock_position", _ => throw new InvalidOperationException("no lock position (live view)"));
                f.OnRpc("get_search_region", _ => throw new InvalidOperationException("no search region (live view)"));
                f.OnRpc("get_guider_options", _ => throw new InvalidOperationException("no options (live view)"));
            }).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var sink = new RecordingSink();
            var previous = OpenAstroAra.Core.Utility.Notification.Notifier.Current;
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(sink);
            try {
                var view = await new GuiderLiveService(svc, new NoFrames()).GetAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.That(view.Connected, Is.True);
                Assert.That((view.FrameWidth, view.LockX, view.SearchRegion, view.MultiStarEnabled), Is.EqualTo(((int?)null, (double?)null, (int?)null, (bool?)null)));
                Assert.That(sink.Messages.Where(m => m.Contains("(live view)", StringComparison.Ordinal)), Is.Empty,
                    "an expected refusal while the dialog polls must not post an inbox Error");
            } finally {
                OpenAstroAra.Core.Utility.Notification.Notifier.Use(previous);
            }
        }

        [Test]
        public async Task The_calibration_view_reads_the_stored_calibration_and_judges_it() {
            // #1364 item 2 — get_calibration_data over the wire: rates px/s, angles degrees.
            var (fake, svc) = await ConnectedAsync(f => f.OnRpc("get_calibration_data", _ => new JsonObject {
                ["calibrated"] = true, ["xAngle"] = 172.4, ["yAngle"] = 84.1, ["xRate"] = 12.1, ["yRate"] = 11.6,
                ["xParity"] = "+", ["yParity"] = "-", ["declination"] = 44.2,
            })).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var view = await new GuiderLiveService(svc, new NoFrames()).GetCalibrationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(view.Connected, Is.True);
            Assert.That(view.RunState, Is.EqualTo("none"), "no calibration ran this session");
            Assert.That(view.Data, Is.Not.Null);
            Assert.That(view.Data!.Calibrated, Is.True);
            Assert.That((view.Data.XAngleDeg, view.Data.YAngleDeg, view.Data.DeclinationDeg), Is.EqualTo(((double?)172.4, (double?)84.1, (double?)44.2)));
            Assert.That(view.Data.OrthogonalityErrorDeg, Is.EqualTo(1.7));
            Assert.That(view.Verdict, Is.EqualTo("good"));
        }

        [Test]
        public async Task Clear_calibration_sends_mount_and_a_refusal_is_422() {
            var received = new ConcurrentQueue<JsonNode?>();
            var refuse = false;
            var (fake, svc) = await ConnectedAsync(f => f.OnRpc("clear_calibration", req => {
                if (refuse) {
                    throw new InvalidOperationException("mount not defined");
                }
                received.Enqueue(req["params"]?.DeepClone());
                return JsonValue.Create(0);
            })).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var live = new GuiderLiveService(svc, new NoFrames());
            // A finished run on the plot, then Clear: the run starts over with the calibration.
            var guider = svc.ConnectedGuiderOrNull()!;
            await fake.BroadcastAsync(new JsonObject { ["Event"] = "StartCalibration", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1 }).ConfigureAwait(false);
            await fake.BroadcastAsync(new JsonObject {
                ["Event"] = "Calibrating", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1,
                ["dir"] = "West", ["step"] = 1, ["dist"] = 1.2, ["dx"] = -1.2, ["dy"] = 0.1,
            }).ConfigureAwait(false);
            await fake.BroadcastAsync(new JsonObject { ["Event"] = "CalibrationComplete", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1 }).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.CalibrationRun.State == "complete").ConfigureAwait(false), Is.True, "precondition");
            var ok = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(
                () => live.ClearCalibrationAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.That((ok as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(204));
            Assert.That(received.TryDequeue(out var sent), Is.True, "clear_calibration reached the guider");
            Assert.That(sent!.ToJsonString(), Is.EqualTo("[\"Mount\"]"), "the mount's calibration, not the AO's");
            Assert.That(guider.CalibrationRun.State, Is.EqualTo("none"), "a successful clear resets the run");
            Assert.That(guider.CalibrationRun.Steps, Is.Empty);

            refuse = true;
            var refused = await OpenAstroAra.Server.Endpoints.EquipmentEndpoints.GuiderLiveActionAsync(
                () => live.ClearCalibrationAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.That((refused as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode, Is.EqualTo(422));
        }

        private static readonly int[] OnlyStepTwo = [2];

        [Test]
        public async Task Steps_without_dx_dy_are_left_off_the_plot() {
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var guider = svc.ConnectedGuiderOrNull()!;
            await fake.BroadcastAsync(new JsonObject { ["Event"] = "StartCalibration", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1 }).ConfigureAwait(false);
            await fake.BroadcastAsync(new JsonObject {
                ["Event"] = "Calibrating", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1, ["dir"] = "West", ["step"] = 1, ["dist"] = 1.0,
            }).ConfigureAwait(false);
            await fake.BroadcastAsync(new JsonObject {
                ["Event"] = "Calibrating", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1, ["dir"] = "West", ["step"] = 2, ["dist"] = 2.0, ["dx"] = -2.0, ["dy"] = 0.2,
            }).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.CalibrationRun.Steps.Count == 2).ConfigureAwait(false), Is.True, "precondition");
            var view = await new GuiderLiveService(svc, new NoFrames()).GetCalibrationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(view.Steps.Select(s => s.Step), Is.EqualTo(OnlyStepTwo), "the step the guider sent no offset for is not drawn at the origin");
        }

        [Test]
        public async Task A_calibration_run_cut_by_a_link_drop_ends_failed() {
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var guider = svc.ConnectedGuiderOrNull()!;
            await fake.BroadcastAsync(new JsonObject { ["Event"] = "StartCalibration", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1 }).ConfigureAwait(false);
            await fake.BroadcastAsync(new JsonObject {
                ["Event"] = "Calibrating", ["Timestamp"] = 0.0, ["Host"] = "fake", ["Inst"] = 1,
                ["dir"] = "West", ["step"] = 4, ["dist"] = 5.2, ["dx"] = -5.0, ["dy"] = -1.1,
            }).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.CalibrationRun.Steps.Count == 1).ConfigureAwait(false), Is.True, "precondition: a running calibration");
            Assert.That(guider.CalibrationRun.State, Is.EqualTo("running"));

            Assert.That(fake.DropConnections(), Is.GreaterThan(0));
            Assert.That(await WaitUntilAsync(() => guider.CalibrationRun.State != "running").ConfigureAwait(false), Is.True,
                "the guider sends no CalibrationFailed when the link drops");
            Assert.That(guider.CalibrationRun.State, Is.EqualTo("failed"));
            Assert.That(guider.CalibrationRun.Error, Does.Contain("connection lost"));
            Assert.That(guider.CalibrationRun.Steps, Has.Count.EqualTo(1), "the steps stay for review");
        }

        [Test]
        [NonParallelizable] // Notifier.Use is process-global
        public async Task A_refused_calibration_read_raises_no_notification() {
            var (fake, svc) = await ConnectedAsync(f =>
                f.OnRpc("get_calibration_data", _ => throw new InvalidOperationException("mount not defined (calibration view)"))).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var sink = new RecordingSink();
            var previous = OpenAstroAra.Core.Utility.Notification.Notifier.Current;
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(sink);
            try {
                var view = await new GuiderLiveService(svc, new NoFrames()).GetCalibrationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.That(view.Data, Is.Null);
                Assert.That(view.Verdict, Is.EqualTo("redo"));
                Assert.That(sink.Messages.Where(m => m.Contains("(calibration view)", StringComparison.Ordinal)), Is.Empty,
                    "the dialog polls this every second: a refusal must not post an inbox Error");
            } finally {
                OpenAstroAra.Core.Utility.Notification.Notifier.Use(previous);
            }
        }

        [Test]
        public async Task A_refused_read_leaves_its_field_null_and_the_rest_answers() {
            var (fake, svc) = await ConnectedAsync(f => {
                LiveRpcs(f);
                f.OnRpc("get_camera_frame_size", _ => throw new InvalidOperationException("camera not connected"));
                f.OnRpc("get_star_centroids", _ => throw new InvalidOperationException("no image available"));
            }).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var view = await new GuiderLiveService(svc, new NoFrames()).GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(view.Connected, Is.True);
            Assert.That(view.FrameWidth, Is.Null);
            Assert.That(view.Stars, Is.Empty);
            Assert.That(view.SearchRegion, Is.EqualTo(15), "the other reads still answer");
            Assert.That(view.LockX, Is.EqualTo(640.12));
            Assert.That(view.HasFrame, Is.False, "Stopped has no picture");
            Assert.That(view.Snr, Is.Null, "no guide step yet");
        }

        [Test]
        public async Task Select_sends_the_positional_lock_with_snap_and_refuses_off_frame() {
            var received = new ConcurrentQueue<JsonNode?>();
            var (fake, svc) = await ConnectedAsync(f => {
                LiveRpcs(f);
                f.OnRpc("set_lock_position", req => { received.Enqueue(req["params"]?.DeepClone()); return JsonValue.Create(0); });
            }).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var live = new GuiderLiveService(svc, new NoFrames());

            await live.SelectStarAsync(320.5, 240.25, CancellationToken.None).ConfigureAwait(false);
            Assert.That(received.TryDequeue(out var p), Is.True);
            Assert.That(p!.ToJsonString(), Is.EqualTo("[320.5,240.25,false]"), "[x, y, exact=false]: the guider snaps to the nearest star");

            await Assert.ThatAsync(async () => await live.SelectStarAsync(1280, 10, CancellationToken.None).ConfigureAwait(false),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "outside the 1280×960 frame the guider reports");
            await Assert.ThatAsync(async () => await live.SelectStarAsync(10, 960.5, CancellationToken.None).ConfigureAwait(false),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(received, Is.Empty, "an off-frame tap never reaches the guider");
        }

        [Test]
        public async Task Select_a_guider_refusal_is_a_GuiderRpcException() {
            var (fake, svc) = await ConnectedAsync(f => {
                LiveRpcs(f);
                f.OnRpc("set_lock_position", _ => throw new InvalidOperationException("could not set lock position"));
            }).ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            await Assert.ThatAsync(async () => await new GuiderLiveService(svc, new NoFrames()).SelectStarAsync(10, 10, CancellationToken.None).ConfigureAwait(false),
                Throws.TypeOf<GuiderRpcException>());
        }

        [Test]
        public async Task Loop_connects_the_guider_equipment_first() {
            var connected = false;
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            fake.OnRpc("get_connected", _ => JsonValue.Create(connected));
            fake.OnRpc("set_connected", _ => { connected = true; return JsonValue.Create(0); });
            var before = fake.ReceivedMethods.Count;

            await new GuiderLiveService(svc, new NoFrames()).LoopAsync(CancellationToken.None).ConfigureAwait(false);

            var calls = fake.ReceivedMethods.Skip(before).ToList();
            Assert.That(calls, Does.Contain("set_connected"));
            Assert.That(calls.IndexOf("set_connected"), Is.LessThan(calls.IndexOf("loop")), "equipment first, then loop");
        }

        [Test]
        public async Task Loop_says_so_when_the_guider_cannot_connect_its_equipment() {
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            fake.OnRpc("get_connected", JsonValue.Create(false));
            fake.OnRpc("set_connected", _ => throw new InvalidOperationException("No iOptron mount found"));
            var before = fake.ReceivedMethods.Count;
            var ex = await Assert.ThrowsAsync<GuiderRpcException>(async () =>
                await new GuiderLiveService(svc, new NoFrames()).LoopAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.That(ex!.Message, Does.Contain("could not connect its camera and mount"));
            Assert.That(fake.ReceivedMethods.Skip(before), Does.Not.Contain("loop"), "no loop without equipment");
        }

        [Test]
        public async Task A_wedged_guider_http_side_is_no_picture_not_a_500() {
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            Assert.That(await new GuiderLiveService(svc, new TimingOutFrames()).GetFrameJpegAsync(CancellationToken.None).ConfigureAwait(false),
                Is.Null);
            Assert.That(await new GuiderLiveService(svc, new NoFrames()).GetFrameJpegAsync(CancellationToken.None).ConfigureAwait(false),
                Is.Null);
        }

        [Test]
        public async Task The_star_stats_start_empty_on_each_connect() {
            var (fake, svc) = await ConnectedAsync().ConfigureAwait(false);
            await using var _ = fake;
            using var __ = svc;
            var step = PhdEvents.GuideStep(0.1, -0.2);
            step["SNR"] = 30.0;
            step["StarMass"] = 9000.0;
            await fake.BroadcastAsync(step).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.LatestStarStats.Snr == 30.0).ConfigureAwait(false), Is.True);

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.ConnectedGuiderOrNull() is null).ConfigureAwait(false), Is.True, "disconnected");
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.ConnectedGuiderOrNull() is not null).ConfigureAwait(false), Is.True, "reconnected");
            Assert.That(svc.LatestStarStats.Snr, Is.Null, "a new session does not show the last session's star");
            Assert.That(svc.LatestStarStats.Mass, Is.Null);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested) {
                if (condition()) {
                    return true;
                }
                try {
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
            return condition();
        }
    }
}
