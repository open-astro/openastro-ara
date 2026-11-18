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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 (#1808) - the Sequencer's focuser moves through the command module, against the
    /// fake adapter with millisecond timeouts.</summary>
    [TestFixture]
    public class FocuserCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "focuser-1", Name: "Bench Focuser", Type: DeviceType.Focuser,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private FaultSink _faults = null!;

        [SetUp]
        public void SetUp() => _faults = new FaultSink();

        // Built from the production declaration, so the device type and the optional-flag rule under test
        // are the Focuser's own; only the timings shrink to milliseconds.
        private static DeviceCommandPolicy Policy(int maxPolls = 40, double hardTimeoutMs = 100, double pollMs = 1) =>
            FocuserService.MovePolicy with {
                HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs),
                PollInterval = TimeSpan.FromMilliseconds(pollMs),
                MaxPolls = maxPolls,
            };

        [Test]
        public void The_focuser_policy_keeps_todays_timeouts_and_optional_motion_flag() {
            Assert.That(FocuserService.MovePolicy, Is.EqualTo(new DeviceCommandPolicy(DeviceType.Focuser,
                TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100), 600,
                MotionFlagOptional: true, UnreadableReadsBeforeSettled: 5, PublishFaults: true)));
        }

        private async Task<FocuserService> ConnectedAsync(FakeFocuserDevice focuser, DeviceCommandPolicy policy) {
            var svc = new FocuserService(faults: _faults) { DeviceFactory = _ => focuser, CommandPolicy = policy };
            await svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((await svc.GetAsync(CancellationToken.None))?.State != EquipmentConnectionState.Connected) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect");
                await Task.Delay(5);
            }
            return svc;
        }

        private static Task<int> Move(FocuserService svc, CancellationToken ct = default) =>
            ((IFocuserMediator)svc).MoveFocuser(700, ct);

        [Test]
        public async Task A_focuser_Move_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            // The handler is process-wide: count only this call's exception, not a stray task from another test.
            const string Marker = "late abandoned focuser move (#1808)";
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            using var thrown = new ManualResetEventSlim(false);
            using var focuser = new FakeFocuserDevice {
                OnMove = _ => { release.Wait(TimeSpan.FromSeconds(10)); thrown.Set(); throw new InvalidOperationException(Marker); },
            };
            using var svc = await ConnectedAsync(focuser, Policy());
            try {
                await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
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
        public async Task A_focuser_that_keeps_moving_past_the_bound_is_one_StallTimeout() {
            using var focuser = new FakeFocuserDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(focuser, Policy(maxPolls: 10));
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            Assert.That(ex!.Message, Does.Contain("focuser Move to 700 was not confirmed settled within the"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task An_IsMoving_that_throws_five_times_in_a_row_settles_with_no_fault() {
            var moved = 0;
            using var focuser = new FakeFocuserDevice {
                OnMove = _ => Volatile.Write(ref moved, 1),
                IsMovingScript = () => Volatile.Read(ref moved) == 0 ? false : throw new InvalidOperationException("no IsMoving"),
            };
            using var svc = await ConnectedAsync(focuser, Policy(maxPolls: 1000));
            await Move(svc); // would fail at the 1000-poll bound if the flag were required
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task Fewer_than_five_unreadable_IsMoving_reads_then_a_clear_read_settles_with_no_fault() {
            var reads = 0;
            using var focuser = new FakeFocuserDevice {
                IsMovingScript = () => Interlocked.Increment(ref reads) <= 3 ? throw new InvalidOperationException("blip") : false,
            };
            using var svc = await ConnectedAsync(focuser, Policy());
            await Move(svc);
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_disconnect_mid_wait_fails_the_move_with_no_fault() {
            using var focuser = new FakeFocuserDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(focuser, Policy(maxPolls: 2000));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(async () => {
                var move = Move(svc);
                await Task.Delay(30);
                await svc.DisconnectAsync(null, CancellationToken.None);
                await move;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_sequencer_cancel_during_the_wait_propagates_with_no_fault() {
            using var focuser = new FakeFocuserDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(focuser, Policy(maxPolls: 2000));
            await Assert.ThrowsAsync<TaskCanceledException>(async () => {
                using var cts = new CancellationTokenSource();
                var move = Move(svc, cts.Token);
                await Task.Delay(30);
                await cts.CancelAsync();
                await move;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_Move_that_throws_is_one_OpError_with_the_device_text() {
            using var focuser = new FakeFocuserDevice { OnMove = _ => throw new InvalidOperationException("driver says no") };
            using var svc = await ConnectedAsync(focuser, Policy());
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            Assert.That(ex!.Message, Is.EqualTo("focuser Move to 700 failed: driver says no"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_completed_move_returns_the_position_read_from_the_device() {
            using var focuser = new FakeFocuserDevice { MoveLands = true };
            using var svc = await ConnectedAsync(focuser, Policy());
            Assert.That(await Move(svc), Is.EqualTo(700));
            Assert.That(_faults.Faults, Is.Empty);
        }
    }
}
