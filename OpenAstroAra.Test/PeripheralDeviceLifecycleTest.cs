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

    /// <summary>ADR 0011 — the lifecycle of the six small devices (FlatDevice, ObservingConditions, Switch,
    /// SafetyMonitor, FilterWheel, Focuser) against a fake device: connect/disconnect/forget, supersede, the
    /// §42.3 probe, the failed-read threshold, the #1246 clean-read gate, the #1539 desired connection state,
    /// and recovery leaving a user-disconnected device alone. No test here dials a port.</summary>
    [TestFixture]
    public class PeripheralDeviceLifecycleTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

        public static readonly string[] AllDevices = ["FlatDevice", "ObservingConditions", "Switch", "SafetyMonitor", "FilterWheel", "Focuser"];
        public static readonly string[] DegradingDevices = ["FlatDevice", "ObservingConditions", "Switch", "FilterWheel", "Focuser"];

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        /// <summary>One device service over fake devices, seen through the members every one of them shares.</summary>
        private sealed class Subject : IDisposable {
            public required DeviceType Type { get; init; }
            public required Type ServiceInterface { get; init; }
            public required object Service { get; init; }
            public required Func<DiscoveredDeviceDto, Task> Connect { get; init; }
            public required Func<Task> Disconnect { get; init; }
            public required Func<Task<bool>> Forget { get; init; }
            public required Func<EquipmentConnectionState?> State { get; init; }
            public required Func<bool> CleanRead { get; init; }
            public required Func<DiscoveredDeviceDto> Selected { get; init; }
            public required Func<DiscoveredDeviceDto> Replacement { get; init; }
            public required IDisposable Disposable { get; init; }
            public required Func<List<FakeDeviceAdapter>> Fakes { get; init; }
            public FakeDeviceAdapter Current => Fakes()[^1];
            public int Built => Fakes().Count;
            public IRecoverableDevice Recoverable => (IRecoverableDevice)Service;
            public void Dispose() => Disposable.Dispose();
        }

        private static DiscoveredDeviceDto Record(DeviceType type, string id, int port = 11111) => new(
            UniqueId: id, Name: $"Bench {type}", Type: type,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: port, AlpacaDeviceNumber: 0, UseHttps: false);

        private static Subject Make(string device, IEquipmentFaultSink faults, Action<FakeDeviceAdapter>? configure = null) {
            var fakes = new List<FakeDeviceAdapter>();
            T Track<T>(T fake) where T : FakeDeviceAdapter {
                configure?.Invoke(fake);
                lock (fakes) { fakes.Add(fake); }
                return fake;
            }
            List<FakeDeviceAdapter> Snapshot() { lock (fakes) { return fakes.ToList(); } }
            static Func<Task> Ignore(Func<Task<OperationAcceptedDto>> call) => async () => { await call(); };
            var ct = CancellationToken.None;
            switch (device) {
                case "FlatDevice": {
                        var svc = new FlatDeviceService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFlatDevice()) };
                        return new Subject {
                            Type = DeviceType.FlatDevice, ServiceInterface = typeof(IFlatDeviceService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync(null, ct)), Forget = () => svc.ForgetAsync(ct),
                            State = () => svc.GetAsync(ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect,
                            Selected = () => Record(DeviceType.FlatDevice, "dev-1"), Replacement = () => Record(DeviceType.FlatDevice, "dev-2", 11112),
                        };
                    }
                case "ObservingConditions": {
                        var svc = new ObservingConditionsService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeObservingConditionsDevice()) };
                        return new Subject {
                            Type = DeviceType.ObservingConditions, ServiceInterface = typeof(IObservingConditionsService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync(null, ct)), Forget = () => svc.ForgetAsync(ct),
                            State = () => svc.GetAsync(ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect,
                            Selected = () => Record(DeviceType.ObservingConditions, "dev-1"), Replacement = () => Record(DeviceType.ObservingConditions, "dev-2", 11112),
                        };
                    }
                case "Switch": {
                        var svc = new SwitchService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeSwitchDevice()) };
                        return new Subject {
                            Type = DeviceType.Switch, ServiceInterface = typeof(ISwitchService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync("dev-1", null, ct)), Forget = () => svc.RemoveAsync("dev-1", ct),
                            State = () => svc.GetAsync("dev-1", ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect("dev-1"),
                            Selected = () => Record(DeviceType.Switch, "dev-1"),
                            // The same endpoint under a renamed UniqueId: one physical switch, so it replaces the first.
                            Replacement = () => Record(DeviceType.Switch, "dev-1-renamed"),
                        };
                    }
                case "SafetyMonitor": {
                        var svc = new SafetyMonitorService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeSafetyMonitorDevice()) };
                        return new Subject {
                            Type = DeviceType.SafetyMonitor, ServiceInterface = typeof(ISafetyMonitorService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync(null, ct)), Forget = () => svc.ForgetAsync(ct),
                            State = () => svc.GetAsync(ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect,
                            Selected = () => Record(DeviceType.SafetyMonitor, "dev-1"), Replacement = () => Record(DeviceType.SafetyMonitor, "dev-2", 11112),
                        };
                    }
                case "FilterWheel": {
                        var svc = new FilterWheelService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFilterWheelDevice()) };
                        return new Subject {
                            Type = DeviceType.FilterWheel, ServiceInterface = typeof(IFilterWheelService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync(null, ct)), Forget = () => svc.ForgetAsync(ct),
                            State = () => svc.GetAsync(ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect,
                            Selected = () => Record(DeviceType.FilterWheel, "dev-1"), Replacement = () => Record(DeviceType.FilterWheel, "dev-2", 11112),
                        };
                    }
                case "Focuser": {
                        var svc = new FocuserService(faults: faults) { RefreshPeriod = Tick, DeviceFactory = _ => Track(new FakeFocuserDevice()) };
                        return new Subject {
                            Type = DeviceType.Focuser, ServiceInterface = typeof(IFocuserService), Service = svc, Disposable = svc, Fakes = Snapshot,
                            Connect = d => svc.ConnectAsync(new ConnectRequestDto(d), null, ct),
                            Disconnect = Ignore(() => svc.DisconnectAsync(null, ct)), Forget = () => svc.ForgetAsync(ct),
                            State = () => svc.GetAsync(ct).Result?.State, CleanRead = () => svc.HasCleanReadSinceConnect,
                            Selected = () => Record(DeviceType.Focuser, "dev-1"), Replacement = () => Record(DeviceType.Focuser, "dev-2", 11112),
                        };
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(device), device, null);
            }
        }

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        private static Task ConnectedAsync(Subject s) =>
            WaitForAsync(() => s.State() == EquipmentConnectionState.Connected, "the connect");

        private static async Task<Subject> ConnectedAsync(string device, FaultSink faults, Action<FakeDeviceAdapter>? configure = null) {
            var s = Make(device, faults, configure);
            await s.Connect(s.Selected());
            await ConnectedAsync(s);
            return s;
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task Connect_disconnect_and_forget_follow_the_users_wishes(string device) {
            using var s = Make(device, new FaultSink());
            Assert.That(s.State(), Is.Null, "nothing selected yet");
            Assert.That(s.Recoverable.DesiredConnection, Is.Null);

            await s.Connect(s.Selected());
            Assert.That(s.Recoverable.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));
            await ConnectedAsync(s);
            Assert.That(s.Built, Is.EqualTo(1));

            await s.Disconnect();
            Assert.That(s.State(), Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That(s.Recoverable.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            await WaitForAsync(() => s.Current.Disposed, "the device to be released");

            Assert.That(await s.Forget(), Is.True);
            Assert.That(s.State(), Is.Null, "a removed device has no state");
            Assert.That(s.Recoverable.DesiredConnection, Is.Null, "and no desired state");
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task Forgetting_a_connected_device_is_refused(string device) {
            using var s = await ConnectedAsync(device, new FaultSink());

            Assert.That(async () => await s.Forget(), Throws.InvalidOperationException);
            Assert.That(s.State(), Is.EqualTo(EquipmentConnectionState.Connected));
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task A_second_connect_supersedes_the_first_device(string device) {
            using var s = await ConnectedAsync(device, new FaultSink());
            var first = s.Current;

            await s.Connect(s.Replacement());
            await WaitForAsync(() => s.Built == 2 && first.Disposed, "the first device to be released");
            await WaitForAsync(() => s.State() == EquipmentConnectionState.Connected || device == "Switch", "the replacement");

            Assert.That(first.Disposed, Is.True);
            Assert.That(s.Current, Is.Not.SameAs(first));
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task A_device_that_stops_answering_is_lost_and_marked_faulted(string device) {
            var faults = new FaultSink();
            using var s = await ConnectedAsync(device, faults);

            s.Current.Answers = false;
            await WaitForAsync(() => s.State() == EquipmentConnectionState.Error, "the §42.3 trip");

            Assert.That(s.Recoverable.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
            var fault = faults.Faults.Single();
            Assert.That((fault.DeviceType, fault.Kind), Is.EqualTo((s.Type, EquipmentFaultKind.Disconnected)));

            Assert.That(s.Recoverable.TryRecover(s.Selected()), Is.True, "a lost device may be reconnected");
            await ConnectedAsync(s);
            Assert.That(s.Recoverable.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));
        }

        [TestCaseSource(nameof(DegradingDevices))]
        public async Task The_essential_read_degrades_the_device_at_its_threshold(string device) {
            var faults = new FaultSink();
            using var permits = new SemaphoreSlim(0);
            using var s = await ConnectedAsync(device, faults, f => f.ReadPermits = permits);
            var fake = s.Current;

            fake.ReadFails = true;
            permits.Release(DeviceHealthTracker.DefaultThreshold - 1);
            await WaitForAsync(() => fake.FailedReads >= DeviceHealthTracker.DefaultThreshold - 1, "the failed reads below the threshold");
            await Task.Delay(100); // the commit of the last of them
            Assert.That(faults.Faults, Is.Empty, $"{DeviceHealthTracker.DefaultThreshold - 1} failed reads are not yet degraded");

            permits.Release();
            await WaitForAsync(() => faults.Faults.Count > 0, "the degraded fault");
            var fault = faults.Faults.Single();
            Assert.That((fault.DeviceType, fault.Kind), Is.EqualTo((s.Type, EquipmentFaultKind.Degraded)));
            Assert.That(s.State(), Is.EqualTo(EquipmentConnectionState.Connected), "degraded, not lost");

            fake.ReadFails = false;
            permits.Release(100);
            await WaitForAsync(() => s.CleanRead(), "the first clean read");
        }

        [Test]
        public async Task A_safety_monitor_whose_first_read_fails_is_in_error_and_not_safe() {
            var faults = new FaultSink();
            using var permits = new SemaphoreSlim(0);
            FakeSafetyMonitorDevice? dead = null;
            using var svc = new SafetyMonitorService(faults: faults) {
                RefreshPeriod = Tick,
                DeviceFactory = _ => dead = new FakeSafetyMonitorDevice { ReadPermits = permits },
            };
            await svc.ConnectAsync(new ConnectRequestDto(Record(DeviceType.SafetyMonitor, "dev-1")), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result is { State: EquipmentConnectionState.Connected, Safe: true }, "a safe verdict");
            Assert.That(svc.GetInfo().IsSafe, Is.True);

            dead!.ReadFails = true;
            permits.Release(1); // exactly ONE failed read
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Error, "the demotion");

            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.Safe, Is.False, "no safe verdict stands after a failed read");
            Assert.That(svc.GetInfo().IsSafe, Is.False);
            Assert.That(svc.GetInfo().Connected, Is.False);
            Assert.That(dead.FailedReads, Is.EqualTo(1));
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
            Assert.That(faults.Faults.Select(f => f.Kind), Does.Contain(EquipmentFaultKind.Disconnected));
        }

        [Test]
        public async Task A_safety_monitor_that_stops_answering_reads_not_safe_after_one_failed_probe() {
            var faults = new FaultSink();
            using var probes = new SemaphoreSlim(0);
            FakeSafetyMonitorDevice? dead = null;
            using var svc = new SafetyMonitorService(faults: faults) {
                RefreshPeriod = Tick,
                DeviceFactory = _ => dead = new FakeSafetyMonitorDevice { ProbePermits = probes },
            };
            await svc.ConnectAsync(new ConnectRequestDto(Record(DeviceType.SafetyMonitor, "dev-1")), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result is { State: EquipmentConnectionState.Connected, Safe: true }, "a safe verdict");

            dead!.Answers = false;
            probes.Release(1); // exactly ONE failed probe; the IsSafe read itself never fails
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Safe == false, "a not-safe verdict");

            Assert.That(svc.GetInfo().IsSafe, Is.False);
            Assert.That(dead.FailedProbes, Is.EqualTo(1));
            Assert.That(dead.FailedReads, Is.Zero);
            Assert.That((await svc.GetAsync(CancellationToken.None))!.State, Is.EqualTo(EquipmentConnectionState.Error));
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
            Assert.That(faults.Faults.Select(f => f.Kind), Does.Contain(EquipmentFaultKind.Disconnected));
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task The_clean_read_gate_is_closed_by_each_connect(string device) {
            // Every device's essential read is held until the test lets it through: Connected, but no read yet.
            var holds = new List<ManualResetEventSlim>();
            try {
                using var s = await ConnectedAsync(device, new FaultSink(), f => {
                    var hold = new ManualResetEventSlim(false);
                    lock (holds) { holds.Add(hold); }
                    f.HoldReads = hold;
                });
                await Task.Delay(60); // several ticks
                Assert.That(s.CleanRead(), Is.False, "Connected, but no essential read has completed");

                holds[0].Set();
                await WaitForAsync(() => s.CleanRead(), "the first clean read");

                await s.Disconnect();
                await s.Connect(s.Selected());
                await ConnectedAsync(s);
                await Task.Delay(60);
                Assert.That(s.CleanRead(), Is.False, "a new session starts with the gate closed");
                lock (holds) { Assert.That(holds, Has.Count.EqualTo(2)); }
            } finally {
                lock (holds) { foreach (var h in holds) { h.Set(); } }
            }
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task Recovery_leaves_a_user_disconnected_device_alone_and_reconnects_a_faulted_one(string device) {
            var faults = new FaultSink();
            using var s = await ConnectedAsync(device, faults);
            var record = s.Selected();
            var reconnector = ReconnectorFor(s, record);

            await s.Disconnect();
            var refused = await reconnector.RecoverAsync(s.Type, CancellationToken.None);
            await Task.Delay(100);
            Assert.That(refused, Is.EqualTo(new ReconnectOutcome(Attempted: 1, Dispatched: 0)), "the user disconnected it");
            Assert.That(s.State(), Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That(s.Built, Is.EqualTo(1), "no connect reached the hardware");
            Assert.That(reconnector.GetDesiredConnection(s.Type), Is.EqualTo(DesiredConnection.DisconnectedByUser));

            await s.Connect(record);
            await ConnectedAsync(s);
            s.Current.Answers = false;
            await WaitForAsync(() => s.State() == EquipmentConnectionState.Error, "the trip");
            s.Current.Answers = true;
            var recovered = await reconnector.RecoverAsync(s.Type, CancellationToken.None);
            Assert.That(recovered, Is.EqualTo(new ReconnectOutcome(Attempted: 1, Dispatched: 1)));
            await ConnectedAsync(s);
            Assert.That(s.Built, Is.EqualTo(3));
        }

        [TestCaseSource(nameof(AllDevices))]
        public async Task The_fault_reaction_releases_a_device_the_user_disconnected(string device) {
            var faults = new FaultSink();
            using var s = await ConnectedAsync(device, faults);
            var record = s.Selected();
            await s.Disconnect();
            var built = s.Built;

            var actions = new List<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, JsonElement, CancellationToken>((t, p, _) => {
                    if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (actions) { actions.Add(p.GetProperty("action").GetString()!); } }
                })
                .Returns(Task.CompletedTask);
            using var reaction = new FaultReactionService(reconnector: ReconnectorFor(s, record), ws: ws.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(300),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
                LingerMaxAttempts = 0,
            };
            reaction.OnFault(new EquipmentFaultEvent(s.Type, record.UniqueId, record.Name,
                EquipmentFaultKind.Disconnected, "3 probes failed", DateTimeOffset.UtcNow));
            await reaction.WhenIdleAsync();

            List<string> seen;
            lock (actions) { seen = actions.ToList(); }
            Assert.That(seen, Does.Contain("released").And.Not.Contain("recovered"));
            Assert.That(s.State(), Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That(s.Built, Is.EqualTo(built), "no reconnect reached the hardware");
        }

        private static EquipmentReconnector ReconnectorFor(Subject s, DiscoveredDeviceDto record) {
            var services = new Mock<IServiceProvider>();
            services.Setup(p => p.GetService(s.ServiceInterface)).Returns(s.Service);
            var store = new Mock<IEquipmentSelectionStore>();
            store.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<DiscoveredDeviceDto> { record });
            return new EquipmentReconnector(services.Object, store.Object, NullLogger<EquipmentReconnector>.Instance);
        }

        // ---- Switch: one lifecycle per connection, with the ports-unreadable detector on top ----

        [Test]
        public async Task A_switch_that_advertises_ports_but_reads_none_is_lost_after_the_grace_window() {
            var faults = new FaultSink();
            using var s = await ConnectedAsync("Switch", faults);
            s.Current.ReadFails = true; // MaxSwitch throws: the device would not report a port count

            await WaitForAsync(() => s.State() == EquipmentConnectionState.Error, "the ports-unreadable trip");

            var kinds = faults.Faults.Select(f => f.Kind).ToList();
            Assert.That(kinds, Does.Contain(EquipmentFaultKind.Degraded), "the essential read degraded first");
            var lost = faults.Faults.Single(f => f.Kind == EquipmentFaultKind.Disconnected);
            Assert.That(lost.Details, Does.Contain("no readable ports on 10 consecutive reads"));
            Assert.That(s.Recoverable.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
        }

        [Test]
        public async Task Two_switches_keep_their_own_lifecycle_and_desired_state() {
            var faults = new FaultSink();
            var fakes = new List<FakeSwitchDevice>();
            using var svc = new SwitchService(faults: faults) {
                RefreshPeriod = Tick,
                DeviceFactory = _ => { var d = new FakeSwitchDevice(); lock (fakes) { fakes.Add(d); } return d; },
            };
            var a = Record(DeviceType.Switch, "sw-a", 11111);
            var b = Record(DeviceType.Switch, "sw-b", 11112);
            await svc.ConnectAsync(new ConnectRequestDto(a), null, CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(b), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAllAsync(CancellationToken.None).Result.All(x => x.State == EquipmentConnectionState.Connected)
                && svc.GetAllAsync(CancellationToken.None).Result.Count == 2, "both switches");

            await svc.DisconnectAsync("sw-a", null, CancellationToken.None);
            Assert.That((await svc.GetAsync("sw-a", CancellationToken.None))!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That((await svc.GetAsync("sw-b", CancellationToken.None))!.State, Is.EqualTo(EquipmentConnectionState.Connected), "the other is untouched");
            Assert.That(svc.TryRecover(a), Is.False, "sw-a was disconnected by the user");
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser), "sw-b is still wanted connected");

            await svc.DisconnectAsync("sw-b", null, CancellationToken.None);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser), "every known switch was disconnected by the user");
            Assert.That(svc.TryRecover(b), Is.False);
            Assert.That(await svc.RemoveAsync("sw-a", CancellationToken.None), Is.True);
            Assert.That(await svc.GetAsync("sw-a", CancellationToken.None), Is.Null);
        }
    }
}