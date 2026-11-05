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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 / #1611 — <see cref="PolarAlignService"/> as the driver of the guider's own Static PA
    /// routine, against the bench <see cref="FakeGuider"/> scripting <c>loop</c>, <c>find_star</c>,
    /// <c>staticpa_*</c> and the PA-session lease, with a mocked mount for manual mode. Covers the
    /// start handshake, the fit → adjusting readout, star-lost pause + resume, manual mode's two RA
    /// turns, failure when the guider closes its tool, and the hand-back on Stop / Done.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback sockets: not part of the quick unit run
    [Category("bench")]
    public class PolarAlignServiceTest {
        private const double SiteLatDeg = 45.0;
        private const double SiteLonDeg = -75.0;
        private static readonly bool[] AutoThenManual = { true, false };
        private static readonly int[] PointsTwoAndThree = { 2, 3 };

        private sealed class WsRecorder : IWsBroadcaster {
            public ConcurrentQueue<(string Type, JsonElement Payload)> Events { get; } = new();
            public long CurrentSequence => 0;

            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                Events.Enqueue((eventType, payload.Clone()));
                return Task.CompletedTask;
            }

            public int Count(string type) => Events.Count(e => e.Type == type);
        }

        private sealed class RecordingLog : IPolarAlignmentLog {
            public ConcurrentQueue<PolarAlignmentRecord> Rows { get; } = new();

            public Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
                Rows.Enqueue(record);
                return Task.CompletedTask;
            }

            public Task<PolarAlignmentRecord?> GetLatestMeasuredAsync(DateTimeOffset since, CancellationToken ct) =>
                Task.FromResult<PolarAlignmentRecord?>(null);
        }

        private sealed class FakeFrameFetcher : IPolarAlignFrameFetcher {
            public static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };
            public bool Fail { get; set; }

            public Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) =>
                throw new NotSupportedException("polar alignment no longer fetches solver frames");

            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) =>
                Fail ? Task.FromException<byte[]>(new HttpRequestException("404 for frame.jpg")) : Task.FromResult(Jpeg);
        }

        /// <summary>The guider's Static PA tool as the fake plays it: <see cref="Status"/> is what
        /// <c>staticpa_get_status</c> answers; <see cref="Calc"/> and <see cref="LoseStar"/> script the
        /// fit and the live star between polls.</summary>
        private sealed class ScriptedTool {
            private readonly object _gate = new();
            private bool _active;
            private bool _auto;
            private bool _calced;
            private bool _starVisible = true;
            private readonly ConcurrentQueue<int> _measured = new();

            public bool CanSlew { get; set; } = true;
            public ConcurrentQueue<JsonObject> StartParams { get; } = new();
            public ConcurrentQueue<int> MeasureRequests => _measured;
            public double AltArcmin { get; set; } = 3.5;
            public double AzArcmin { get; set; } = -1.2;

            public void Calc() { lock (_gate) { _calced = true; } }
            public void LoseStar(bool lost) { lock (_gate) { _starVisible = !lost; } }
            public void CloseFromGuiderSide() { lock (_gate) { _active = false; } }

            public void Install(FakeGuider fake) {
                fake.OnRpc("staticpa_start", req => {
                    var p = req["params"] as JsonObject ?? new JsonObject();
                    StartParams.Enqueue((JsonObject)p.DeepClone());
                    var auto = p["auto"]?.GetValue<bool>() ?? true;
                    if (auto && !CanSlew) {
                        throw new InvalidOperationException("mount cannot slew - use manual mode (auto:false)");
                    }
                    lock (_gate) {
                        _active = true;
                        _auto = auto; // a scripted Calc() before the start lands is kept: the test's fit, not the tool's
                    }
                    return Status();
                });
                fake.OnRpc("staticpa_measure", req => {
                    var pos = req["params"]?["position"]?.GetValue<int>() ?? 0;
                    _measured.Enqueue(pos);
                    return Status();
                });
                fake.OnRpc("staticpa_get_status", _ => Status());
                fake.OnRpc("staticpa_stop", _ => Status());
                fake.OnRpc("staticpa_close", _ => { lock (_gate) { _active = false; } return JsonValue.Create(0); });
            }

            private JsonObject Status() {
                lock (_gate) {
                    var o = new JsonObject { ["active"] = _active };
                    if (!_active) {
                        return o;
                    }
                    o["aligning"] = !_calced;
                    o["auto"] = _auto;
                    o["can_slew"] = CanSlew;
                    o["hemisphere"] = "north";
                    o["pixel_scale"] = 3.9;
                    o["ref_star"] = 0;
                    var pts = new JsonArray();
                    foreach (var p in _measured.Distinct()) {
                        pts.Add(new JsonObject { ["position"] = p, ["x"] = 10.0, ["y"] = 10.0 });
                    }
                    o["measured_points"] = pts;
                    o["calced"] = _calced;
                    if (!_calced) {
                        return o;
                    }
                    o["centre"] = new JsonObject { ["x"] = 640.0, ["y"] = 480.0, ["radius_px"] = 120.0 };
                    o["adjustment"] = Adjustment();
                    if (_starVisible) {
                        o["current_star"] = new JsonObject { ["x"] = 650.0, ["y"] = 470.0 };
                        o["live_adjustment"] = Adjustment();
                    }
                    return o;
                }
            }

            private JsonObject Adjustment() => new() {
                ["alt_error_px"] = AltArcmin * 60 / 3.9,
                ["az_error_px"] = AzArcmin * 60 / 3.9,
                ["total_error_px"] = 1.0,
                ["alt_error_arcmin"] = AltArcmin,
                ["az_error_arcmin"] = AzArcmin,
                ["total_error_arcmin"] = Math.Sqrt(AltArcmin * AltArcmin + AzArcmin * AzArcmin),
            };
        }

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        private static (FakeGuider Fake, ScriptedTool Tool) StartFake(ConcurrentQueue<bool> paSessionActiveCalls, bool starFound = true) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(3.9));
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                paSessionActiveCalls.Enqueue(active);
                return new JsonObject { ["active"] = active, ["expires_in_s"] = active ? 600 : null };
            });
            fake.OnRpc("find_star", _ => starFound
                ? new JsonArray(512.5, 384.25)
                : throw new InvalidOperationException("no star found"));
            var tool = new ScriptedTool();
            tool.Install(fake);
            return (fake, tool);
        }

        private static Mock<ITelescopeMediator> NewMount(bool connected = true) {
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo {
                Connected = connected, SiderealTime = 20.0, TrackingEnabled = true,
            });
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => position = target)
                .ReturnsAsync(true);
            mount.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            return mount;
        }

        private static InMemoryProfileStore NewStore() {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = SiteLatDeg, LongitudeDeg = SiteLonDeg });
            store.PutPolarAlignSettings(new PolarAlignSettingsDto(
                ExposureSeconds: 0.01, LoopCadenceMs: 100, SettleSeconds: 0));
            return store;
        }

        private static PolarAlignService NewService(GuiderService guider, Mock<ITelescopeMediator>? mount = null,
                IWsBroadcaster? ws = null, IPolarAlignmentLog? log = null, IGuideFocusService? guideFocus = null) =>
            new(guider, NullLogger<PolarAlignService>.Instance, (mount ?? NewMount()).Object, NewStore(), ws, log,
                new FakeFrameFetcher(), guideFocus: guideFocus is null ? null : () => guideFocus) {
                FrameWaitSlack = TimeSpan.FromMilliseconds(10),
            };

        private static async Task<GuiderService> ConnectGuiderAsync(FakeGuider fake) {
            var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!cts.IsCancellationRequested) {
                var dto = await svc.GetAsync(cts.Token).ConfigureAwait(false);
                if (dto?.State == EquipmentConnectionState.Connected) {
                    return svc;
                }
                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
            Assert.Fail("the guider never reached Connected against the fake");
            return svc;
        }

        private static async Task<PolarAlignStateDto> PollStateAsync(PolarAlignService svc, params string[] anyOf) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var status = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
            while (!anyOf.Contains(status.State)) {
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
                status = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
            }
            return status;
        }

        private static async Task WaitForRpcAsync(FakeGuider fake, string method, int atLeast = 1) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (fake.ReceivedMethods.Count(m => m == method) < atLeast) {
                await Task.Delay(20, cts.Token).ConfigureAwait(false);
            }
        }

        private static async Task<JsonElement> WaitForEventAsync(WsRecorder ws, string type) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true) {
                var hit = ws.Events.FirstOrDefault(e => e.Type == type);
                if (hit.Type is not null) {
                    return hit.Payload;
                }
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
            }
        }

        // ── lifecycle ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Status_is_idle_before_start() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = NewService(guider);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("idle"));
            Assert.That(status.FramesCaptured, Is.EqualTo(0));
        }

        [Test]
        public async Task Start_without_a_connected_guider_throws() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = NewService(guider);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Start_without_a_connected_mount_throws_before_touching_the_guider() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, NewMount(connected: false));

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("loop"), "a failed preflight must not touch the guider");
        }

        [Test]
        public async Task Start_loops_picks_a_star_and_starts_the_guider_tool_in_auto_mode_without_a_lease() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("seeding"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.EqualTo(1));
            await WaitForRpcAsync(fake, "staticpa_start").ConfigureAwait(false);
            var methods = fake.ReceivedMethods.ToList();
            Assert.That(methods.IndexOf("loop"), Is.LessThan(methods.IndexOf("find_star")), "loop before the star pick");
            Assert.That(methods.IndexOf("find_star"), Is.LessThan(methods.IndexOf("staticpa_start")), "the star pick before the tool starts");
            Assert.That(methods, Does.Contain("set_exposure"));
            Assert.That(methods, Does.Not.Contain("set_pa_session"), "the lease would block loop, which the guider's tool needs");
            Assert.That(tool.StartParams.TryDequeue(out var p), Is.True);
            Assert.That(p!["auto"]!.GetValue<bool>(), Is.True);
            Assert.That(p["hemisphere"]!.GetValue<string>(), Is.EqualTo("north"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Start_stops_a_running_guide_camera_focus_loop_first() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var focus = new Mock<IGuideFocusService>();
            focus.SetupGet(f => f.IsActive).Returns(true);
            focus.Setup(f => f.StopAsync()).Returns(Task.CompletedTask);
            using var svc = NewService(guider, guideFocus: focus.Object);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            focus.Verify(f => f.StopAsync(), Times.Once);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Start_is_idempotent_and_starts_the_guider_tool_only_once() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            await WaitForRpcAsync(fake, "staticpa_start").ConfigureAwait(false);
            Assert.That(fake.ReceivedMethods.Count(m => m == "staticpa_start"), Is.EqualTo(1));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // ── the readout ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_guider_fit_moves_the_routine_to_adjusting_with_its_live_errors() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, ws: ws);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await WaitForRpcAsync(fake, "staticpa_get_status").ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("seeding"),
                "no fit yet: still seeding");

            tool.Calc();

            var status = await PollStateAsync(svc, "adjusting").ConfigureAwait(false);
            Assert.That(status.AltitudeAdjustmentArcmin, Is.EqualTo(3.5).Within(1e-9));
            Assert.That(status.AzimuthAdjustmentArcmin, Is.EqualTo(-1.2).Within(1e-9));
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(Math.Sqrt(3.5 * 3.5 + 1.2 * 1.2)).Within(1e-9));
            Assert.That(status.WaitingForCapture, Is.False);
            var progress = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignProgress).ConfigureAwait(false);
            Assert.That(progress.GetProperty("altitude_error_arcmin").GetDouble(), Is.EqualTo(3.5).Within(1e-9));
            Assert.That(progress.GetProperty("zone").GetString(), Is.EqualTo("green"));

            // The user turns a knob: the guider's live decomposition changes, Ara relays it.
            tool.AltArcmin = 0.4;
            var later = await PollStateAsync(svc, "adjusting").ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (later.AltitudeAdjustmentArcmin is not double a || Math.Abs(a - 0.4) > 1e-9) {
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
                later = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
            }
            Assert.That(later.FramesCaptured, Is.GreaterThan(1));
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Losing_the_star_pauses_after_five_polls_and_resumes_when_it_is_back() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, ws: ws);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            tool.Calc();
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);

            tool.LoseStar(true);
            var paused = await PollStateAsync(svc, "paused").ConfigureAwait(false);
            Assert.That(paused.AltitudeAdjustmentArcmin, Is.EqualTo(3.5).Within(1e-9), "the last readout stays on show while paused");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignPaused), Is.EqualTo(1));

            tool.LoseStar(false);
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Capture_request_is_a_no_op_in_the_adjust_phase_and_409_outside_it() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RequestCaptureAsync(null, CancellationToken.None));
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            tool.Calc();
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);

            var accepted = await svc.RequestCaptureAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(accepted.OperationType, Is.EqualTo("polar-align.capture"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // ── manual mode ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_guider_without_a_slewable_mount_runs_manual_mode_with_two_RA_turns_by_Ara() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            tool.CanSlew = false;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var mount = NewMount();
            using var svc = NewService(guider, mount);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            await WaitForRpcAsync(fake, "staticpa_measure", atLeast: 2).ConfigureAwait(false);

            Assert.That(tool.StartParams.Select(p => p["auto"]!.GetValue<bool>()), Is.EqualTo(AutoThenManual),
                "auto first, then manual after the guider's refusal");
            Assert.That(tool.MeasureRequests, Is.EqualTo(PointsTwoAndThree));
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
            Assert.That(fake.ReceivedMethods.Count(m => m == "find_star"), Is.GreaterThanOrEqualTo(3), "the star is re-picked after each turn");

            tool.Calc();
            var status = await PollStateAsync(svc, "adjusting").ConfigureAwait(false);
            Assert.That(status.AltitudeAdjustmentArcmin, Is.EqualTo(3.5).Within(1e-9));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        // ── failures ─────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task No_star_after_the_retries_fails_the_routine() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls, starFound: false);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, ws: ws);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("no_star"));
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("staticpa_start"));
            Assert.That(status.State, Is.EqualTo("failed"));
        }

        [Test]
        public async Task The_guider_closing_its_tool_fails_the_routine() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewService(guider, ws: ws, log: log);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            tool.Calc();
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);

            tool.CloseFromGuiderSide();

            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("guider_closed"));
            Assert.That(log.Rows.Single().Outcome, Is.EqualTo("failed"));
        }

        // ── hand-back ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Stop_closes_the_guider_tool_stops_looping_and_logs_aborted() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewService(guider, ws: ws, log: log);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            tool.Calc();
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            var methods = fake.ReceivedMethods;
            Assert.That(methods, Does.Contain("staticpa_stop").And.Contain("staticpa_close").And.Contain("stop_capture"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
            var row = log.Rows.Single();
            Assert.That(row.Outcome, Is.EqualTo("aborted"));
            Assert.That(row.FinalAltErrorArcmin, Is.EqualTo(3.5).Within(1e-9));
            Assert.That(row.Iterations, Is.GreaterThan(0));
        }

        [Test]
        public async Task Done_logs_the_achieved_error_as_complete() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, tool) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var log = new RecordingLog();
            using var svc = NewService(guider, log: log);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            tool.Calc();
            await PollStateAsync(svc, "adjusting").ConfigureAwait(false);

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            var row = log.Rows.Single();
            Assert.That(row.Outcome, Is.EqualTo("complete"));
            Assert.That(row.FinalErrorArcmin, Is.EqualTo(Math.Sqrt(3.5 * 3.5 + 1.2 * 1.2)).Within(1e-9));
        }

        [Test]
        public async Task Stop_when_idle_is_accepted_and_writes_no_row() {
            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var log = new RecordingLog();
            using var svc = NewService(guider, log: log);

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows, Is.Empty);
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("staticpa_stop"));
        }

        // ── live view ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Live_frame_is_served_from_the_guider_and_null_without_one() {
            using var idle = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var none = NewService(idle);
            Assert.That(await none.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false), Is.Null);

            var paCalls = new ConcurrentQueue<bool>();
            var (fake, _) = StartFake(paCalls);
            await using var _f = fake;
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider);
            Assert.That(await svc.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false), Is.EqualTo(FakeFrameFetcher.Jpeg));
        }
    }
}
