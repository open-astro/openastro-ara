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

    /// <summary>ADR 0011 (#1808) — the Sequencer's rotator move through the command module, against the
    /// fake adapter with millisecond timeouts.</summary>
    [TestFixture]
    public class RotatorCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "rot-1", Name: "Bench Rotator", Type: Contracts.DeviceType.Rotator,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private FaultSink _faults = null!;

        [SetUp]
        public void SetUp() => _faults = new FaultSink();

        private static DeviceCommandPolicy Policy(int maxPolls = 40) =>
            new(Contracts.DeviceType.Rotator, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1), maxPolls,
                MotionFlagOptional: true, UnreadableReadsBeforeSettled: 5);

        private async Task<RotatorService> ConnectedAsync(FakeRotatorDevice rotator, DeviceCommandPolicy policy) {
            var svc = new RotatorService(faults: _faults) { DeviceFactory = _ => rotator, CommandPolicy = policy };
            await svc.ConnectAsync(new ConnectRequestDto(Device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (svc.GetAsync(CancellationToken.None).Result?.State != EquipmentConnectionState.Connected) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect");
                await Task.Delay(5);
            }
            return svc;
        }

        private static Task<float> Move(RotatorService svc, CancellationToken ct = default) =>
            ((IRotatorMediator)svc).MoveMechanical(90f, ct);

        [Test]
        public async Task A_move_that_never_returns_fails_with_one_StallTimeout() {
            using var release = new ManualResetEventSlim(false);
            using var rotator = new FakeRotatorDevice { OnMove = _ => release.Wait(TimeSpan.FromSeconds(10)) };
            using var svc = await ConnectedAsync(rotator, Policy());
            try {
                Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
                Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
            } finally {
                release.Set();
            }
        }

        [Test]
        public async Task A_rotator_that_keeps_moving_past_the_bound_is_one_StallTimeout() {
            using var rotator = new FakeRotatorDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(rotator, Policy(maxPolls: 10));
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            Assert.That(ex!.Message, Does.Contain("rotator move to 90° was not confirmed settled within the"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task An_IsMoving_that_is_always_unreadable_settles_after_five_polls() {
            var reads = 0;
            using var rotator = new FakeRotatorDevice { IsMovingScript = () => { Interlocked.Increment(ref reads); throw new InvalidOperationException("no IsMoving"); } };
            using var svc = await ConnectedAsync(rotator, Policy());
            var angle = await Move(svc);
            Assert.That(reads, Is.EqualTo(5));
            Assert.That(angle, Is.EqualTo(10f));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_flaky_IsMoving_does_not_settle_the_move_early() {
            var reads = 0;
            using var rotator = new FakeRotatorDevice {
                IsMovingScript = () => Interlocked.Increment(ref reads) % 2 == 1 ? throw new InvalidOperationException("blip") : true,
            };
            using var svc = await ConnectedAsync(rotator, Policy(maxPolls: 14));
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            Assert.That(reads, Is.GreaterThanOrEqualTo(14));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_move_that_throws_is_one_OpError_with_the_device_text() {
            using var rotator = new FakeRotatorDevice { OnMove = _ => throw new InvalidOperationException("driver says no") };
            using var svc = await ConnectedAsync(rotator, Policy());
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            Assert.That(ex!.Message, Is.EqualTo("rotator move to 90° failed: driver says no"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_sequencer_cancel_during_the_move_propagates_with_no_fault() {
            using var release = new ManualResetEventSlim(false);
            using var rotator = new FakeRotatorDevice { OnMove = _ => release.Wait(TimeSpan.FromSeconds(10)) };
            using var svc = await ConnectedAsync(rotator, new DeviceCommandPolicy(Contracts.DeviceType.Rotator, TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(1), 40, true, 5));
            using var cts = new CancellationTokenSource();
            try {
                var move = Move(svc, cts.Token);
                cts.CancelAfter(30);
                Assert.CatchAsync<OperationCanceledException>(() => move);
                Assert.That(_faults.Faults, Is.Empty);
            } finally {
                release.Set();
            }
        }

        [Test]
        public async Task A_disconnect_mid_wait_fails_the_move_with_no_stall_fault() {
            using var rotator = new FakeRotatorDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(rotator, Policy(maxPolls: 2000));
            var move = Move(svc);
            await Task.Delay(30);
            await svc.DisconnectAsync(null, CancellationToken.None);
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => move);
            Assert.That(_faults.Faults.Where(f => f.Kind == EquipmentFaultKind.StallTimeout), Is.Empty);
        }

        [Test]
        public async Task A_command_fault_leaves_the_connection_healthy_and_adds_no_degraded_fault() {
            // ADR 0011 rule 7: the stall goes to the fault hub only; the lifecycle's read health and
            // clean-read gate never see it, so the rotator stays Connected with no Degraded episode.
            using var rotator = new FakeRotatorDevice { IsMovingScript = () => true };
            using var svc = await ConnectedAsync(rotator, Policy(maxPolls: 5));
            var before = rotator.FailedReads;
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => Move(svc));
            await Task.Delay(150);
            Assert.That(rotator.FailedReads, Is.EqualTo(before));
            Assert.That((await svc.GetAsync(CancellationToken.None))?.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(_faults.Faults.Select(f => f.Kind), Is.EqualTo(new[] { EquipmentFaultKind.StallTimeout }));
        }
    }
}
