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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 (#1808) - the Sequencer's switch writes go through the command module, against the
    /// fake adapter with millisecond timeouts.</summary>
    [TestFixture]
    public class SwitchCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "switch-1", Name: "Bench Switch", Type: DeviceType.Switch,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private FaultSink _faults = null!;

        [SetUp]
        public void SetUp() => _faults = new FaultSink();

        // Built from the production declaration; only the timings shrink to milliseconds.
        private static DeviceCommandPolicy Policy(double hardTimeoutMs = 100) =>
            SwitchService.WritePolicy with { HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs) };

        [Test]
        public void The_switch_policy_keeps_todays_timeout_and_has_no_terminal_condition() {
            Assert.That(SwitchService.WritePolicy.Device, Is.EqualTo(DeviceType.Switch));
            Assert.That(SwitchService.WritePolicy.HardTimeout, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(SwitchService.WritePolicy.PublishFaults, Is.True);
            Assert.That(SwitchService.WritePolicy.MotionFlagOptional, Is.False);
        }

        private async Task<SwitchService> ConnectedAsync(FakeSwitchDevice device, DeviceCommandPolicy policy) {
            var svc = new SwitchService(faults: _faults) { DeviceFactory = _ => device, CommandPolicy = policy };
            await svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (svc.GetInfo() is not { Connected: true, WritableSwitches.Count: > 0 }) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect");
                await Task.Delay(5);
            }
            return svc;
        }

        private static Task Run(SwitchService svc, CancellationToken ct = default) =>
            ((ISwitchMediator)svc).SetSwitchValue(0, 0, new Progress<ApplicationStatus>(), ct);

        [Test]
        public async Task A_switch_write_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            // The handler is process-wide: count only this call's exception, not a stray task from another test.
            const string Marker = "late abandoned switch write (#1808)";
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            using var thrown = new ManualResetEventSlim(false);
            using var device = new FakeSwitchDevice();
            using var svc = await ConnectedAsync(device, Policy());
            device.OnSet = (_, _) => { release.Wait(TimeSpan.FromSeconds(10)); thrown.Set(); throw new InvalidOperationException(Marker); };
            try {
                await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(svc));
                Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
                // The abandoned call faults after the verdict; it must be observed, never an unobserved task.
                release.Set();
                Assert.That(thrown.Wait(TimeSpan.FromSeconds(10)), Is.True, "the abandoned call never faulted");
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Assert.That(unobserved, Is.Zero);
                Assert.That(_faults.Faults, Has.Count.EqualTo(1));
            } finally {
                release.Set();
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        }

        [Test]
        public async Task A_write_that_throws_is_one_OpError_with_the_device_text() {
            using var device = new FakeSwitchDevice();
            using var svc = await ConnectedAsync(device, Policy());
            device.OnSet = (_, _) => throw new InvalidOperationException("driver says no");
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(svc));
            Assert.That(ex!.Message, Is.EqualTo("switch write to port 0 failed: driver says no"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_completed_write_publishes_no_fault() {
            using var device = new FakeSwitchDevice();
            using var svc = await ConnectedAsync(device, Policy());
            await Run(svc);
            Assert.That(_faults.Faults, Is.Empty);
        }
    }
}
