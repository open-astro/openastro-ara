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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 (#1808) - the Sequencer's dome commands through the command module, against the
    /// fake adapter with millisecond timeouts.</summary>
    [TestFixture]
    public class DomeCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "dome-1", Name: "Bench Dome", Type: DeviceType.Dome,
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
        // are the Dome's own; only the timings shrink to milliseconds.
        private static DeviceCommandPolicy Policy(int maxPolls = 40, double hardTimeoutMs = 100, double pollMs = 1) =>
            DomeService.DomePolicy with {
                HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs),
                PollInterval = TimeSpan.FromMilliseconds(pollMs),
                MaxPolls = maxPolls,
            };

        [Test]
        public void The_dome_policy_keeps_todays_timeouts_and_required_motion_flag() {
            Assert.That(DomeService.DomePolicy, Is.EqualTo(new DeviceCommandPolicy(DeviceType.Dome,
                TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100), 1800,
                MotionFlagOptional: false, UnreadableReadsBeforeSettled: 0, PublishFaults: true)));
        }

        private async Task<DomeService> ConnectedAsync(FakeDomeDevice dome, DeviceCommandPolicy policy) {
            var svc = new DomeService(faults: _faults) { DeviceFactory = _ => dome, CommandPolicy = policy };
            await svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((await svc.GetAsync(CancellationToken.None))?.State != EquipmentConnectionState.Connected) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect");
                await Task.Delay(5);
            }
            return svc;
        }

        private static Task<bool> Park(DomeService svc, CancellationToken ct = default) =>
            ((IDomeMediator)svc).Park(ct);

        [Test]
        public async Task A_dome_call_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            // The handler is process-wide: count only this call's exception, not a stray task from another test.
            const string Marker = "late abandoned dome call (#1808)";
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            using var thrown = new ManualResetEventSlim(false);
            using var dome = new FakeDomeDevice {
                OnCommand = () => { release.Wait(TimeSpan.FromSeconds(10)); thrown.Set(); throw new InvalidOperationException(Marker); },
            };
            using var svc = await ConnectedAsync(dome, Policy());
            try {
                await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Park(svc));
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
        public async Task A_dome_that_keeps_slewing_past_the_bound_is_one_StallTimeout() {
            using var dome = new FakeDomeDevice { SlewingScript = () => true };
            using var svc = await ConnectedAsync(dome, Policy(maxPolls: 10));
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Park(svc));
            Assert.That(ex!.Message, Does.Contain("dome.park was not confirmed settled within the"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_Slewing_that_is_always_unreadable_keeps_polling_and_fails_at_the_bound() {
            var reads = 0;
            using var dome = new FakeDomeDevice { SlewingScript = () => { Interlocked.Increment(ref reads); throw new InvalidOperationException("no Slewing"); } };
            using var svc = await ConnectedAsync(dome, Policy(maxPolls: 12));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Park(svc));
            // The motion flag is required: an optional one would have settled after five reads.
            Assert.That(reads, Is.GreaterThanOrEqualTo(12));
            // The lifecycle's own refresh reads Slewing too, so its read-health fault may join; the command's
            // verdict is the one StallTimeout.
            Assert.That(_faults.Faults.Count(f => f.Kind == EquipmentFaultKind.StallTimeout), Is.EqualTo(1));
        }

        [Test]
        public async Task A_command_already_finished_returns_one_poll_interval_later() {
            using var dome = new FakeDomeDevice(); // parked and not slewing from the start
            using var svc = await ConnectedAsync(dome, Policy(pollMs: 300, hardTimeoutMs: 30_000));
            var watch = Stopwatch.StartNew();
            Assert.That(await Park(svc), Is.True);
            // Wait-then-check: the first read comes after one 300 ms poll, not before it.
            Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(250));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_dome_call_that_throws_is_one_OpError_with_the_device_text() {
            using var dome = new FakeDomeDevice { OnCommand = () => throw new InvalidOperationException("driver says no") };
            using var svc = await ConnectedAsync(dome, Policy());
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Park(svc));
            Assert.That(ex!.Message, Is.EqualTo("dome.park failed: driver says no"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_disconnect_mid_wait_fails_the_command_with_no_fault() {
            using var dome = new FakeDomeDevice { SlewingScript = () => true };
            using var svc = await ConnectedAsync(dome, Policy(maxPolls: 2000));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(async () => {
                var park = Park(svc);
                await Task.Delay(30);
                await svc.DisconnectAsync(null, CancellationToken.None);
                await park;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_command_fault_leaves_the_connection_healthy_and_adds_no_degraded_fault() {
            // ADR 0011 rule 7: the stall goes to the fault hub only; the lifecycle's read health never sees it.
            using var dome = new FakeDomeDevice { SlewingScript = () => true };
            using var svc = await ConnectedAsync(dome, Policy(maxPolls: 5));
            var before = dome.FailedReads;
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Park(svc));
            await Task.Delay(150);
            Assert.That(dome.FailedReads, Is.EqualTo(before));
            Assert.That((await svc.GetAsync(CancellationToken.None))?.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(_faults.Faults.Select(f => f.Kind), Is.EqualTo(new[] { EquipmentFaultKind.StallTimeout }));
        }
    }
}
