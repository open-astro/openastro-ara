#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 (#1808) — the command module on its own: the hard timeout, the settle poll, the
    /// verdicts and the fault publish, driven with stub delegates (no device class).</summary>
    [TestFixture]
    public class DeviceCommandTest {

        private static readonly DiscoveredDeviceDto Device = new(
            UniqueId: "dev-1", Name: "Bench Rotator", Type: Contracts.DeviceType.Rotator,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private sealed class CapturingLogger : ILogger {
            private readonly List<(LogLevel Level, string Text)> _entries = new();
            public List<(LogLevel Level, string Text)> Entries { get { lock (_entries) { return _entries.ToList(); } } }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
            }
        }

        private FaultSink _faults = null!;
        private CapturingLogger _log = null!;

        [SetUp]
        public void SetUp() {
            _faults = new FaultSink();
            _log = new CapturingLogger();
        }

        private static DeviceCommandPolicy Policy(bool optional = false, int unreadable = 0, int maxPolls = 20, int hardMs = 50) =>
            new(Contracts.DeviceType.Rotator, TimeSpan.FromMilliseconds(hardMs), TimeSpan.FromMilliseconds(1), maxPolls, optional, unreadable);

        private Task Run(DeviceCommandPolicy policy, Action call, Func<bool?>? terminal = null, bool live = true, bool current = true,
                CancellationToken ct = default, Action? dispatched = null) {
            var command = new DeviceCommand(policy, _faults, _log);
            return command.RunAsync(new DeviceCommandRequest("rotator move to 90°", call, () => live, () => current ? Device : null,
                terminal, dispatched), ct);
        }

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(5);
            }
        }

        [Test]
        public async Task A_call_that_never_returns_fails_at_the_hard_timeout_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            try {
                var sw = Stopwatch.StartNew();
                var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                    Run(Policy(hardMs: 50), () => { release.Wait(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("late"); }));
                Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
                Assert.That(ex!.Message, Does.Contain("did not complete within"));
                var fault = _faults.Faults.Single();
                Assert.That((fault.DeviceType, fault.Kind), Is.EqualTo((Contracts.DeviceType.Rotator, EquipmentFaultKind.StallTimeout)));
                // The abandoned call fails later: it is observed (Debug log), never an unobserved task.
                release.Set();
                await WaitForAsync(() => _log.Entries.Any(e => e.Level == LogLevel.Debug && e.Text.Contains("abandoned")), "the abandoned call's log");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Assert.That(unobserved, Is.Zero);
            } finally {
                release.Set();
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        }

        [Test]
        public void A_motion_flag_that_stays_true_past_the_bound_is_one_StallTimeout() {
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(Policy(maxPolls: 5), () => { }, terminal: () => false));
            Assert.That(ex!.Message, Does.Contain("was not confirmed settled within"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_settled_read_ends_the_command_with_no_fault() {
            var reads = 0;
            var dispatched = 0;
            await Run(Policy(), () => { }, terminal: () => ++reads >= 3, dispatched: () => dispatched++);
            Assert.That((reads, dispatched, _faults.Faults.Count), Is.EqualTo((3, 1, 0)));
        }

        [Test]
        public async Task A_command_with_no_terminal_condition_ends_when_the_call_returns() {
            var called = 0;
            await Run(Policy(), () => called++, terminal: null);
            Assert.That(called, Is.EqualTo(1));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public void A_required_flag_never_settles_on_unreadable_reads() {
            var reads = 0;
            Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                Run(Policy(optional: false, unreadable: 5, maxPolls: 12), () => { }, terminal: () => { reads++; return null; }));
            Assert.That(reads, Is.EqualTo(12), "every poll read, none ended the wait");
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task An_optional_flag_settles_after_N_unreadable_reads_in_a_row() {
            var reads = 0;
            await Run(Policy(optional: true, unreadable: 5, maxPolls: 50), () => { }, terminal: () => { reads++; return null; });
            Assert.That(reads, Is.EqualTo(5));
        }

        [Test]
        public void A_flaky_optional_flag_does_not_settle_early() {
            var reads = 0;
            // unreadable, moving, unreadable, moving ...: the streak never reaches 5.
            Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                Run(Policy(optional: true, unreadable: 5, maxPolls: 20), () => { }, terminal: () => ++reads % 2 == 1 ? null : false));
            Assert.That(reads, Is.EqualTo(20));
        }

        [Test]
        public void A_call_that_throws_is_an_OpError_with_the_device_text() {
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                Run(Policy(), () => throw new InvalidOperationException("driver says no")));
            Assert.That(ex!.Message, Is.EqualTo("rotator move to 90° failed: driver says no"));
            var fault = _faults.Faults.Single();
            Assert.That((fault.Kind, fault.Details), Is.EqualTo((EquipmentFaultKind.OpError, ex.Message)));
        }

        [Test]
        public void A_sequencer_cancel_propagates_with_no_fault() {
            using var release = new ManualResetEventSlim(false);
            using var cts = new CancellationTokenSource();
            try {
                var run = Run(Policy(hardMs: 5000), () => release.Wait(TimeSpan.FromSeconds(10)), ct: cts.Token);
                cts.CancelAfter(30);
                Assert.CatchAsync<OperationCanceledException>(() => run);
                Assert.That(_faults.Faults, Is.Empty);
            } finally {
                release.Set();
            }
        }

        [Test]
        public void A_cancel_during_the_settle_wait_propagates_with_no_fault() {
            using var cts = new CancellationTokenSource();
            var run = Run(Policy(maxPolls: 1000), () => { }, terminal: () => false, ct: cts.Token);
            cts.CancelAfter(30);
            Assert.CatchAsync<OperationCanceledException>(() => run);
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public void A_device_that_is_gone_mid_wait_fails_the_command_but_publishes_nothing() {
            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                Run(Policy(), () => { }, terminal: () => false, live: false, current: false));
            Assert.That(ex!.Message, Does.Contain("was not confirmed settled"));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public void A_policy_that_opts_out_of_publishing_fails_the_command_with_no_fault() {
            var policy = Policy(maxPolls: 3) with { PublishFaults = false };
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => Run(policy, () => throw new InvalidOperationException("x")));
            Assert.That(_faults.Faults, Is.Empty);
        }
    }
}
