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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 (#1808, slice 2d) - the Sequencer's mount commands (park, unpark, find home, goto,
    /// WaitForSlew, the meridian-flip pier-side hint and Sync) through the command module, against the fake
    /// mount with millisecond timeouts.</summary>
    [TestFixture]
    public class TelescopeCommandTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        // The fake mount sits at 5 h, +30 deg and reports the local-apparent system, so this target is "here".
        private static readonly Coordinates Here = new(Angle.ByHours(5.0), Angle.ByDegree(30.0), Epoch.JNOW);

        private sealed class FaultSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _faults = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_faults) { _faults.Add(fault); } }
            public List<EquipmentFaultEvent> Faults { get { lock (_faults) { return _faults.ToList(); } } }
        }

        private sealed class ListLogger : ILogger<TelescopeService> {
            private readonly List<string> _lines = new();
            public List<string> Lines { get { lock (_lines) { return _lines.ToList(); } } }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (_lines) { _lines.Add($"{logLevel}: {formatter(state, exception)}"); }
            }
        }

        private FaultSink _faults = null!;
        private ListLogger _log = null!;
        private OpenAstroAra.Profile.Profile? _profile;

        [SetUp]
        public void SetUp() {
            _faults = new FaultSink();
            _log = new ListLogger();
        }

        [TearDown]
        public void TearDown() => _profile?.Dispose();

        private async Task<TelescopeService> ConnectedAsync(FakeTelescopeDevice mount, Action<TelescopeService>? configure = null, bool useSideOfPier = false) {
            var profile = _profile = new OpenAstroAra.Profile.Profile();
            // Put the local sidereal time 3 h before the fake mount's 5 h, so a flip to it is expected on the west
            // side while the fake reports east: the pier-side hint is due.
            profile.AstrometrySettings.Longitude = ((2.0 - LstHours() + 24.0) % 24.0) * 15.0;
            profile.MeridianFlipSettings.UseSideOfPier = useSideOfPier;
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(p => p.ActiveProfile).Returns(profile);
            var svc = new TelescopeService(_log, faults: _faults, profileService: profiles.Object) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => mount,
            };
            configure?.Invoke(svc);
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!(((ITelescopeMediator)svc).GetInfo() is { Connected: true, Coordinates: not null })) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the connect and the first position");
                await Task.Delay(5);
            }
            return svc;
        }

        private static double LstHours() => SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0.0) / 15.0;

        // ── Sync: no fault, false on failure, the sequencer's token is ignored (Decision 2026-11-16) ───────

        [Test]
        public async Task Sync_is_false_with_no_fault_when_the_driver_throws() {
            using var mount = new FakeTelescopeDevice { OnSync = () => throw new InvalidOperationException("driver says no") };
            using var svc = await ConnectedAsync(mount);
            Assert.That(await svc.Sync(Here), Is.False);
            Assert.That(mount.Syncs, Is.EqualTo(1));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task Sync_on_a_mount_without_CanSync_is_false_and_sends_nothing() {
            using var mount = new FakeTelescopeDevice { CanSyncValue = false };
            using var svc = await ConnectedAsync(mount);
            Assert.That(await svc.Sync(Here), Is.False);
            Assert.That(mount.Syncs, Is.Zero);
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task Sync_ignores_a_cancelled_sequencer_token() {
            using var mount = new FakeTelescopeDevice();
            using var svc = await ConnectedAsync(mount);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            Assert.That(await svc.Sync(Here, cancelled.Token), Is.True);
            Assert.That(mount.Syncs, Is.EqualTo(1));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task A_good_Sync_is_true_and_sends_one_sync() {
            using var mount = new FakeTelescopeDevice();
            using var svc = await ConnectedAsync(mount);
            Assert.That(await svc.Sync(Here), Is.True);
            Assert.That(mount.Syncs, Is.EqualTo(1));
        }

        // ── The meridian-flip pier-side hint: best-effort, never a fault, the goto still runs ───────────────

        [Test]
        public async Task A_pier_side_hint_the_mount_refuses_is_logged_and_the_goto_still_runs() {
            using var mount = new FakeTelescopeDevice {
                CanSetPierSideValue = true,
                OnSideOfPierWrite = () => throw new InvalidOperationException("not from this state"),
            };
            using var svc = await ConnectedAsync(mount, useSideOfPier: true);
            Assert.That(await svc.MeridianFlip(Here, CancellationToken.None), Is.True);
            Assert.That(mount.Gotos, Is.EqualTo(1), "the goto runs after a refused hint");
            Assert.That(_log.Lines, Has.Some.Contains("the pier-side hint was refused"));
            Assert.That(_faults.Faults, Is.Empty, "the hint never publishes a fault");
        }

        [Test]
        public async Task A_mount_still_moving_after_the_pier_side_write_logs_it_and_the_goto_still_runs() {
            var written = false;
            var goneTo = false;
            using var mount = new FakeTelescopeDevice { CanSetPierSideValue = true };
            mount.OnSideOfPierWrite = () => Volatile.Write(ref written, true);
            mount.SlewingScript = () => Volatile.Read(ref written) && !Volatile.Read(ref goneTo);
            mount.OnCommand = () => Volatile.Write(ref goneTo, true);
            using var svc = await ConnectedAsync(mount, s => s.SlewSettleMaxPolls = 5, useSideOfPier: true);
            Assert.That(await svc.MeridianFlip(Here, CancellationToken.None), Is.True);
            Assert.That(written, Is.True, "the hint was written");
            Assert.That(mount.Gotos, Is.EqualTo(1), "the goto runs although the mount was still moving");
            Assert.That(_log.Lines, Has.Some.Contains("still moving after the pier-side write"));
            Assert.That(_faults.Faults, Is.Empty, "the hint never publishes a fault");
        }

        // ── The command module's policies (ADR 0011 rule 5): today's numbers, injectable bounds ───────────────

        // Built from the production declarations, so the device type and the required-flag rule under test are
        // the mount's own; only the timings shrink to milliseconds.
        private static DeviceCommandPolicy Mounted(int maxPolls = 40, double hardTimeoutMs = 100, double pollMs = 1) =>
            TelescopeService.MountOpPolicy with {
                HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs),
                PollInterval = TimeSpan.FromMilliseconds(pollMs),
                MaxPolls = maxPolls,
            };

        private static DeviceCommandPolicy Slew(int maxPolls = 40, double hardTimeoutMs = 100, double pollMs = 1) =>
            TelescopeService.SlewPolicy with {
                HardTimeout = TimeSpan.FromMilliseconds(hardTimeoutMs),
                PollInterval = TimeSpan.FromMilliseconds(pollMs),
                MaxPolls = maxPolls,
            };

        [Test]
        public void The_mount_op_policy_keeps_todays_timeouts_and_required_motion_flag() {
            Assert.That(TelescopeService.MountOpPolicy, Is.EqualTo(new DeviceCommandPolicy(DeviceType.Telescope,
                TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100), 1800,
                MotionFlagOptional: false, UnreadableReadsBeforeSettled: 0, PublishFaults: true)));
        }

        [Test]
        public void The_slew_policy_is_the_mount_op_policy_with_6000_polls() {
            Assert.That(TelescopeService.SlewPolicy, Is.EqualTo(TelescopeService.MountOpPolicy with { MaxPolls = 6000 }));
        }

        [Test]
        public async Task The_slew_poll_knob_sets_the_slew_policys_poll_count() {
            // #1265: a bench fixture shortens the slew's settle through SlewSettleMaxPolls; it still does.
            using var mount = new FakeTelescopeDevice();
            using var svc = await ConnectedAsync(mount, s => s.SlewSettleMaxPolls = 7);
            Assert.That(svc.SlewCommandPolicy.MaxPolls, Is.EqualTo(7));
            Assert.That(svc.SlewCommandPolicy, Is.EqualTo(TelescopeService.SlewPolicy with { MaxPolls = 7 }));
            Assert.That(svc.CommandPolicy, Is.EqualTo(TelescopeService.MountOpPolicy), "the knob leaves the mount ops alone");
        }

        // ── Hard timeout: a call that never returns is one StallTimeout, and the abandoned call is observed ───

        // The handler is process-wide: count only this call's exception, not a stray task from another test.
        private async Task AssertAbandonedCallObservedAsync(Action<FakeTelescopeDevice, Action> hang, Func<TelescopeService, Task> run, Action<TelescopeService> configure, bool expectFault) {
            const string Marker = "late abandoned mount call (#1808)";
            var unobserved = 0;
            void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
                if (e.Exception.InnerExceptions.Any(x => x.Message.Contains(Marker, StringComparison.Ordinal))) {
                    Interlocked.Increment(ref unobserved);
                }
            }
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            using var release = new ManualResetEventSlim(false);
            using var thrown = new ManualResetEventSlim(false);
            using var mount = new FakeTelescopeDevice();
            hang(mount, () => { release.Wait(TimeSpan.FromSeconds(10)); thrown.Set(); throw new InvalidOperationException(Marker); });
            using var svc = await ConnectedAsync(mount, configure, useSideOfPier: true);
            try {
                var watch = Stopwatch.StartNew();
                await run(svc);
                // The bound is the policy's 100 ms: waiting out the hung call (released only below) is the old, unbounded wait.
                Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "the command waited for the hung call");
                Assert.That(_faults.Faults.Select(f => f.Kind), expectFault ? Is.EqualTo(new[] { EquipmentFaultKind.StallTimeout }) : Is.Empty);
                // The abandoned call faults after the verdict; it must be observed, never an unobserved task.
                release.Set();
                Assert.That(thrown.Wait(TimeSpan.FromSeconds(10)), Is.True, "the abandoned call never faulted");
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Assert.That(unobserved, Is.Zero);
                Assert.That(_faults.Faults, expectFault ? Has.Count.EqualTo(1) : Is.Empty);
            } finally {
                release.Set();
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
        }

        [TestCase("park")]
        [TestCase("unpark")]
        [TestCase("findhome")]
        public async Task A_mount_op_call_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed(string op) {
            await AssertAbandonedCallObservedAsync((m, blocked) => m.OnCommand = blocked, async svc => {
                await Assert.ThrowsAsync<SequenceEntityFailedException>(() => op switch {
                    "park" => svc.ParkTelescope(null!, MoveAsker.Session, CancellationToken.None),
                    "unpark" => svc.UnparkTelescope(null!, CancellationToken.None),
                    _ => svc.FindHome(null!, MoveAsker.Session, CancellationToken.None),
                });
            }, svc => svc.CommandPolicy = Mounted(), expectFault: true);
        }

        [Test]
        public async Task A_goto_call_that_never_returns_fails_with_one_StallTimeout_and_the_abandoned_call_is_observed() {
            await AssertAbandonedCallObservedAsync((m, blocked) => m.OnCommand = blocked, async svc => {
                await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.SlewToCoordinatesAsync(Here, MoveAsker.Session, CancellationToken.None));
            }, svc => svc.SlewCommandPolicy = Slew(), expectFault: true);
        }

        [Test]
        public async Task A_Sync_call_that_never_returns_is_false_with_no_fault_and_the_abandoned_call_is_observed() {
            await AssertAbandonedCallObservedAsync((m, blocked) => m.OnSync = blocked, async svc => {
                Assert.That(await svc.Sync(Here), Is.False);
            }, svc => svc.CommandPolicy = Mounted(), expectFault: false);
        }

        [Test]
        public async Task A_pier_side_write_that_never_returns_leaves_a_log_line_and_the_goto_still_runs_with_no_fault() {
            FakeTelescopeDevice? mount = null;
            await AssertAbandonedCallObservedAsync((m, blocked) => { mount = m; m.CanSetPierSideValue = true; m.OnSideOfPierWrite = blocked; }, async svc => {
                Assert.That(await svc.MeridianFlip(Here, CancellationToken.None), Is.True);
                // A hard timeout carries its TimeoutException: logged as refused, not as still moving.
                Assert.That(_log.Lines, Has.Some.Contains("the pier-side hint was refused"));
                Assert.That(mount!.Gotos, Is.EqualTo(1), "the goto runs after the abandoned write");
            }, svc => svc.SlewCommandPolicy = Slew(), expectFault: false);
        }

        // ── Stall: the motion flag never clears ──────────────────────────────────────────────────────────────

        [Test]
        public async Task A_mount_that_keeps_slewing_past_the_bound_is_one_StallTimeout() {
            using var mount = new FakeTelescopeDevice { SlewingScript = () => true, AtParkScript = () => true };
            using var svc = await ConnectedAsync(mount, s => s.CommandPolicy = Mounted(maxPolls: 10));
            var ex = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.ParkTelescope(null!, MoveAsker.Session, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("telescope.park was not confirmed settled within the"));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task A_Slewing_that_is_always_unreadable_keeps_polling_and_fails_at_the_bound() {
            // Unpark, not park: an unreadable Slewing leaves the position unverified, which refuses a park (#2085).
            var reads = 0;
            using var mount = new FakeTelescopeDevice {
                AtParkScript = () => false,
                SlewingScript = () => { Interlocked.Increment(ref reads); throw new InvalidOperationException("no Slewing"); },
            };
            using var svc = await ConnectedAsync(mount, s => s.CommandPolicy = Mounted(maxPolls: 12));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.UnparkTelescope(null!, CancellationToken.None));
            // The motion flag is required (rule 6): an optional one would have settled after five reads.
            Assert.That(reads, Is.GreaterThanOrEqualTo(12));
            Assert.That(_faults.Faults.Count(f => f.Kind == EquipmentFaultKind.StallTimeout), Is.EqualTo(1));
        }

        [Test]
        public async Task A_command_fault_leaves_the_connection_healthy_and_adds_no_degraded_fault() {
            // ADR 0011 rule 7: the stall goes to the fault hub only; the lifecycle's read health never sees it.
            using var mount = new FakeTelescopeDevice { SlewingScript = () => true, AtParkScript = () => true };
            using var svc = await ConnectedAsync(mount, s => s.CommandPolicy = Mounted(maxPolls: 5));
            var before = mount.FailedReads;
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.ParkTelescope(null!, MoveAsker.Session, CancellationToken.None));
            await Task.Delay(150);
            Assert.That(mount.FailedReads, Is.EqualTo(before));
            Assert.That((await svc.GetAsync(CancellationToken.None))?.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(_faults.Faults.Select(f => f.Kind), Is.EqualTo(new[] { EquipmentFaultKind.StallTimeout }));
        }

        [Test]
        public async Task A_disconnect_mid_wait_fails_the_command_with_no_fault() {
            using var mount = new FakeTelescopeDevice { SlewingScript = () => true, AtParkScript = () => true };
            using var svc = await ConnectedAsync(mount, s => s.CommandPolicy = Mounted(maxPolls: 2000));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(async () => {
                var park = svc.ParkTelescope(null!, MoveAsker.Session, CancellationToken.None);
                await Task.Delay(30);
                await svc.DisconnectAsync(null, CancellationToken.None);
                await park;
            });
            Assert.That(_faults.Faults, Is.Empty);
        }

        // ── WaitForSlew runs on the slew policy: a mount still moving at the bound is a stall, not a quiet return

        [Test]
        public async Task WaitForSlew_on_a_mount_that_keeps_slewing_is_one_StallTimeout() {
            using var mount = new FakeTelescopeDevice { SlewingScript = () => true };
            using var svc = await ConnectedAsync(mount, s => s.SlewCommandPolicy = Slew(maxPolls: 5));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.WaitForSlew(CancellationToken.None));
            Assert.That(_faults.Faults.Single().Kind, Is.EqualTo(EquipmentFaultKind.StallTimeout));
        }

        [Test]
        public async Task WaitForSlew_run_without_op_faults_throws_at_the_bound_and_publishes_nothing() {
            // #1536: Polar Align waits for its slews inside RunWithoutOpFaultsAsync; the stall is its own to report.
            using var mount = new FakeTelescopeDevice { SlewingScript = () => true };
            using var svc = await ConnectedAsync(mount, s => s.SlewCommandPolicy = Slew(maxPolls: 5));
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => TelescopeService.RunWithoutOpFaultsAsync(async () => {
                await svc.WaitForSlew(CancellationToken.None);
                return true;
            }));
            Assert.That(_faults.Faults, Is.Empty);
        }

        [Test]
        public async Task WaitForSlew_on_a_mount_that_is_not_slewing_returns_and_publishes_nothing() {
            using var mount = new FakeTelescopeDevice();
            using var svc = await ConnectedAsync(mount, s => s.SlewCommandPolicy = Slew());
            await svc.WaitForSlew(CancellationToken.None);
            Assert.That(_faults.Faults, Is.Empty);
        }
    }
}
