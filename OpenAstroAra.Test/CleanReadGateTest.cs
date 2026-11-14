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
    /// alone). The Guider (PHD2, not Alpaca) is off the module and keeps "Connected is recovered". No test here dials a port.</summary>
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
            public int Built { get { lock (Fakes) { return Fakes.Count; } } }
            public void Dispose() => _service.Dispose();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Wire hands each service to the Rig, which disposes it.")]
        private static Rig Make(string device, Action<FakeDeviceAdapter>? configure = null) {
            var fakes = new List<FakeDeviceAdapter>();
            T Track<T>(T fake) where T : FakeDeviceAdapter {
                configure?.Invoke(fake);
                lock (fakes) { fakes.Add(fake); }
                return fake;
            }
            var ct = CancellationToken.None;
            var type = Enum.Parse<DeviceType>(device);
            var request = new ConnectRequestDto(Record(type));
            var services = new Mock<IServiceProvider>();
            Rig Wire<TService>(TService s, Type contract, Func<TService, EquipmentConnectionState?> state,
                    Func<TService, Task> connect) where TService : IDisposable {
                services.Setup(p => p.GetService(contract)).Returns(s);
                var store = new Mock<IEquipmentSelectionStore>();
                store.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> { Record(type) });
                var reconnector = new EquipmentReconnector(services.Object, store.Object, NullLogger<EquipmentReconnector>.Instance);
                return new Rig(s) { Type = type, State = () => state(s), Connect = () => connect(s), Fakes = fakes, Reconnector = reconnector };
            }
            return device switch {
                "Rotator" => Wire(new RotatorService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeRotatorDevice()) },
                    typeof(IRotatorService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "Dome" => Wire(new DomeService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeDomeDevice()) },
                    typeof(IDomeService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "Camera" => Wire(new CameraService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeCameraDevice()) },
                    typeof(ICameraService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "Telescope" => Wire(new TelescopeService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeTelescopeDevice()) },
                    typeof(ITelescopeService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "FilterWheel" => Wire(new FilterWheelService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFilterWheelDevice()) },
                    typeof(IFilterWheelService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "Focuser" => Wire(new FocuserService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFocuserDevice()) },
                    typeof(IFocuserService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "FlatDevice" => Wire(new FlatDeviceService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFlatDevice()) },
                    typeof(IFlatDeviceService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "ObservingConditions" => Wire(new ObservingConditionsService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeObservingConditionsDevice()) },
                    typeof(IObservingConditionsService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "SafetyMonitor" => Wire(new SafetyMonitorService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeSafetyMonitorDevice()) },
                    typeof(ISafetyMonitorService), s => s.GetAsync(ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                "Switch" => Wire(new SwitchService { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeSwitchDevice()) },
                    typeof(ISwitchService), s => s.GetAsync(request.Device.UniqueId, ct).Result?.State, s => s.ConnectAsync(request, null, ct)),
                _ => throw new ArgumentOutOfRangeException(nameof(device), device, null),
            };
        }

        public static readonly string[] OnTheModule =
            ["Rotator", "Dome", "Telescope", "Camera", "FilterWheel", "Focuser", "FlatDevice", "ObservingConditions", "SafetyMonitor", "Switch"];

        // SafetyMonitor is not here: a failed read trips it to Error at once (ADR 0011 rule 3), so it is never
        // Connected-and-unreadable; the held-read case below covers its gate.
        public static readonly string[] ReadableWhileFailing = [.. OnTheModule.Where(d => d != "SafetyMonitor")];

        // Exactly this many failed reads, then the next one holds its tick: below the Switch's unreadable-ports trip
        // (grace 2 + streak 10), so the device stays Connected-and-unread however slow the host is.
        private const int FailedTicks = 3;

        private static void Unreadable(FakeDeviceAdapter f) {
            f.ReadPermits = new SemaphoreSlim(FailedTicks);
            f.ReadFails = true;
        }

        private static void MakeReadable(FakeDeviceAdapter f) {
            f.ReadFails = false;
            f.ReadPermits!.Release(100); // the held read fails; the next tick reads cleanly
        }

        [TestCaseSource(nameof(ReadableWhileFailing))]
        public async Task A_connected_device_is_not_cleanly_connected_until_a_tick_reads_it_cleanly(string device) {
            using var rig = Make(device, Unreadable);
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected && rig.Current.FailedReads >= FailedTicks, "failed reads");
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False, "Connected, but no clean read since the connect");

            MakeReadable(rig.Current);
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "the first clean read");
        }

        [TestCaseSource(nameof(OnTheModule))]
        public async Task A_connected_device_is_not_cleanly_connected_while_its_first_read_is_in_flight(string device) {
            using var hold = new ManualResetEventSlim(false);
            using var rig = Make(device, f => f.HoldReads = hold);
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False, "Connected, but the first read has not landed");

            hold.Set();
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "the first clean read");
        }

        [TestCaseSource(nameof(ReadableWhileFailing))]
        public async Task The_clean_read_gate_starts_over_on_every_connect(string device) {
            var built = 0;
            using var rig = Make(device, f => {
                if (Interlocked.Increment(ref built) > 1) {
                    Unreadable(f); // the second session starts unreadable
                }
            });
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected && rig.Reconnector.IsCleanlyConnected(rig.Type), "a clean first session");

            rig.Current.Answers = false; // lost: the §42.3 trip
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Error, "the trip");
            var outcome = await rig.Reconnector.RecoverAsync(rig.Type, CancellationToken.None);
            Assert.That(outcome.Dispatched, Is.EqualTo(1));
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected && rig.Current.FailedReads >= FailedTicks, "the reconnect's failed reads");
            Assert.That(rig.Reconnector.IsCleanlyConnected(rig.Type), Is.False, "the first session's clean read does not carry over");

            MakeReadable(rig.Current);
            await WaitForAsync(() => rig.Reconnector.IsCleanlyConnected(rig.Type), "the new session's first clean read");
        }

        [Test]
        public void A_type_whose_service_is_off_the_module_keeps_Connected_as_recovered() {
            var services = new Mock<IServiceProvider>();
            services.Setup(p => p.GetService(typeof(IGuiderService))).Returns(new Mock<IGuiderService>().Object);
            var reconnector = new EquipmentReconnector(services.Object, new Mock<IEquipmentSelectionStore>().Object,
                NullLogger<EquipmentReconnector>.Instance);

            Assert.That(reconnector.IsCleanlyConnected(DeviceType.Guider), Is.True, "the guider (PHD2) has no lifecycle module, so no gate");
            Assert.That(reconnector.IsCleanlyConnected(DeviceType.Camera), Is.True, "no service at all");
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
            var (actions, _) = await RunLadderAsync(device, type, replacementReadsFail: true);
            Assert.That(actions, Does.Not.Contain("recovered"), $"Connected but unreadable is not recovered (actions: {string.Join(",", actions)})");
        }

        // Rung 2 on finds the device Connected from rung 1 but unread: each later rung must send the
        // disconnect/connect pair again, not take the idempotent no-op and wait out the confirm window.
        [TestCase("Rotator", DeviceType.Rotator)]
        [TestCase("Dome", DeviceType.Dome)]
        public async Task Each_later_rung_reconnects_a_device_that_is_connected_but_unread(string device, DeviceType type) {
            var (_, built) = await RunLadderAsync(device, type, replacementReadsFail: true);
            Assert.That(built, Is.GreaterThan(2), "the first session, rung 1's, and at least one more rung's");
        }

        [Test]
        public async Task A_user_connect_to_a_connected_but_unread_device_stays_the_no_op() {
            using var rig = Make("Rotator", f => f.ReadFails = true);
            await rig.Connect();
            await WaitForAsync(() => rig.State() == EquipmentConnectionState.Connected, "the connect");
            await Task.Delay(100);
            await rig.Connect();
            await Task.Delay(100);
            Assert.That(rig.Built, Is.EqualTo(1), "only the ladder reconnects a device that has not read cleanly");
        }

        [TestCase("Rotator", DeviceType.Rotator)]
        [TestCase("Dome", DeviceType.Dome)]
        public async Task The_ladder_calls_a_device_recovered_once_it_reads_cleanly(string device, DeviceType type) {
            var (actions, _) = await RunLadderAsync(device, type, replacementReadsFail: false);
            Assert.That(actions, Does.Contain("recovered"), $"(actions: {string.Join(",", actions)})");
        }

        private static async Task<(List<string> Actions, int Built)> RunLadderAsync(string device, DeviceType type, bool replacementReadsFail) {
            var built = 0;
            using var rig = Make(device, f => {
                if (Interlocked.Increment(ref built) > 1) {
                    f.ReadFails = replacementReadsFail;
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
            lock (actions) { return (actions.ToList(), rig.Built); }
        }
    }
}