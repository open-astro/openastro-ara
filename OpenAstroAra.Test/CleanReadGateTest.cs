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

    /// <summary>ADR 0011 rule 2 — the fault ladder asks a device's lifecycle whether a Connected device read
    /// cleanly since the connect, through one type-free seam, for every device on the module (not the mount
    /// alone). A device not on the module yet keeps "Connected is recovered". No test here dials a port.</summary>
    [TestFixture]
    public class CleanReadGateTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

        private static DiscoveredDeviceDto Record(DeviceType type) => new(
            UniqueId: $"{type}-1", Name: $"Bench {type}", Type: type,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        /// <summary>A device service over fakes, and the reconnector that resolves it.</summary>
        private sealed class Rig : IDisposable {
            private readonly IDisposable _service;
            public required DeviceType Type { get; init; }
            public required Func<EquipmentConnectionState?> State { get; init; }
            public required Func<Task> Connect { get; init; }
            public required List<FakeDeviceAdapter> Fakes { get; init; }
            public required EquipmentReconnector Reconnector { get; init; }
            public Rig(IDisposable service) { _service = service; }
            public FakeDeviceAdapter Current { get { lock (Fakes) { return Fakes[^1]; } } }
            public void Dispose() => _service.Dispose();
        }

        private static Rig Make(string device, Action<FakeDeviceAdapter>? configure = null) {
            var fakes = new List<FakeDeviceAdapter>();
            T Track<T>(T fake) where T : FakeDeviceAdapter {
                configure?.Invoke(fake);
                lock (fakes) { fakes.Add(fake); }
                return fake;
            }
            var services = new Mock<IServiceProvider>();
            var store = new Mock<IEquipmentSelectionStore>();
            IDisposable service;
            DeviceType type;
            Func<EquipmentConnectionState?> state;
            Func<Task> connect;
            switch (device) {
                case "Rotator": {
                        var s = new RotatorService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeRotatorDevice()) };
                        services.Setup(p => p.GetService(typeof(IRotatorService))).Returns(s);
                        (service, type) = (s, DeviceType.Rotator);
                        state = () => s.GetAsync(CancellationToken.None).Result?.State;
                        connect = () => s.ConnectAsync(new ConnectRequestDto(Record(type)), null, CancellationToken.None);
                        break;
                    }
                case "Dome": {
                        var s = new DomeService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeDomeDevice()) };
                        services.Setup(p => p.GetService(typeof(IDomeService))).Returns(s);
                        (service, type) = (s, DeviceType.Dome);
                        state = () => s.GetAsync(CancellationToken.None).Result?.State;
                        connect = () => s.ConnectAsync(new ConnectRequestDto(Record(type)), null, CancellationToken.None);
                        break;
                    }
                default: {
                        var s = new TelescopeService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeTelescopeDevice()) };
                        services.Setup(p => p.GetService(typeof(ITelescopeService))).Returns(s);
                        (service, type) = (s, DeviceType.Telescope);
                        state = () => s.GetAsync(CancellationToken.None).Result?.State;
                        connect = () => s.ConnectAsync(new ConnectRequestDto(Record(type)), null, CancellationToken.None);
                        break;
                    }
            }
            store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> { Record(type) });
            var reconnector = new EquipmentReconnector(services.Object, store.Object, NullLogger<EquipmentReconnector>.Instance);
            return new Rig(service) { Type = type, State = state, Connect = connect, Fakes = fakes, Reconnector = reconnector };
        }

        // The failure each device's gate looks at: the mount's gate is the #1193 latch, the others' their essential read.
        private static void Break(FakeDeviceAdapter f, bool broken) {
            if (f is FakeTelescopeDevice mount) {
                mount.Latched = broken;
            } else {
                f.ReadFails = broken;
            }
        }

        public static readonly string[] OnTheModule = ["Rotator", "Dome", "Telescope"];

        // The mount is not in this case: a latched read on an idle mount trips it to Error at once (#1193), so it
        // is never Connected-and-unreadable on its first session; the next test covers it on the reconnect.
        [TestCase("Rotator")]
        [TestCase("Dome")]
        public async Task A_connected_device_is_not_cleanly_connected_until_a_tick_reads_it_cleanly(string device) {
            using var rig = Make(device, f => Break(f, true));
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            await Task.Delay(200); // ten ticks that all fail
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False, "Connected, but no clean read since the connect");

            Break(rig.Current, false);
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "the first clean read");
        }

        [TestCaseSource(nameof(OnTheModule))]
        public async Task The_clean_read_gate_starts_over_on_every_connect(string device) {
            var built = 0;
            using var rig = Make(device, f => Break(f, Interlocked.Increment(ref built) > 1)); // the second session starts unreadable
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected && rig.Reconnector.IsCleanlyConnected(rig.Type), "a clean first session");

            // lost: the §42.3 trip. The mount's is the #1193 latch (it sets the episode flag the next session inherits).
            if (rig.Current is FakeTelescopeDevice mount) { mount.Latched = true; } else { rig.Current.Answers = false; }
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Error, "the trip");
            var outcome = await rig.Reconnector.RecoverAsync(rig.Type, CancellationToken.None);
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the reconnect");
            await Task.Delay(200);
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False, "the first session's clean read does not carry over");

            Break(rig.Current, false);
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "the new session's first clean read");
        }

        [Test]
        public void A_type_whose_service_is_off_the_module_keeps_Connected_as_recovered() {
            var services = new Mock<IServiceProvider>();
            services.Setup(p => p.GetService(typeof(ICameraService))).Returns(new Mock<ICameraService>().Object);
            var reconnector = new EquipmentReconnector(services.Object, new Mock<IEquipmentSelectionStore>().Object,
                NullLogger<EquipmentReconnector>.Instance);

            Assert.That(reconnector.IsCleanlyConnected(DeviceType.Camera), Is.True, "a service with no lifecycle module has no gate");
            Assert.That(reconnector.IsCleanlyConnected(DeviceType.Guider), Is.True, "no service at all");
        }

        // ── Item 4: a driver that lacks its essential read must not hold the gate shut forever ──

        [Test]
        public async Task A_rotator_whose_driver_lacks_Position_is_cleanly_connected_after_one_tick() {
            using var rig = Make("Rotator", f => f.ReadThrows = new ASCOM.NotImplementedException("Position"));
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "a clean read from a driver without the property");
        }

        [Test]
        public async Task A_dome_whose_driver_lacks_Slewing_is_cleanly_connected_after_one_tick() {
            using var rig = Make("Dome", f => f.ReadThrows = new ASCOM.NotImplementedException("Slewing"));
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "a clean read from a driver without the property");
        }

        [TestCase("Rotator")]
        [TestCase("Dome")]
        public async Task A_device_whose_essential_read_throws_any_other_error_is_not_cleanly_connected(string device) {
            using var rig = Make(device, f => f.ReadThrows = new InvalidOperationException("CAA not responding"));
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            await Task.Delay(200);
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False);
        }

        // ── The ladder itself: Rotator and Dome are recovered only after a clean read ──

        [TestCase("Rotator", DeviceType.Rotator)]
        [TestCase("Dome", DeviceType.Dome)]
        public async Task The_ladder_does_not_call_a_device_recovered_that_reconnects_but_reads_nothing(string device, DeviceType type) {
            var actions = await RunLadderAsync(device, type, replacementReadsFail: true);
            Assert.That(actions, Does.Not.Contain("recovered"), $"Connected but unreadable is not recovered (actions: {string.Join(",", actions)})");
        }

        [TestCase("Rotator", DeviceType.Rotator)]
        [TestCase("Dome", DeviceType.Dome)]
        public async Task The_ladder_calls_a_device_recovered_once_it_reads_cleanly(string device, DeviceType type) {
            var actions = await RunLadderAsync(device, type, replacementReadsFail: false);
            Assert.That(actions, Does.Contain("recovered"), $"(actions: {string.Join(",", actions)})");
        }

        private static async Task<List<string>> RunLadderAsync(string device, DeviceType type, bool replacementReadsFail) {
            var built = 0;
            using var rig = Make(device, f => {
                if (Interlocked.Increment(ref built) > 1) {
                    Break(f, replacementReadsFail);
                }
            });
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            rig.Current.Answers = false;
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Error, "the §42.3 trip");

            var actions = new List<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, JsonElement, CancellationToken>((t, p, _) => {
                    if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (actions) { actions.Add(p.GetProperty("action").GetString()!); } }
                })
                .Returns(Task.CompletedTask);
            using var reaction = new FaultReactionService(reconnector: rig.Reconnector, ws: ws.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(400),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
                LingerMaxAttempts = 0,
            };
            reaction.OnFault(new EquipmentFaultEvent(type, $"{type}-1", $"Bench {type}", EquipmentFaultKind.Disconnected,
                "3 probes failed", DateTimeOffset.UtcNow));
            await reaction.WhenIdleAsync();
            lock (actions) { return actions.ToList(); }
        }
    }
}
