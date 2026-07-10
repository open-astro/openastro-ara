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
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §42.2/§42.3 slice 1 — the fault-detection spine: the pure consecutive-failure probe, the
    /// fault hub's log/WS/subscriber contract, and one end-to-end proof that a §32.4 device
    /// service (Focuser, the canonical integration — the other seven share the identical pattern)
    /// notices a dead Alpaca device, trips Connected → Error, and publishes the §42.2 fault.
    /// </summary>
    [TestFixture]
    public class EquipmentFaultDetectionTest {

        // ── DeviceConnectionProbe (pure) ──

        [Test]
        public void A_single_blip_never_trips() {
            var probe = new DeviceConnectionProbe();
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded));
            Assert.That(probe.Observe(true), Is.EqualTo(ProbeVerdict.Healthy));
            Assert.That(probe.FailureStreak, Is.Zero, "a success clears the streak");
        }

        [Test]
        public void The_threshold_consecutive_failure_declares_lost() {
            var probe = new DeviceConnectionProbe(threshold: 3);
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded));
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded));
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Lost));
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Lost), "stays lost while failures continue");
        }

        [Test]
        public void Flapping_never_accumulates() {
            var probe = new DeviceConnectionProbe(threshold: 3);
            for (int i = 0; i < 10; i++) {
                Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded));
                Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded));
                Assert.That(probe.Observe(true), Is.EqualTo(ProbeVerdict.Healthy));
            }
        }

        [Test]
        public void Reset_starts_a_fresh_episode_and_the_threshold_floors_at_one() {
            var probe = new DeviceConnectionProbe(threshold: 3);
            probe.Observe(false);
            probe.Observe(false);
            probe.Reset();
            Assert.That(probe.Observe(false), Is.EqualTo(ProbeVerdict.Degraded), "old streaks never bleed across a reconnect");

            var eager = new DeviceConnectionProbe(threshold: 0); // floored to 1
            Assert.That(eager.Observe(false), Is.EqualTo(ProbeVerdict.Lost));
        }

        // ── EquipmentFaultHub ──

        private static (EquipmentFaultHub Hub, List<(string Type, JsonElement Payload)> Events) Hub() {
            var events = new List<(string Type, JsonElement Payload)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => {
                    lock (events) { events.Add((t, p)); }
                })
                .Returns(Task.CompletedTask);
            return (new EquipmentFaultHub(ws.Object), events);
        }

        private static EquipmentFaultEvent Fault(EquipmentFaultKind kind = EquipmentFaultKind.Disconnected) =>
            new(DeviceType.Camera, "cam-1", "Test Cam", kind, "details here",
                new DateTimeOffset(2026, 7, 10, 3, 0, 0, TimeSpan.Zero));

        [Test]
        public void Publish_broadcasts_the_equipment_fault_event() {
            var (hub, events) = Hub();

            hub.Publish(Fault());

            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Type, Is.EqualTo(WsEventCatalog.EquipmentFault));
            Assert.That(events[0].Payload.GetProperty("device_type").GetString(), Is.EqualTo("camera"));
            Assert.That(events[0].Payload.GetProperty("kind").GetString(), Is.EqualTo("disconnected"));
            Assert.That(events[0].Payload.GetProperty("device_name").GetString(), Is.EqualTo("Test Cam"));
        }

        [Test]
        public void Subscribers_receive_the_fault_and_a_throwing_subscriber_cannot_suppress_others() {
            var (hub, _) = Hub();
            var received = new List<EquipmentFaultKind>();
            hub.Subscribe(_ => throw new InvalidOperationException("bad subscriber"));
            hub.Subscribe(f => received.Add(f.Kind));

            hub.Publish(Fault(EquipmentFaultKind.StallTimeout));

            Assert.That(received, Is.EqualTo(new[] { EquipmentFaultKind.StallTimeout }),
                "the second subscriber must still fire after the first throws");
        }

        [Test]
        public void Wire_tokens_are_the_snake_case_taxonomy() {
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.Disconnected), Is.EqualTo("disconnected"));
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.TrackingLost), Is.EqualTo("tracking_lost"));
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.StallTimeout), Is.EqualTo("stall_timeout"));
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.ValueMismatch), Is.EqualTo("value_mismatch"));
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.CoolingDrift), Is.EqualTo("cooling_drift"));
            Assert.That(EquipmentFaultHub.WireToken(EquipmentFaultKind.OpError), Is.EqualTo("op_error"));
        }

        // ── End-to-end: a real FocuserService against a dying Alpaca device ──

        /// <summary>A minimal loopback Alpaca device: answers every GET with a fixed-value Alpaca
        /// envelope and every PUT with success — just enough for AlpacaFocuser to connect and for
        /// the Connected probe to read true. (The per-field runtime reads that receive a bool where
        /// they expect an int throw client-side and fall back to their defaults — by design.)</summary>
        private sealed class StubDevice : IAsyncDisposable {
            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;

            private StubDevice(HttpListener listener, int port) {
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = listener;
                _loop = Task.Run(LoopAsync);
            }

            public Uri BaseUri { get; }

            public static StubDevice Start() {
                var (listener, port) = OpenAstroAra.TestHarness.Net.LoopbackListener.Bind();
                return new StubDevice(listener, port);
            }

            private async Task LoopAsync() {
                while (!_cts.IsCancellationRequested) {
                    HttpListenerContext ctx;
                    try {
                        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    } catch (HttpListenerException) {
                        return;
                    } catch (ObjectDisposedException) {
                        return;
                    }
                    var body = Encoding.UTF8.GetBytes(
                        """{"Value":true,"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":0,"ErrorMessage":""}""");
                    try {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                        ctx.Response.Close();
                    } catch (HttpListenerException) {
                        // client went away mid-write — irrelevant to the test
                    } catch (ObjectDisposedException) {
                        // listener torn down mid-write — irrelevant to the test
                    }
                }
            }

            public async ValueTask DisposeAsync() {
                await _cts.CancelAsync().ConfigureAwait(false);
                try { _listener.Stop(); } catch (ObjectDisposedException) { }
                _listener.Close();
                try {
                    await _loop.ConfigureAwait(false);
                } catch (HttpListenerException) {
                } catch (ObjectDisposedException) {
                }
                _cts.Dispose();
            }
        }

        [Test]
        [Category("bench")] // §42.2 virtual-observatory bench — loopback-only, runs in the default job too
        public async Task A_dead_device_trips_connected_to_error_and_publishes_the_fault() {
            await using var stub = StubDevice.Start();
            await using var proxy = AlpacaFaultProxy.Start(stub.BaseUri);
            var (hub, wsEvents) = Hub();
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });

            using var svc = new FocuserService(faults: hub);
            var device = new DiscoveredDeviceDto(
                UniqueId: "focuser-under-test", Name: "Bench Focuser", Type: DeviceType.Focuser,
                HostName: proxy.BaseUri.Host, IpAddress: proxy.BaseUri.Host, IpPort: proxy.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            await WaitForStateAsync(svc, EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15));

            // Kill the device: every subsequent probe sees an aborted connection. Three consecutive
            // 2 s refresh ticks (§42.3 streak) must trip the service to Error and publish the fault.
            proxy.InjectFault(new AlpacaFaultRule { Fault = AlpacaFault.Drop() });
            await WaitForStateAsync(svc, EquipmentConnectionState.Error, TimeSpan.FromSeconds(25));

            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1), "exactly one fault per episode — no re-fire on later ticks");
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.Disconnected));
                Assert.That(faults[0].DeviceType, Is.EqualTo(DeviceType.Focuser));
                Assert.That(faults[0].DeviceName, Is.EqualTo("Bench Focuser"));
            }
            lock (wsEvents) {
                Assert.That(wsEvents.FindAll(e => e.Type == WsEventCatalog.EquipmentFault), Has.Count.EqualTo(1));
            }
        }

        private static async Task WaitForStateAsync(FocuserService svc, EquipmentConnectionState want, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto?.State == want) {
                    return;
                }
                await Task.Delay(200);
            }
            var last = await svc.GetAsync(CancellationToken.None);
            Assert.Fail($"focuser never reached {want} within {timeout.TotalSeconds:0}s (last state: {last?.State.ToString() ?? "null"})");
        }
    }
}
