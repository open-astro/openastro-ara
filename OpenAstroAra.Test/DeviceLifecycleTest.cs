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
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 — the device lifecycle module against a fake device: the #1539 desired
    /// connection state, recovery, the #1246 clean-read gate and the read-health threshold.</summary>
    [TestFixture]
    public class DeviceLifecycleTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "dev-1", Name: "Bench Device", Type: DeviceType.Rotator,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private readonly List<FakeDeviceAdapter> _devices = new();
        private FaultSink _faults = null!;
        private bool _readsFailOnConnect;

        [SetUp]
        public void SetUp() {
            _devices.Clear();
            _faults = new FaultSink();
            _readsFailOnConnect = false;
        }

        private FakeDeviceAdapter Current { get { lock (_devices) { return _devices[^1]; } } }

        private DeviceLifecycle<FakeDeviceAdapter> Build(int failedReadThreshold = DeviceHealthTracker.DefaultThreshold) {
            var lifecycle = new DeviceLifecycle<FakeDeviceAdapter>(this, DeviceType.Rotator, "rotator",
                _ => { var d = new FakeDeviceAdapter { ReadFails = _readsFailOnConnect }; lock (_devices) { _devices.Add(d); } return d; },
                d => new DeviceRead(d.ReadFails ? "CAA not responding" : null, CommitLocked: () => { }),
                resetSessionLocked: () => { }, failedReadThreshold, NullLogger.Instance, events: null, _faults);
            lifecycle.RefreshPeriod = TimeSpan.FromMilliseconds(20);
            return lifecycle;
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

        private static Task ConnectedAsync(DeviceLifecycle<FakeDeviceAdapter> l) =>
            WaitForAsync(() => l.State == EquipmentConnectionState.Connected, "the connect");

        [Test]
        public async Task The_desired_connection_follows_the_users_connect_disconnect_and_forget() {
            using var l = Build();
            Assert.That(l.Desired, Is.Null, "nothing selected yet");
            await l.ConnectAsync(Device, null);
            Assert.That(l.Desired, Is.EqualTo(DesiredConnection.ConnectedByUser));
            await ConnectedAsync(l);
            await l.DisconnectAsync(null);
            Assert.That(l.Desired, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            Assert.That(await l.ForgetAsync(), Is.True);
            Assert.That(l.Desired, Is.Null, "a removed device has no desired state");
        }

        [Test]
        public async Task A_trip_marks_the_device_faulted_and_recovery_reconnects_it() {
            using var l = Build();
            await l.ConnectAsync(Device, null);
            await ConnectedAsync(l);
            Current.Answers = false;
            await WaitForAsync(() => l.State == EquipmentConnectionState.Error, "the §42.3 trip");
            Assert.That(l.Desired, Is.EqualTo(DesiredConnection.Faulted));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.Disconnected));

            Assert.That(l.TryRecover(Device), Is.True);
            await ConnectedAsync(l);
            Assert.That(l.Desired, Is.EqualTo(DesiredConnection.ConnectedByUser), "a recovered device is wanted connected again");
            lock (_devices) { Assert.That(_devices, Has.Count.EqualTo(2), "recovery built a fresh device"); }
        }

        [Test]
        public async Task Recovery_refuses_a_device_the_user_disconnected() {
            using var l = Build();
            await l.ConnectAsync(Device, null);
            await ConnectedAsync(l);
            await l.DisconnectAsync(null);

            Assert.That(l.TryRecover(Device), Is.False);
            Assert.That(l.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
            lock (_devices) { Assert.That(_devices, Has.Count.EqualTo(1), "no connect was dispatched"); }
        }

        [Test]
        public async Task The_clean_read_gate_opens_on_the_first_clean_essential_read() {
            using var l = Build();
            _readsFailOnConnect = true;
            await l.ConnectAsync(Device, null);
            await ConnectedAsync(l);
            await Task.Delay(100); // several ticks
            Assert.That(l.HasCleanReadSinceConnect, Is.False, "Connected, but the essential read fails");
            Current.ReadFails = false;
            await WaitForAsync(() => l.HasCleanReadSinceConnect, "the first clean read");
        }

        [Test]
        public async Task A_failed_read_threshold_of_one_degrades_on_the_first_failed_read() {
            using var l = Build(failedReadThreshold: 1);
            await l.ConnectAsync(Device, null);
            await ConnectedAsync(l);
            Current.ReadFails = true;
            await WaitForAsync(() => _faults.Faults.Count > 0, "the degraded fault");
            var fault = _faults.Faults.Single();
            Assert.That(fault.Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
            Assert.That(fault.Details, Does.Contain("failed 1 ticks in a row"), "degraded on the first failed read, not the third");
        }

        [Test]
        public async Task A_dome_whose_Slewing_read_keeps_failing_is_degraded() {
            using var dome = new FakeDomeDevice(); // the service disposes it too; a second Dispose is harmless
            using var svc = new DomeService(faults: _faults) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => dome,
            };
            await svc.ConnectAsync(new ConnectRequestDto(Device with { Type = DeviceType.Dome }), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "the connect");
            dome.ReadFails = true;
            await WaitForAsync(() => _faults.Faults.Count > 0, "the degraded fault");
            var fault = _faults.Faults.Single();
            Assert.That((fault.DeviceType, fault.Kind), Is.EqualTo((DeviceType.Dome, EquipmentFaultKind.Degraded)));
            Assert.That(fault.Details, Does.Contain("CAA not responding"));
        }
    }
}
