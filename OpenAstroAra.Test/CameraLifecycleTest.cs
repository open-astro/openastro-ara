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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 — the Camera's lifecycle on the shared module, against a fake camera: connect,
    /// disconnect, forget, supersede, the §42.3 probe, read health, the #1246 clean-read gate (including a driver
    /// without CameraState), the #1539 desired connection state, recovery leaving a user-disconnected camera
    /// alone, and the exposure abort a teardown makes only while a capture is in flight. No test here dials a port.</summary>
    [TestFixture]
    public class CameraLifecycleTest {

        private static readonly DiscoveredDeviceDto Cam = new(
            UniqueId: "cam-1", Name: "Bench Camera", Type: DeviceType.Camera,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private static readonly DiscoveredDeviceDto OtherCam = Cam with { UniqueId = "cam-2", Name = "Other Camera" };

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private readonly List<FakeCameraDevice> _devices = new();
        private readonly List<string> _trace = new();
        private FaultSink _faults = null!;
        private Action<FakeCameraDevice>? _configure;

        [SetUp]
        public void SetUp() {
            _devices.Clear();
            _trace.Clear();
            _faults = new FaultSink();
            _configure = null;
        }

        private FakeCameraDevice Current { get { lock (_devices) { return _devices[^1]; } } }
        private int Built { get { lock (_devices) { return _devices.Count; } } }
        private List<string> Trace { get { lock (_trace) { return _trace.ToList(); } } }

        private CameraService Build(Func<DiscoveredDeviceDto, FakeCameraDevice>? make = null, IFrameRepository? frames = null) =>
            new(frames: frames, faults: _faults) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = d => {
                    var fake = make?.Invoke(d) ?? new FakeCameraDevice();
                    fake.Trace = e => { lock (_trace) { _trace.Add($"{d.UniqueId}:{e}"); } };
                    _configure?.Invoke(fake);
                    lock (_devices) { _devices.Add(fake); }
                    return fake;
                },
            };

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        private static EquipmentConnectionState? StateOf(CameraService s) => s.GetAsync(CancellationToken.None).Result?.State;

        private static Task ConnectedAsync(CameraService s) =>
            WaitForAsync(() => StateOf(s) == EquipmentConnectionState.Connected, "the connect");

        private static Task<OperationAcceptedDto> ConnectAsync(CameraService s, DiscoveredDeviceDto d) =>
            s.ConnectAsync(new ConnectRequestDto(d), null, CancellationToken.None);

        [Test]
        public async Task Connect_disconnect_and_forget_run_on_the_fake_camera() {
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the capabilities read");
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean read");

            await Assert.ThatAsync(() => svc.ForgetAsync(CancellationToken.None), Throws.InvalidOperationException, "a connected camera cannot be removed");
            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Disconnected));
            await WaitForAsync(() => Current.Disposed, "the teardown");
            Assert.That(Current.Disconnects, Is.EqualTo(1));
            Assert.That(Current.Aborts, Is.EqualTo(0), "an idle camera is not sent AbortExposure (ZWO/QHY throw from it)");

            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.True);
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task A_disconnect_aborts_the_exposure_only_while_a_capture_is_in_flight() {
            var frames = new Mock<IFrameRepository>();
            using var svc = Build(frames: frames.Object);
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            // The fake never reports ImageReady, so the capture stays in flight until the disconnect supersedes it.
            await svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null), null, CancellationToken.None);
            Assert.That(svc.IsFreeToCapture(new object()), Is.False, "a capture is in flight");

            await svc.DisconnectAsync(null, CancellationToken.None);
            await WaitForAsync(() => Current.Disposed, "the teardown");
            Assert.That(Current.Aborts, Is.GreaterThanOrEqualTo(1), "the in-flight exposure is aborted before the camera is let go");
            Assert.That(Trace.IndexOf("cam-1:disconnect"), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public async Task The_desired_connection_follows_the_users_connect_and_disconnect_and_a_fault() {
            using var svc = Build();
            Assert.That(svc.DesiredConnection, Is.Null, "nothing selected yet");
            await ConnectAsync(svc, Cam);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));
            await ConnectedAsync(svc);

            Current.Answers = false;
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the §42.3 trip");
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
            Assert.That(svc.TryRecover(Cam), Is.True, "a lost camera the user still wants is recovered");
            await ConnectedAsync(svc);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));

            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            Assert.That(svc.TryRecover(Cam), Is.False, "recovery leaves a camera the user disconnected alone");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That(Built, Is.EqualTo(2), "no connect was dispatched for the refused recovery");
        }

        [Test]
        public async Task A_dead_probe_trips_the_camera_after_the_streak_and_not_before() {
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            Current.ProbePermits = new SemaphoreSlim(0);
            Current.Answers = false;

            Current.ProbePermits.Release(DeviceConnectionProbe.DefaultLostThreshold - 1);
            await WaitForAsync(() => Current.FailedProbes >= DeviceConnectionProbe.DefaultLostThreshold - 1, "the failed probes");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "one short of the threshold");
            Assert.That(_faults.Faults, Is.Empty);

            Current.ProbePermits.Release(1);
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the trip");
            await WaitForAsync(() => _faults.Faults.Count == 1, "the fault");
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.Disconnected));
            Assert.That(_faults.Faults.Single().DeviceType, Is.EqualTo(DeviceType.Camera));
        }

        [Test]
        public async Task A_second_connect_supersedes_the_first_and_lets_it_go_before_dialing() {
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await ConnectAsync(svc, OtherCam);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.DeviceId == OtherCam.UniqueId
                && StateOf(svc) == EquipmentConnectionState.Connected, "the second camera");

            var trace = Trace;
            Assert.That(trace.IndexOf("cam-1:disconnect"), Is.GreaterThanOrEqualTo(0));
            Assert.That(trace.IndexOf("cam-1:disconnect"), Is.LessThan(trace.IndexOf("cam-2:connect")),
                $"the old Connected=false reaches the bridge before the new Connected=true ({string.Join(",", trace)})");
            Assert.That(Built, Is.EqualTo(2));
        }

        // ADR 0011 rule 2 — CameraState is the essential read: failing ticks raise degraded and hold the gate shut.
        [Test]
        public async Task A_failing_CameraState_read_degrades_the_camera_and_holds_the_clean_read_gate_shut() {
            _configure = d => d.ReadFails = true;
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await WaitForAsync(() => _faults.Faults.Count == 1, "the degraded fault after the threshold");
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
            Assert.That(_faults.Faults.Single().Details, Does.Contain("CAA not responding"));
            Assert.That(Current.FailedReads, Is.GreaterThanOrEqualTo(DeviceHealthTracker.DefaultThreshold));
            Assert.That(svc.IsCleanlyConnected, Is.False, "every CameraState read failed: not recovered");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "read health, not a trip");

            Current.ReadFails = false;
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean read");
        }

        [Test]
        public async Task A_connected_camera_with_no_clean_read_yet_is_not_cleanly_connected_until_one_lands() {
            using var hold = new ManualResetEventSlim(false);
            _configure = d => d.HoldReads = hold;
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            Assert.That(svc.IsCleanlyConnected, Is.False, "Connected, but the first read has not landed");

            hold.Set();
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean read");
        }

        [Test]
        public async Task The_clean_read_gate_starts_over_on_every_connect() {
            var built = 0;
            _configure = d => { if (Interlocked.Increment(ref built) > 1) { d.ReadFails = true; } };
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await WaitForAsync(() => svc.IsCleanlyConnected, "a clean first session");

            await ConnectAsync(svc, OtherCam); // a new session whose reads fail
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Connected && Current.FailedReads >= 1, "the second session's failed reads");
            Assert.That(svc.IsCleanlyConnected, Is.False, "the first session's clean read does not carry over");
        }

        // Item 4: a driver with no CameraState must not hold the gate shut forever.
        [Test]
        public async Task A_camera_whose_driver_lacks_CameraState_is_cleanly_connected_after_one_tick() {
            _configure = d => d.ReadThrows = new ASCOM.NotImplementedException("CameraState");
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await WaitForAsync(() => svc.IsCleanlyConnected, "a clean read from a driver without the property");
            Assert.That(_faults.Faults, Is.Empty, "not a fault: the driver has no CameraState");
        }

        [Test]
        public async Task A_camera_whose_CameraState_read_throws_any_other_error_is_not_cleanly_connected() {
            _configure = d => d.ReadThrows = new InvalidOperationException("CAA not responding");
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await Task.Delay(200);
            Assert.That(svc.IsCleanlyConnected, Is.False);
        }

        // #1539 — the ladder's reconnector and fault reaction leave a camera the user disconnected alone.
        [Test]
        public async Task Fault_reaction_and_the_reconnector_leave_a_user_disconnected_camera_alone() {
            using var svc = Build();
            await ConnectAsync(svc, Cam);
            await ConnectedAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);

            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(ICameraService))).Returns(svc);
            var store = new Mock<IEquipmentSelectionStore>();
            store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> { Cam });
            var reconnector = new EquipmentReconnector(services.Object, store.Object, NullLogger<EquipmentReconnector>.Instance);
            Assert.That(reconnector.GetDesiredConnection(DeviceType.Camera), Is.EqualTo(DesiredConnection.DisconnectedByUser));

            var actions = new List<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<WsEvent, JsonElement, CancellationToken>((t, p, _) => {
                    if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (actions) { actions.Add(p.GetProperty("action").GetString()!); } }
                })
                .Returns(Task.CompletedTask);
            using var reaction = new FaultReactionService(reconnector: reconnector, ws: ws.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(300),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
                LingerMaxAttempts = 0,
            };
            reaction.OnFault(new EquipmentFaultEvent(DeviceType.Camera, Cam.UniqueId, Cam.Name,
                EquipmentFaultKind.Disconnected, "3 probes failed", DateTimeOffset.UtcNow));
            await reaction.WhenIdleAsync();
            await reconnector.RecoverAsync(DeviceType.Camera, CancellationToken.None);
            await Task.Delay(100); // a dispatched reconnect would land within a few refresh ticks

            List<string> seen;
            lock (actions) { seen = actions.ToList(); }
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Disconnected), $"(actions: {string.Join(",", seen)})");
            Assert.That(seen, Does.Not.Contain("recovered"));
            Assert.That(Built, Is.EqualTo(1), "no reconnect reached the hardware");
        }
    }
}