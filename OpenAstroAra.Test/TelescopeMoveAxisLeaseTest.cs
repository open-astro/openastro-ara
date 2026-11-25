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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1649 — a direction-pad press carries a lease; when the renewals stop (a sleeping iPad,
    /// dropped Wi-Fi) the daemon stops that axis by itself. A request without a lease behaves as before.</summary>
    [TestFixture]
    public class MoveAxisLeaseTest {

        private static readonly DateTimeOffset T0 = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        private static readonly int[] Primary = [0];
        private static readonly int[] Secondary = [1];

        [Test]
        public void A_lease_is_clamped_to_250_to_5000_ms() {
            Assert.That(MoveAxisLease.Clamp(10), Is.EqualTo(250));
            Assert.That(MoveAxisLease.Clamp(1000), Is.EqualTo(1000));
            Assert.That(MoveAxisLease.Clamp(60_000), Is.EqualTo(5000));
        }

        [Test]
        public void An_unrenewed_axis_expires_after_its_lease_and_only_once() {
            var lease = new MoveAxisLease();
            lease.Renew(1, 1.0, 1000, T0);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(999)), Is.Empty);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1000)), Is.EqualTo(Secondary));
            Assert.That(lease.TakeExpired(T0.AddSeconds(5)), Is.Empty, "an expired axis is stopped once");
            Assert.That(lease.Any, Is.False);
        }

        [Test]
        public void Renewals_keep_an_axis_moving() {
            var lease = new MoveAxisLease();
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(0, 1.0, 1000, T0.AddMilliseconds(300));
            lease.Renew(0, 1.0, 1000, T0.AddMilliseconds(600));
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1500)), Is.Empty);
            Assert.That(lease.TakeExpired(T0.AddMilliseconds(1600)), Is.EqualTo(Primary));
        }

        [Test]
        public void Only_a_live_lease_at_the_same_rate_is_a_renewal() {
            var lease = new MoveAxisLease();
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0), Is.False, "nothing to renew yet");
            lease.Renew(0, 1.0, 1000, T0);
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0.AddMilliseconds(500)), Is.True);
            Assert.That(lease.TryRenew(0, 2.0, 1000, T0.AddMilliseconds(600)), Is.False, "another speed");
            Assert.That(lease.TryRenew(1, 1.0, 1000, T0.AddMilliseconds(600)), Is.False, "another axis");
            Assert.That(lease.TryRenew(0, 1.0, 1000, T0.AddMilliseconds(1600)), Is.False, "the lease ran out");
        }

        [Test]
        public void Each_axis_has_its_own_deadline_and_cancel_clears_it() {
            var lease = new MoveAxisLease();
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(1, 1.0, 1000, T0);
            lease.Cancel(1);
            Assert.That(lease.TakeExpired(T0.AddSeconds(2)), Is.EqualTo(Primary));
            lease.Renew(0, 1.0, 1000, T0);
            lease.Renew(1, 1.0, 1000, T0);
            lease.CancelAll();
            Assert.That(lease.TakeExpired(T0.AddSeconds(2)), Is.Empty);
        }
    }

    /// <summary>#1649 — the lease end to end on the telescope service, against a scripted Alpaca mount
    /// and a hand-advanced clock (no sleeps for the lease itself).</summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeMoveAxisLeaseTest {

        private sealed class ManualClock : TimeProvider {
            private readonly object _lock = new();
            private DateTimeOffset _now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

            public DateTimeOffset Now {
                get {
                    lock (_lock) {
                        return _now;
                    }
                }
                set {
                    lock (_lock) {
                        _now = value;
                    }
                }
            }

            public void Advance(TimeSpan by) {
                lock (_lock) {
                    _now += by;
                }
            }

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Mount() => path =>
            path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/canmoveaxis", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static async Task<(ScriptedAlpacaDevice Box, TelescopeService Svc, ManualClock Clock)> ArmedAsync(
                Func<string, string?>? putResponder = null, Microsoft.Extensions.Logging.ILogger<TelescopeService>? logger = null,
                TimeProvider? poseClock = null) {
            var box = ScriptedAlpacaDevice.Start(Mount(), putResponder);
            box.RespondWithQuery((path, _) => path.EndsWith("/axisrates", StringComparison.Ordinal)
                ? "[{\"Minimum\":0.002,\"Maximum\":4.0}]" : null);
            var clock = new ManualClock();
            var svc = new TelescopeService(logger) {
                RefreshPeriod = TimeSpan.FromMilliseconds(100), LeaseClock = clock, LeaseCheckInterval = Timeout.InfiniteTimeSpan,
                PoseClock = poseClock ?? TimeProvider.System,
            };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while ((await svc.GetAsync(CancellationToken.None))?.Capabilities?.MoveAxisRateBandsDegPerSec is not { Count: > 0 }) {
                if (sw.Elapsed > TimeSpan.FromSeconds(15)) {
                    Assert.Fail("capabilities never published");
                }
                await Task.Delay(50);
            }
            return (box, svc, clock);
        }

        private static string[] MoveAxisPuts(ScriptedAlpacaDevice box) =>
            [.. box.Puts.Where(p => p.Path.EndsWith("/moveaxis", StringComparison.Ordinal)).Select(p => p.Body)];

        private static bool IsStop(string body, int axis) =>
            body.Contains($"Axis={axis}", StringComparison.Ordinal) && body.Contains("Rate=0&", StringComparison.Ordinal)
            || body.Contains($"Axis={axis}", StringComparison.Ordinal) && body.EndsWith("Rate=0", StringComparison.Ordinal);

        private sealed class WarningCounter : Microsoft.Extensions.Logging.ILogger<TelescopeService> {
            private int _snapped;
            public int Snapped => Volatile.Read(ref _snapped);
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (formatter(state, exception).Contains("snapped to", StringComparison.Ordinal)) {
                    Interlocked.Increment(ref _snapped);
                }
            }
        }

        // Polar alignment stops its motor moves on this read: it must be the driver's answer now, not the 2 s refresh.
        [Test]
        public async Task ReadPointingNow_returns_the_drivers_position_at_once() {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            box.Respond(path => path.EndsWith("/rightascension", StringComparison.Ordinal) ? "7.5" : mount(path));

            var now = await svc.ReadPointingNowAsync(CancellationToken.None);

            Assert.That(now, Is.EqualTo(((double, double)?)(7.5, 45.0)));
        }

        [Test]
        public async Task ReadPointingNow_is_null_without_a_mount() {
            using var svc = new TelescopeService();

            Assert.That(await svc.ReadPointingNowAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ReadPointingNow_is_null_when_the_driver_read_fails() {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            box.Respond(path => path.EndsWith("/rightascension", StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error("no reading") : mount(path));

            Assert.That(await svc.ReadPointingNowAsync(CancellationToken.None), Is.Null);
        }

        // #2066 (ARA-5) — Polar Align plans and moves only from this reading: the driver's raw answer now (a Dec past the pole
        // is not folded, so the routine can refuse it), stamped when the read began and with the mount session it came from.
        [Test]
        public async Task ReadNow_returns_the_drivers_raw_pose_stamped_with_its_time_and_session() {
            var stamp = new ManualClock { Now = new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero) };
            var (box, svc, _) = await ArmedAsync(poseClock: stamp);
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            // Every Dec read takes 3 s on the stamp's clock (a stalled Wi-Fi link): a reading stamped when it ended would
            // look 3 s younger than it is. The refresh's own reads move the clock too, so only the gap is checked.
            box.Respond(path => path.EndsWith("/rightascension", StringComparison.Ordinal) ? "7.5"
                : path.EndsWith("/declination", StringComparison.Ordinal) ? Slow("90.9")
                : path.EndsWith("/slewing", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? "1"
                : mount(path));
            string Slow(string answer) {
                stamp.Advance(TimeSpan.FromSeconds(3));
                return answer;
            }

            var now = await svc.ReadNowAsync(CancellationToken.None);

            Assert.That(now, Is.Not.Null);
            Assert.That((now!.RaHours, now.DecDegrees), Is.EqualTo((7.5, 90.9)), "the driver's answer, never folded");
            Assert.That(now.Slewing, Is.True, "Slewing read now, not the cached false");
            Assert.That(now.SideOfPier, Is.EqualTo(PierSide.pierWest), "pointing state 1 (through the pole)");
            Assert.That(stamp.Now - now.ReadAtUtc, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(3)), "stamped when the read began, before its slow Dec");
            Assert.That(now.SessionEpoch, Is.EqualTo(svc.SessionEpoch).And.Not.Zero, "the session the read came from");
        }

        // #2083 — the rate a routine's turn runs at comes from the mount's AxisRates, at or below the request: a request
        // under every band has none (the pad's own snap would raise it to the band).
        [Test]
        public async Task MatchMoveAxisRate_takes_the_mounts_bands_at_or_below_the_request() {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            IMountMotionControl seam = svc;

            Assert.That(seam.MatchMoveAxisRate(0, 3.0), Is.EqualTo(3.0), "inside the mount's 0.002–4 °/s band");
            Assert.That(seam.MatchMoveAxisRate(0, -9.0), Is.EqualTo(-4.0), "over it: its top, sign kept");
            Assert.That(seam.MatchMoveAxisRate(0, 0.001), Is.Null, "under it: none, never raised to 0.002");
            Assert.That(TelescopeService.SnapMoveAxisRate(0.001, [(0.002, 4.0)]), Is.EqualTo(0.002), "where the pad's snap raises it");
        }

        [Test]
        public async Task ReadNow_is_null_when_the_position_read_fails_and_unknown_when_only_slewing_fails() {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            box.Respond(path => path.EndsWith("/slewing", StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error("busy") : mount(path));
            var slewingUnknown = await svc.ReadNowAsync(CancellationToken.None);
            box.Respond(path => path.EndsWith("/declination", StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error("no reading") : mount(path));

            Assert.That(await svc.ReadNowAsync(CancellationToken.None), Is.Null, "no position, no reading");
            Assert.That(slewingUnknown, Is.Not.Null);
            Assert.That(slewingUnknown!.Slewing, Is.Null, "a failed Slewing read is unknown, never 'not slewing'");
        }

        // #2048 — Polar Align confirms its stop on this read: the driver's Slewing now, not the 2 s refresh.
        [TestCase("true", true)]
        [TestCase("false", false)]
        public async Task ReadSlewingNow_returns_the_drivers_slewing_at_once(string answer, bool expected) {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            // The cache was filled with "false": a true answer can only come from a read of the driver now.
            box.Respond(path => path.EndsWith("/slewing", StringComparison.Ordinal) ? answer : mount(path));

            Assert.That(await svc.ReadSlewingNowAsync(CancellationToken.None), Is.EqualTo(expected));
        }

        // #2089 — Polar Align forgets its sweep origin when the mount session changes: a connect and a disconnect each count.
        [Test]
        public async Task SessionEpoch_changes_on_connect_and_on_disconnect() {
            using var idle = new TelescopeService();
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var connected = svc.SessionEpoch;

            await svc.DisconnectAsync(null, CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while (svc.SessionEpoch == connected && sw.Elapsed < TimeSpan.FromSeconds(15)) {
                await Task.Delay(20);
            }

            Assert.That(connected, Is.GreaterThan(idle.SessionEpoch), "the connect began a new session");
            Assert.That(svc.SessionEpoch, Is.GreaterThan(connected), "the disconnect ended it");
        }

        [Test]
        public async Task ReadSlewingNow_is_null_without_a_mount() {
            using var svc = new TelescopeService();

            Assert.That(await svc.ReadSlewingNowAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ReadSlewingNow_is_null_when_the_driver_read_fails() {
            var (box, svc, _) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var mount = Mount();
            box.Respond(path => path.EndsWith("/slewing", StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error("no reading") : mount(path));

            Assert.That(await svc.ReadSlewingNowAsync(CancellationToken.None), Is.Null);
        }

        // A polar-align move renews its lease four times a second: the snap warning belongs to the driver call only.
        [Test]
        public async Task A_snapped_rate_is_logged_once_per_driver_call_not_per_renewal() {
            var log = new WarningCounter();
            var (box, svc, clock) = await ArmedAsync(logger: log);
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(0, 9.0, 1000, CancellationToken.None); // the bands top out at 4°/s
            for (var i = 0; i < 3; i++) {
                clock.Now = clock.Now.AddMilliseconds(250);
                await svc.MoveAxisAsync(0, 9.0, 1000, CancellationToken.None);
            }

            Assert.That(log.Snapped, Is.EqualTo(1));
        }

        // Stop Mount cancels a routine's lease; the routine's next renewal would reach the driver as a new move and
        // start the motor again. A renewal tagged with the abort count it began under is refused instead.
        [Test]
        public async Task A_renewal_from_before_Stop_Mount_does_not_restart_the_motor() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var since = svc.AbortEpoch;
            await svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None);

            await svc.AbortSlewAsync(CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(250);

            Assert.That(svc.AbortEpoch, Is.Not.EqualTo(since));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None));
            Assert.That(MoveAxisPuts(box).Count(b => !IsStop(b, 0)), Is.EqualTo(1), "only the first start reached the driver");
            await svc.MoveAxisAsync(0, 0, null, since, CancellationToken.None); // a stop is always allowed
            await svc.MoveAxisAsync(0, 1.0, 1000, svc.AbortEpoch, CancellationToken.None); // a new move after the abort is fine
            Assert.That(MoveAxisPuts(box).Count(b => !IsStop(b, 0)), Is.EqualTo(2));
        }

        // #2066 — Emergency Stop's first step counts the stop before any rung, with no command to the mount and no lease
        // cancelled. A routine's renewal at the same speed would only extend its lease: it is refused all the same, so the
        // lease runs out and stops the axis.
        [Test]
        public async Task A_renewal_after_Emergency_Stops_latch_is_refused_though_its_lease_still_runs() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var since = svc.AbortEpoch;
            await svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None);

            svc.FenceMotion("emergency_stop");
            clock.Now = clock.Now.AddMilliseconds(250);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None));
            Assert.That(svc.AbortReasonSince(since, except: "polar_align"), Is.EqualTo("emergency_stop"));
            Assert.That(box.Puts.Any(p => p.Path.EndsWith("/abortslew", StringComparison.Ordinal)), Is.False, "the latch sends nothing");
        }

        // #2107 — the refused renewal says what stopped the mount (only Polar Align reads it, in its failure): a park, Emergency
        // Stop's latch or the direction pad is no longer called "Stop Mount".
        [TestCase(MountStopReasons.Park, "(a park)")]
        [TestCase(MountStopReasons.EmergencyStop, "(Emergency Stop)")]
        [TestCase(MountStopReasons.PadStop, "(the direction pad)")]
        [TestCase(MountStopReasons.FaultReaction, "(the equipment-fault reaction)")]
        [TestCase(MountStopReasons.UserRequest, "(Stop Mount)")]
        public async Task A_refused_renewal_names_the_stop_that_came_since_it_began(string by, string named) {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var since = svc.AbortEpoch;
            await svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None);

            svc.FenceMotion(by);
            clock.Now = clock.Now.AddMilliseconds(250);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None));
            Assert.That(refused!.Message, Does.Contain(named));
        }

        // #2066 — a REST Park or Find Home ends a routine that drives the motors: it counts as a stop, named for what raised it.
        [TestCase("park")]
        [TestCase("findhome")]
        public async Task A_park_or_find_home_counts_as_a_stop_for_a_routines_renewal(string what) {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            var since = svc.AbortEpoch;
            await svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None);

            await (what == "park"
                ? svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None)
                : svc.FindHomeAsync(null, CancellationToken.None));
            clock.Now = clock.Now.AddMilliseconds(250);

            Assert.That(svc.AbortEpoch, Is.Not.EqualTo(since));
            Assert.That(svc.AbortReasonSince(since, except: "polar_align"), Is.EqualTo(what));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None));
        }

        // #2066 — a Park or Find Home counts as a stop only when no stop came since its request read the count: a Stop Mount or
        // Emergency Stop's latch that landed while the request checked the pose keeps the request's reading, so the park or
        // homing is still dropped before it goes out. Counting over it would hide that stop and send the move after it.
        [TestCase("park")]
        [TestCase("findhome")]
        public void A_stop_since_the_request_keeps_its_reading_and_is_not_counted_over(string what) {
            using var svc = new TelescopeService();
            var request = svc.AbortEpoch;
            svc.FenceMotion("emergency_stop");

            Assert.That(svc.BumpAbortEpochUnlessAbortedSince(request, what), Is.EqualTo(request), "the request keeps its own reading");
            Assert.That(svc.AbortEpoch, Is.EqualTo(request + 1), "no count of its own over the stop");
            Assert.That(svc.AbortReasonSince(request, except: null), Is.EqualTo("emergency_stop"));

            var quiet = svc.AbortEpoch;
            Assert.That(svc.BumpAbortEpochUnlessAbortedSince(quiet, what), Is.EqualTo(quiet + 1), "no stop since: it counts as one");
            Assert.That(svc.AbortReasonSince(quiet, except: null), Is.EqualTo(what));
        }

        // #2066 — a Find Home ends the leases, as a Park does: a lease left to run out would send MoveAxis 0 in the middle
        // of the homing.
        [Test]
        public async Task A_find_home_ends_the_leases_so_no_lease_stop_lands_on_the_homing() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None); // a pad press, leased

            await svc.FindHomeAsync(null, CancellationToken.None);
            var stopsBefore = MoveAxisPuts(box).Count(b => IsStop(b, 0));
            clock.Now = clock.Now.AddSeconds(5);
            svc.CheckLeases();

            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 0)), Is.EqualTo(stopsBefore), "no lease-expiry stop after the Find Home");
        }

        // The race the check before the driver call cannot see: Stop Mount lands while the move's own PUT is in flight,
        // so the move may reach the mount after the AbortSlew. The service stops the axis again and refuses the move.
        [Test]
        public async Task A_move_that_races_Stop_Mount_is_stopped_again_after_the_driver_call() {
            TelescopeService? service = null;
            Task? abort = null;
            var raced = 0;
            var (box, svc, _) = await ArmedAsync(path => {
                if (path.EndsWith("/moveaxis", StringComparison.Ordinal) && Interlocked.Exchange(ref raced, 1) == 0 && service is { } s) {
                    var before = s.AbortEpoch;
                    abort = Task.Run(() => s.AbortSlewAsync(CancellationToken.None)); // the user taps Stop Mount now
                    var sw = Stopwatch.StartNew();
                    while (s.AbortEpoch == before && sw.Elapsed < TimeSpan.FromSeconds(5)) {
                        Thread.Sleep(5);
                    }
                }
                return null;
            });
            await using var _ = box;
            using var __ = svc;
            service = svc;
            var since = svc.AbortEpoch;

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.MoveAxisAsync(0, 1.0, 1000, since, CancellationToken.None));
            if (abort is not null) {
                await abort;
            }

            Assert.That(raced, Is.EqualTo(1), "the abort raced the move's PUT");
            Assert.That(MoveAxisPuts(box).Last(), Does.Contain("Axis=0").And.Contain("Rate=0"), "the axis is stopped after the racing move");
            Assert.That(svc.Lease.Any, Is.False, "no lease is left to renew");
        }

        [Test]
        public async Task A_leased_move_with_no_renewal_is_stopped_after_the_lease() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(999);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.Zero, "still inside the lease");

            clock.Now = clock.Now.AddMilliseconds(2);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(1), "the daemon stops the axis itself");
        }

        [Test]
        public async Task A_renewal_at_the_same_rate_only_extends_the_lease_and_never_reaches_the_driver() {
            // On the rig (EQMOD through AlpacaBridge, 2026-11-11) a MoveAxis repeated every 300 ms
            // came back "Motion superseded before dispatch" (500): the pad stopped after a second.
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(300);
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            clock.Now = clock.Now.AddMilliseconds(300);
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(1), "one move reaches the driver; renewals stay in the daemon");

            clock.Now = clock.Now.AddMilliseconds(900);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 0)), Is.Zero, "the last renewal extended the lease");

            await svc.MoveAxisAsync(0, 2.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(2), "a new speed is a new move");
        }

        [Test]
        public async Task A_move_the_driver_superseded_with_a_newer_one_is_not_a_failure() {
            // AlpacaBridge queues motion: a press let go at once has its start replaced by the stop
            // before it reaches the mount, and answers the start with this DriverException (rig,
            // 2026-11-11: every short press showed "Couldn't move the mount").
            var superseded = 1;
            var (box, svc, _) = await ArmedAsync(path => path.EndsWith("/moveaxis", StringComparison.Ordinal)
                && Interlocked.Exchange(ref superseded, 0) == 1
                ? ScriptedAlpacaDevice.Error("Motion superseded before dispatch") : null);
            await using var _ = box;
            using var __ = svc;

            await Assert.DoesNotThrowAsync(() => svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None));
            Assert.That(svc.Lease.Any, Is.False, "the newer command decides; no lease for the replaced start");
            await svc.MoveAxisAsync(0, 1.0, 1000, CancellationToken.None);
            Assert.That(MoveAxisPuts(box), Has.Length.EqualTo(2), "the next press still reaches the driver");
        }

        [Test]
        public async Task A_request_without_a_lease_never_auto_stops_and_a_stop_cancels_the_lease() {
            var (box, svc, clock) = await ArmedAsync();
            await using var _ = box;
            using var __ = svc;

            await svc.MoveAxisAsync(0, 1.0, null, CancellationToken.None);
            clock.Now = clock.Now.AddMinutes(1);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 0)), Is.Zero, "the Full UI's pad is unchanged");

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            await svc.MoveAxisAsync(1, 0, null, CancellationToken.None);
            var stops = MoveAxisPuts(box).Count(b => IsStop(b, 1));
            clock.Now = clock.Now.AddSeconds(5);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(stops), "a rate-0 release cancels the deadline");

            await svc.MoveAxisAsync(1, 1.0, 1000, CancellationToken.None);
            await svc.AbortSlewAsync(CancellationToken.None);
            stops = MoveAxisPuts(box).Count(b => IsStop(b, 1));
            clock.Now = clock.Now.AddSeconds(5);
            svc.CheckLeases();
            Assert.That(MoveAxisPuts(box).Count(b => IsStop(b, 1)), Is.EqualTo(stops), "AbortSlew cancels the deadline");
        }
    }
}
