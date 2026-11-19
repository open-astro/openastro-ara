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

    /// <summary>ADR 0011 (#1808) - the Sequencer's filter changes go through the command module, against the
    /// fake adapter with millisecond timeouts.</summary>
    [TestFixture]
    public class FilterWheelCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "filterwheel-1", Name: "Bench Wheel", Type: DeviceType.FilterWheel,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private FaultSink _faults = null!;

        [SetUp]
        public void SetUp() => _faults = new FaultSink();

        // Built from the production declaration, so the device type and the required-flag rule under test
        // are the FilterWheel's own; only the timings shrink to milliseconds.
        private static DeviceCommandPolicy Policy(int maxPolls = 40, double hardTimeoutMs = 100, double pollMs = 1) =>
            FilterWheelService.ChangePolicy with {
                HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs),
                PollInterval = TimeSpan.FromMilliseconds(pollMs),
                MaxPolls = maxPolls,
            };

        [Test]
        public void The_filter_wheel_policy_keeps_todays_timeouts_and_required_motion_flag() {
            Assert.That(FilterWheelService.ChangePolicy, Is.EqualTo(new DeviceCommandPolicy(DeviceType.FilterWheel,
                TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100), 600,
                MotionFlagOptional: false, UnreadableReadsBeforeSettled: 0, PublishFaults: true)));
        }

        private async Task<FilterWheelService> ConnectedAsync(FakeFilterWheelDevice wheel, DeviceCommandPolicy policy) {
            var svc = new FilterWheelService(faults: _faults) { DeviceFactory = _ => wheel, CommandPolicy = policy };
            await svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((await svc.GetAsync(CancellationToken.None))?.State != EquipmentConnectionState.Connected) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect");
                await Task.Delay(5);
            }
            return svc;
        }

        // The wheel reads position 1 until a test scripts it, so slot 0 is a real change.
        private static Task<FilterInfo> Run(FilterWheelService svc, CancellationToken ct = default) =>
            ((IFilterWheelMediator)svc).ChangeFilter(new FilterInfo("L", 0, 0), token: ct);

        [Test]
        public async Task A_filter_wheel_Position_write_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            // The handler is process-wide: count only this call's exception, not a stray task from another test.
            const string Marker = "late abandoned filter change (#1808)";
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            using var thrown = new ManualResetEventSlim(false);
            using var device = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(device, Policy());
            device.OnSetPosition = _ => { release.Wait(TimeSpan.FromSeconds(10)); thrown.Set(); throw new InvalidOperationException(Marker); };
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
        public async Task A_wheel_that_never_reaches_the_target_is_one_StallTimeout_at_the_bound() {
            using var wheel = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(wheel, Policy(maxPolls: 10));
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(svc));
            Assert.That(ex!.Message, Does.Contain("filter change to position 0 was not confirmed settled within the"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_Position_read_that_throws_keeps_polling_and_fails_at_the_bound() {
            using var wheel = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(wheel, Policy(maxPolls: 10));
            wheel.PositionScript = () => throw new InvalidOperationException("no position");
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(svc));
            Assert.That(ex!.Message, Does.Contain("was not confirmed settled"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_good_read_equal_to_the_target_after_unreadable_ones_finishes_with_no_fault() {
            var reads = 0;
            using var wheel = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(wheel, Policy());
            wheel.PositionScript = () => Interlocked.Increment(ref reads) <= 8 ? throw new InvalidOperationException("blip") : (short)0;
            var reached = await Run(svc);
            Assert.That(reached.Position, Is.EqualTo(0));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_Position_write_that_throws_is_one_OpError_with_the_device_text() {
            using var wheel = new FakeFilterWheelDevice { OnSetPosition = _ => throw new InvalidOperationException("driver says no") };
            using var svc = await ConnectedAsync(wheel, Policy());
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(svc));
            Assert.That(ex!.Message, Is.EqualTo("filter change to position 0 failed: driver says no"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_sequencer_cancel_during_the_wait_propagates_with_no_fault() {
            using var wheel = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(wheel, Policy(maxPolls: 2000));
            await Assert.ThrowsAsync<TaskCanceledException>(async () => {
                using var cts = new CancellationTokenSource();
                var change = Run(svc, cts.Token);
                await Task.Delay(30);
                await cts.CancelAsync();
                await change;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_disconnect_mid_wait_fails_the_change_with_no_fault() {
            using var wheel = new FakeFilterWheelDevice();
            using var svc = await ConnectedAsync(wheel, Policy(maxPolls: 2000));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(async () => {
                var change = Run(svc);
                await Task.Delay(30);
                await svc.DisconnectAsync(null, CancellationToken.None);
                await change;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }
    }
}
