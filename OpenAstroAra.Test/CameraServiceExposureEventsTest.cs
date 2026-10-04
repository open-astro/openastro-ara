#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>The <c>camera.exposure_*</c> events from the shared capture core, against a real
    /// <see cref="CameraService"/> and a scripted loopback camera: every announced exposure ends in
    /// exactly one complete or failed, and a REST abort ends the wait at once as
    /// <c>reason: "aborted"</c> with no stall fault.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP
    [Category("bench")]
    public class CameraServiceExposureEventsTest {

        private sealed class RecordingBroadcaster : IWsBroadcaster {
            private readonly List<(string Type, JsonElement Payload)> _events = new();
            public long CurrentSequence => Snapshot().Count;

            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                lock (_events) {
                    _events.Add((eventType, payload.Clone()));
                }
                return Task.CompletedTask;
            }

            public List<(string Type, JsonElement Payload)> Snapshot() {
                lock (_events) {
                    return _events.ToList();
                }
            }

            public List<(string Type, JsonElement Payload)> Exposure() =>
                Snapshot().Where(e => e.Type.StartsWith("camera.exposure_", StringComparison.Ordinal)).ToList();
        }

        /// <summary>A scripted camera whose ImageReady is whatever the test says; ImageArray is a
        /// 2×2 frame (the extra Type/Rank fields ride in the scripted Value slot).</summary>
        private static ScriptedAlpacaDevice Camera(Func<bool> imageReady) => ScriptedAlpacaDevice.Start(path =>
            path.EndsWith("/imageready", StringComparison.Ordinal) ? (imageReady() ? "true" : "false")
            : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
            : null);

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static (EquipmentFaultHub Hub, List<EquipmentFaultEvent> Faults) Hub() {
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            return (hub, faults);
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (await condition()) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
        }

        private static async Task<CameraService> ConnectedAsync(ScriptedAlpacaDevice box, RecordingBroadcaster ws, IEquipmentFaultSink? faults = null) {
            var svc = new CameraService(events: new EquipmentEventPublisher(ws), faults: faults);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None)) is { State: EquipmentConnectionState.Connected, Capabilities: not null },
                "camera never connected with its capabilities read");
            return svc;
        }

        [Test]
        public async Task A_capture_announces_its_start_and_ends_in_exactly_one_complete() {
            await using var box = Camera(() => true);
            var ws = new RecordingBroadcaster();
            using var svc = await ConnectedAsync(box, ws);

            await svc.CaptureForAnalysisAsync(0.01, 1, CancellationToken.None);

            var events = ws.Exposure();
            Assert.That(events.Select(e => e.Type),
                Is.EqualTo(new[] { WsEventCatalog.CameraExposureStarted, WsEventCatalog.CameraExposureComplete }));
            Assert.That(events[1].Payload.GetProperty("frame_id").GetString(),
                Is.EqualTo(events[0].Payload.GetProperty("frame_id").GetString()), "complete pairs with its own start");
            Assert.That(events[0].Payload.GetProperty("kind").GetString(), Is.EqualTo("analysis"));
        }

        [Test]
        public async Task A_rest_abort_ends_the_wait_at_once_as_aborted_with_no_stall_fault() {
            // ImageReady never comes: without the abort the wait runs exposure + 60 s and ends as a
            // StallTimeout fault.
            await using var box = Camera(() => false);
            var ws = new RecordingBroadcaster();
            var (hub, faults) = Hub();
            using var svc = await ConnectedAsync(box, ws, hub);

            var capture = svc.CaptureForAnalysisAsync(30, 1, CancellationToken.None);
            await WaitForAsync(() => Task.FromResult(ws.Exposure().Count > 0), "the exposure was never announced");
            await svc.AbortExposureAsync(CancellationToken.None);

            var finished = await Task.WhenAny(capture, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.That(finished, Is.SameAs(capture), "the abort ends the capture now, not after exposure + margin");
            try {
                await capture;
            } catch (InvalidOperationException) {
                // an aborted analysis capture has no frame to return — the shape is the caller's concern
            }

            var events = ws.Exposure();
            Assert.That(events.Select(e => e.Type),
                Is.EqualTo(new[] { WsEventCatalog.CameraExposureStarted, WsEventCatalog.CameraExposureFailed }));
            Assert.That(events[1].Payload.GetProperty("reason").GetString(), Is.EqualTo("aborted"));
            lock (faults) {
                Assert.That(faults.Where(f => f.Kind == EquipmentFaultKind.StallTimeout), Is.Empty,
                    "a user's abort is not a device fault");
            }
        }

        [Test]
        public async Task An_abort_with_no_capture_running_does_not_kill_the_next_capture() {
            await using var box = Camera(() => true);
            var ws = new RecordingBroadcaster();
            using var svc = await ConnectedAsync(box, ws);

            await svc.AbortExposureAsync(CancellationToken.None);
            await svc.CaptureForAnalysisAsync(0.01, 1, CancellationToken.None);

            Assert.That(ws.Exposure().Select(e => e.Type),
                Is.EqualTo(new[] { WsEventCatalog.CameraExposureStarted, WsEventCatalog.CameraExposureComplete }));
        }
    }
}
