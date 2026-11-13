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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 — the Telescope's lifecycle on the shared module, against a fake mount: connect,
    /// disconnect, forget, supersede, the §42.3 probe, the #1193 latch and the #1246 clean-read gate, and the
    /// #1539 desired connection state. No test here dials a port.</summary>
    [TestFixture]
    public class TelescopeLifecycleTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private static readonly DiscoveredDeviceDto OtherMount = Mount with { UniqueId = "mount-2", Name = "Other Mount" };

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
            public List<EquipmentFaultEvent> Of(EquipmentFaultKind kind) => Faults.Where(f => f.Kind == kind).ToList();
        }

        private readonly List<FakeTelescopeDevice> _devices = new();
        private readonly List<string> _trace = new();
        private FaultSink _faults = null!;
        private Action<FakeTelescopeDevice>? _configure;

        [SetUp]
        public void SetUp() {
            _devices.Clear();
            _trace.Clear();
            _faults = new FaultSink();
            _configure = null;
        }

        private FakeTelescopeDevice Current { get { lock (_devices) { return _devices[^1]; } } }
        private int Built { get { lock (_devices) { return _devices.Count; } } }
        private List<string> Trace { get { lock (_trace) { return _trace.ToList(); } } }

        private TelescopeService Build(TimeSpan? latchedSlewHold = null) {
            var service = new TelescopeService(faults: _faults) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = d => {
                    var fake = new FakeTelescopeDevice { Trace = e => { lock (_trace) { _trace.Add($"{d.UniqueId}:{e}"); } } };
                    _configure?.Invoke(fake);
                    lock (_devices) { _devices.Add(fake); }
                    return fake;
                },
            };
            if (latchedSlewHold is { } hold) {
                service.LatchedSlewHold = hold;
            }
            return service;
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

        private static EquipmentConnectionState? StateOf(TelescopeService s) => s.GetAsync(CancellationToken.None).Result?.State;

        private static Task ConnectedAsync(TelescopeService s) =>
            WaitForAsync(() => StateOf(s) == EquipmentConnectionState.Connected, "the connect");

        private static Task<OperationAcceptedDto> ConnectAsync(TelescopeService s, DiscoveredDeviceDto d) =>
            s.ConnectAsync(new ConnectRequestDto(d), null, CancellationToken.None);

        [Test]
        public async Task Connect_disconnect_and_forget_run_on_the_fake_mount() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the capabilities read");
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean read");

            await Assert.ThatAsync(() => svc.ForgetAsync(CancellationToken.None), Throws.InvalidOperationException, "a connected mount cannot be removed");
            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Disconnected));
            await WaitForAsync(() => Current.Disposed, "the teardown");
            Assert.That(Current.Aborts, Is.EqualTo(1), "the mount is stopped before it is let go");
            Assert.That(Current.Disconnects, Is.EqualTo(1));

            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.True);
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task The_desired_connection_follows_the_users_connect_and_disconnect_and_a_fault() {
            using var svc = Build();
            Assert.That(svc.DesiredConnection, Is.Null, "nothing selected yet");
            await ConnectAsync(svc, Mount);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));
            await ConnectedAsync(svc);

            Current.Answers = false;
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the §42.3 trip");
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
            Assert.That(svc.TryRecover(Mount), Is.True, "a lost mount the user still wants is recovered");
            await ConnectedAsync(svc);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.ConnectedByUser));

            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            Assert.That(svc.TryRecover(Mount), Is.False, "recovery leaves a mount the user disconnected alone");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That(Built, Is.EqualTo(2), "no connect was dispatched for the refused recovery");
        }

        [Test]
        public async Task A_dead_probe_trips_the_mount_after_the_streak_and_not_before() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
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
        }

        [Test]
        public async Task A_second_connect_supersedes_the_first_and_lets_it_go_before_dialing() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            await ConnectAsync(svc, OtherMount);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.DeviceId == OtherMount.UniqueId
                && StateOf(svc) == EquipmentConnectionState.Connected, "the second mount");

            var trace = Trace;
            Assert.That(trace.IndexOf("mount-1:disconnect"), Is.GreaterThanOrEqualTo(0));
            Assert.That(trace.IndexOf("mount-1:disconnect"), Is.LessThan(trace.IndexOf("mount-2:connect")),
                $"the old Connected=false reaches the bridge before the new Connected=true ({string.Join(",", trace)})");
            Assert.That(Built, Is.EqualTo(2));
        }

        [Test]
        public async Task A_latched_bridge_trips_the_mount_once_per_episode() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            var first = Current;

            first.Latched = true;
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the latch trip");
            await WaitForAsync(() => _faults.Faults.Count >= 1, "the fault");
            await Task.Delay(200); // ten more ticks on a mount that is out of service
            var faults = _faults.Faults;
            Assert.That(faults, Has.Count.EqualTo(1), "one episode, one fault");
            Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.Disconnected));
            Assert.That(faults[0].Details, Does.Contain(FakeTelescopeDevice.LatchMessage));
            Assert.That(svc.DesiredConnection, Is.EqualTo(DesiredConnection.Faulted));
        }

        [Test]
        public async Task A_latched_mount_is_not_recovered_until_it_reads_cleanly_and_the_connect_clears_the_gate() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            await WaitForAsync(() => svc.IsCleanlyConnected, "a healthy mount's first tick is clean");

            Current.Latched = true;
            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the latch trip");
            _configure = d => d.Latched = true; // the bridge is still latched after the reconnect
            Assert.That(svc.TryRecover(Mount), Is.True);
            await ConnectedAsync(svc);
            await Task.Delay(200);
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "still the same episode: no second trip");
            Assert.That(svc.IsCleanlyConnected, Is.False, "Connected, but every read still fails: not recovered");
            Assert.That(_faults.Of(EquipmentFaultKind.Disconnected), Has.Count.EqualTo(1));

            // The ladder's later rung recovers the same device again: not the idempotent no-op.
            _configure = null;
            Assert.That(svc.TryRecover(Mount), Is.True);
            await WaitForAsync(() => Built == 3, "the real reconnect");
            Assert.That(Trace.IndexOf("mount-1:disconnect"), Is.LessThan(Trace.LastIndexOf("mount-1:connect")), "disconnect before connect");
            await ConnectedAsync(svc);
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean tick after the bridge recovers");
            Assert.That(_faults.Of(EquipmentFaultKind.Disconnected), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_connect_to_a_healthy_connected_mount_stays_the_no_op() {
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            await ConnectAsync(svc, Mount);
            await Task.Delay(100);
            Assert.That(Built, Is.EqualTo(1));
        }

        [Test]
        public async Task A_goto_in_flight_holds_a_latched_Slewing_read_until_the_hold_runs_out() {
            using var svc = Build(latchedSlewHold: TimeSpan.FromMilliseconds(400));
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            Current.IsSlewing = true;
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Runtime.State == "slewing", "a slew the watch has seen start");

            Current.Latched = true; // every read latches, the Slewing read too
            await WaitForAsync(() => _faults.Of(EquipmentFaultKind.Degraded).Count == 1, "read health on the latched RA/Dec reads");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "never during a slew");
            Assert.That(_faults.Of(EquipmentFaultKind.Disconnected), Is.Empty, "no trip while the goto is open");

            await WaitForAsync(() => StateOf(svc) == EquipmentConnectionState.Error, "the trip once the hold ran out");
        }

        // ADR 0011 rule 2 — RA/Dec is the mount's essential read: a failure there counts, latch or not.
        [Test]
        public async Task A_generic_RA_failure_degrades_the_mount_and_holds_the_clean_read_gate_shut() {
            _configure = d => d.ReadFails = true; // RightAscension throws a generic error, not the bridge's phrase
            using var svc = Build();
            await ConnectAsync(svc, Mount);
            await ConnectedAsync(svc);
            await WaitForAsync(() => _faults.Faults.Count == 1, "the degraded fault after the threshold");
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
            Assert.That(_faults.Faults.Single().Details, Does.Contain("CAA not responding"));
            Assert.That(Current.FailedReads, Is.GreaterThanOrEqualTo(DeviceHealthTracker.DefaultThreshold));
            Assert.That(svc.IsCleanlyConnected, Is.False, "every RA read failed: not recovered");
            Assert.That(StateOf(svc), Is.EqualTo(EquipmentConnectionState.Connected), "read health, not a trip");

            Current.ReadFails = false;
            await WaitForAsync(() => svc.IsCleanlyConnected, "the first clean read");
        }
    }
}
