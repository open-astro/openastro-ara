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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §31 — the time-sync state machine: the fresh-and-trusted gate, trust clamping per source,
    /// offset computation with and without a working clock setter, and the location write-through
    /// into the profile site settings.
    /// </summary>
    [TestFixture]
    public class TimeSyncServiceTest {

        private sealed class FakeClockSetter : ISystemClockSetter {
            public bool Succeeds { get; set; } = true;
            public DateTimeOffset? LastSet { get; private set; }
            public bool TrySet(DateTimeOffset utc) {
                if (!Succeeds) {
                    return false;
                }
                LastSet = utc;
                return true;
            }
        }

        private static readonly DateTimeOffset T0 = new(2026, 7, 11, 6, 0, 0, TimeSpan.Zero);

        private static TimeSyncService NewService(FakeClockSetter? setter = null, IProfileStore? profiles = null,
                DateTimeOffset? now = null) {
            var svc = new TimeSyncService(NullLogger<TimeSyncService>.Instance, setter ?? new FakeClockSetter(), profiles);
            svc.Now = () => now ?? T0;
            svc.TickMs = () => 0;
            return svc;
        }

        [Test]
        public async Task Fresh_boot_reports_unsynced_with_no_source() {
            var state = await NewService().GetStateAsync(CancellationToken.None);
            Assert.That(state.Synced, Is.False);
            Assert.That(state.Source, Is.EqualTo("none"));
            Assert.That(state.Trust, Is.EqualTo("none"));
            Assert.That(state.SyncedAtUtc, Is.Null);
        }

        [Test]
        public async Task The_state_carries_the_daemons_utc_offset() {
            // Simple Mode writes a scheduled start in the daemon's wall clock (WaitForTime compares with
            // DateTime.Now), so the client needs the daemon's offset, not its own.
            var state = await NewService().GetStateAsync(CancellationToken.None);
            Assert.That(state.UtcOffsetMinutes, Is.EqualTo((int)TimeZoneInfo.Local.GetUtcOffset(T0).TotalMinutes));
        }

        [Test]
        public async Task A_client_push_sets_the_clock_and_flips_synced() {
            var setter = new FakeClockSetter();
            var svc = NewService(setter);
            var pushed = T0.AddSeconds(-42.5); // the client says we're 42.5 s fast

            var result = await svc.PushAsync(new TimeSyncPushRequestDto("client", pushed), CancellationToken.None);

            Assert.That(result.ClockSet, Is.True);
            Assert.That(setter.LastSet, Is.EqualTo(pushed));
            Assert.That(result.Before.OffsetSeconds, Is.EqualTo(-42.5).Within(0.001));
            Assert.That(result.After.OffsetSeconds, Is.EqualTo(0).Within(0.001));

            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Synced, Is.True, "a fresh medium-trust sync satisfies the §31.1 gate");
            Assert.That(state.Source, Is.EqualTo("client"));
            Assert.That(state.Trust, Is.EqualTo("medium"));
            Assert.That(state.SystemTimeOffsetSeconds, Is.EqualTo(0));
        }

        [Test]
        public async Task A_failed_clock_set_tracks_the_offset_honestly() {
            var svc = NewService(new FakeClockSetter { Succeeds = false });
            var pushed = T0.AddSeconds(30);

            var result = await svc.PushAsync(new TimeSyncPushRequestDto("client", pushed), CancellationToken.None);

            Assert.That(result.ClockSet, Is.False);
            Assert.That(result.After.OffsetSeconds, Is.EqualTo(30).Within(0.001),
                "the offset stays outstanding when the set fails");
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.SystemTimeOffsetSeconds, Is.EqualTo(30).Within(0.001));
            Assert.That(state.Synced, Is.True,
                "the sync is still known (offset tracked) — synced reflects knowledge, not the OS clock");
        }

        [Test]
        public async Task Trust_is_clamped_to_the_sources_ceiling() {
            var svc = NewService();
            // A manual entry claiming high trust stays low (§31.2).
            await svc.PushAsync(new TimeSyncPushRequestDto("manual", T0, Trust: "high"), CancellationToken.None);
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Trust, Is.EqualTo("low"));
            Assert.That(state.Source, Is.EqualTo("manual"));
            Assert.That(state.Synced, Is.False, "low trust never satisfies the §31.1 gate");

            // A client push may DOWNGRADE itself below its ceiling.
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0, Trust: "low"), CancellationToken.None);
            state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Trust, Is.EqualTo("low"));
        }

        [Test]
        public async Task Gps_mobile_maps_to_the_gps_external_state_source() {
            var svc = NewService();
            await svc.PushAsync(new TimeSyncPushRequestDto("gps-mobile", T0), CancellationToken.None);
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Source, Is.EqualTo("gps-external"));
            Assert.That(state.Trust, Is.EqualTo("medium"));
        }

        [Test]
        public async Task Gps_client_is_an_external_gps_at_high_trust() {
            // A USB dongle on the client computer relays the receiver's own UTC + fix — trusted like
            // the Pi-side dongle, unlike gps-mobile (phone clock timestamps, medium).
            var svc = NewService();
            await svc.PushAsync(new TimeSyncPushRequestDto("gps-client", T0, new TimeSyncLocationDto(38.85, -77.04, 120), Trust: "high"), CancellationToken.None);
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(state.Source, Is.EqualTo("gps-external"));
                Assert.That(state.Trust, Is.EqualTo("high"));
                Assert.That(state.Synced, Is.True);
                Assert.That(state.Location?.Lat, Is.EqualTo(38.85));
            });
        }

        [Test]
        public void An_unknown_source_throws_the_422_exception() {
            var svc = NewService();
            Assert.That(async () => await svc.PushAsync(new TimeSyncPushRequestDto("ntp", T0), CancellationToken.None),
                Throws.InstanceOf<TimeSyncInvalidSourceException>());
        }

        [Test]
        public async Task A_sync_goes_stale_after_an_hour_of_monotonic_time() {
            var svc = NewService();
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);

            svc.TickMs = () => 59L * 60 * 1000;
            Assert.That((await svc.GetStateAsync(CancellationToken.None)).Synced, Is.True);

            svc.TickMs = () => 61L * 60 * 1000;
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Synced, Is.False, "> 1 h old → the waterfall re-runs");
            Assert.That(state.Source, Is.EqualTo("client"), "the stale source is still reported for detail");
        }

        [Test]
        public async Task Freshness_survives_a_wildly_wrong_system_clock_when_the_set_fails() {
            // #832 r1 — the degrade path this feature exists for: an RTC-less Pi boots near epoch,
            // the clock-set fails (no CAP_SYS_TIME), and freshness must still be measured by
            // ELAPSED time, not by subtracting the bogus system clock from the corrected instant.
            var systemNow = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // 26 years behind
            var svc = NewService(new FakeClockSetter { Succeeds = false }, now: systemNow);
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);

            svc.TickMs = () => 30L * 60 * 1000; // 30 real minutes later
            Assert.That((await svc.GetStateAsync(CancellationToken.None)).Synced, Is.True,
                "30 real minutes after the push the sync is still fresh, whatever the raw clock says");

            svc.TickMs = () => 2L * 60 * 60 * 1000; // 2 real hours later
            Assert.That((await svc.GetStateAsync(CancellationToken.None)).Synced, Is.False,
                "2 real hours later it is stale — a clock 26 years behind must not read as fresh forever");
        }

        [Test]
        public void An_implausible_time_is_refused_before_touching_the_clock() {
            var setter = new FakeClockSetter();
            var svc = NewService(setter);
            // A request body missing time_utc deserializes to default(DateTimeOffset) — year 1.
            Assert.That(async () => await svc.PushAsync(new TimeSyncPushRequestDto("client", default), CancellationToken.None),
                Throws.InstanceOf<TimeSyncInvalidRequestException>());
            Assert.That(async () => await svc.PushAsync(
                    new TimeSyncPushRequestDto("client", new DateTimeOffset(2150, 1, 1, 0, 0, 0, TimeSpan.Zero)), CancellationToken.None),
                Throws.InstanceOf<TimeSyncInvalidRequestException>());
            Assert.That(setter.LastSet, Is.Null, "the clock setter is never reached with an implausible time");
        }

        [Test]
        public void An_out_of_range_location_is_refused() {
            var svc = NewService();
            Assert.That(async () => await svc.PushAsync(
                    new TimeSyncPushRequestDto("client", T0, new TimeSyncLocationDto(91, 0, 0)), CancellationToken.None),
                Throws.InstanceOf<TimeSyncInvalidRequestException>());
            Assert.That(async () => await svc.PushAsync(
                    new TimeSyncPushRequestDto("client", T0, new TimeSyncLocationDto(0, -181, 0)), CancellationToken.None),
                Throws.InstanceOf<TimeSyncInvalidRequestException>());
        }

        [Test]
        public async Task A_pushed_location_lands_in_the_profile_site_settings() {
            var store = new InMemoryProfileStore();
            var svc = NewService(profiles: store);
            var loc = new TimeSyncLocationDto(Lat: 30.27, Lng: -97.74, Alt: 165.0);

            var result = await svc.PushAsync(new TimeSyncPushRequestDto("client", T0, loc), CancellationToken.None);

            Assert.That(result.LocationUpdated, Is.True);
            var site = store.GetSiteSettings();
            Assert.That(site.LatitudeDeg, Is.EqualTo(30.27));
            Assert.That(site.LongitudeDeg, Is.EqualTo(-97.74));
            Assert.That(site.ElevationM, Is.EqualTo(165.0));
            // Non-location site fields survive the write (a `with` update, not a rebuild).
            Assert.That(site.TimeZone, Is.Not.Empty);

            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Location, Is.EqualTo(loc));
        }

        [Test]
        public async Task A_full_precision_gps_fix_is_normalized_to_two_decimals_everywhere() {
            // A raw NMEA fix carries 8+ decimals of GPS noise; the profile (and the GET state
            // clients echo into their fill affordances) must store ~1 km site precision (xx.xx).
            var store = new InMemoryProfileStore();
            var svc = NewService(profiles: store);
            var raw = new TimeSyncLocationDto(Lat: 34.67111883333333, Lng: -106.7861325, Alt: 1480.1);

            await svc.PushAsync(new TimeSyncPushRequestDto("gps-client", T0, raw, Trust: "high"), CancellationToken.None);

            var site = store.GetSiteSettings();
            Assert.That(site.LatitudeDeg, Is.EqualTo(34.67));
            Assert.That(site.LongitudeDeg, Is.EqualTo(-106.79));
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Location!.Lat, Is.EqualTo(34.67), "state and profile must agree");
            Assert.That(state.Location!.Lng, Is.EqualTo(-106.79));
        }

        [Test]
        public async Task A_push_without_location_reports_location_not_updated() {
            var store = new InMemoryProfileStore();
            var before = store.GetSiteSettings();
            var svc = NewService(profiles: store);

            var result = await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);

            Assert.That(result.LocationUpdated, Is.False);
            Assert.That(store.GetSiteSettings(), Is.EqualTo(before), "no location push → the site section is untouched");
        }

        // ─── #2071: the trusted time the mount's clock is set from ───

        [TestCase("client")]
        [TestCase("gps-client")]
        [TestCase("gps-mobile")]
        public async Task A_sync_of_medium_or_high_trust_raises_TrustedTimeApplied(string source) {
            var svc = NewService();
            var raised = 0;
            svc.TrustedTimeApplied += (_, _) => raised++;
            await svc.PushAsync(new TimeSyncPushRequestDto(source, T0), CancellationToken.None);
            Assert.That(raised, Is.EqualTo(1));
            Assert.That(svc.TryGetTrustedUtc(out var utc), Is.True);
            Assert.That(utc, Is.EqualTo(T0));
        }

        [Test]
        public async Task A_manual_sync_is_never_trusted_time_for_the_mount() {
            var svc = NewService();
            var raised = 0;
            svc.TrustedTimeApplied += (_, _) => raised++;
            await svc.PushAsync(new TimeSyncPushRequestDto("manual", T0), CancellationToken.None);
            Assert.That(raised, Is.Zero, "a typed-in time never reaches the mount");
            Assert.That(svc.TryGetTrustedUtc(out _), Is.False);
        }

        [Test]
        public async Task Trusted_time_runs_on_the_monotonic_clock_when_the_OS_clock_could_not_be_set() {
            var wall = T0.AddHours(-6.8); // the Pi's stale boot clock, which the set fails to correct
            long tick = 1_000;
            var svc = new TimeSyncService(NullLogger<TimeSyncService>.Instance, new FakeClockSetter { Succeeds = false }) {
                Now = () => wall,
                TickMs = () => tick,
            };
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);
            tick += 90_000;
            wall = T0.AddMinutes(1); // something else (NTP, the bridge) stepped the OS clock since
            Assert.That(svc.TryGetTrustedUtc(out var utc), Is.True);
            Assert.That(utc, Is.EqualTo(T0.AddSeconds(90)), "the pushed time plus monotonic elapsed, never the wall clock plus a stale offset");
        }

        [Test]
        public async Task Trusted_time_does_not_expire_after_the_freshness_hour() {
            long tick = 0;
            var svc = new TimeSyncService(NullLogger<TimeSyncService>.Instance, new FakeClockSetter()) {
                Now = () => T0,
                TickMs = () => tick,
            };
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);
            tick = (long)TimeSpan.FromHours(9).TotalMilliseconds; // a §42.3 reconnect late in the night
            Assert.That((await svc.GetStateAsync(CancellationToken.None)).Synced, Is.False, "the §31.1 UI gate is still the hour");
            Assert.That(svc.TryGetTrustedUtc(out var utc), Is.True, "the mount's clock can still be set and checked");
            Assert.That(utc, Is.EqualTo(T0.AddHours(9)));
        }

        // ─── #2071: the kernel's NTP sync is a trusted time source too ───

        private sealed class FakeKernelClock : IKernelClockSyncProbe {
            public bool Synced { get; set; }
            public bool Throws { get; set; }
            public bool IsSynchronized() => Throws ? throw new InvalidOperationException("adjtimex failed") : Synced;
        }

        private static TimeSyncService KernelService(FakeKernelClock kernel, Func<DateTimeOffset> now, Func<long>? tick = null) =>
            new(NullLogger<TimeSyncService>.Instance, new FakeClockSetter { Succeeds = false }, kernelClock: kernel) {
                Now = now,
                TickMs = tick ?? (() => 0),
            };

        [Test]
        public void A_kernel_synchronised_clock_is_trusted_without_a_push() {
            var svc = KernelService(new FakeKernelClock { Synced = true }, () => T0);
            Assert.That(svc.TryGetTrustedUtc(out var utc), Is.True, "after a daemon restart on a rig with working NTP");
            Assert.That(utc, Is.EqualTo(T0), "the OS clock itself");
        }

        [Test]
        public void An_unsynchronised_kernel_clock_is_never_trusted() {
            var svc = KernelService(new FakeKernelClock { Synced = false }, () => T0);
            Assert.That(svc.TryGetTrustedUtc(out _), Is.False);
        }

        [Test]
        public void A_kernel_probe_that_throws_is_not_trusted() {
            var svc = KernelService(new FakeKernelClock { Synced = true, Throws = true }, () => T0);
            Assert.That(svc.TryGetTrustedUtc(out _), Is.False);
        }

        [Test]
        public async Task A_kernel_that_loses_the_sync_falls_back_to_the_client_push() {
            var kernel = new FakeKernelClock { Synced = true };
            var wall = T0.AddMinutes(1); // the OS clock NTP keeps, a minute after the client's push
            long tick = 0;
            var svc = KernelService(kernel, () => wall, () => tick);
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);
            tick = 10_000;
            Assert.That(svc.TryGetTrustedUtc(out var synced), Is.True);
            Assert.That(synced, Is.EqualTo(wall), "while the kernel is synchronised its clock is the time");

            kernel.Synced = false;
            Assert.That(svc.TryGetTrustedUtc(out var pushed), Is.True);
            Assert.That(pushed, Is.EqualTo(T0.AddSeconds(10)), "the client push, carried forward on the monotonic clock");
        }

        // ─── #2087 review: a client sync carried for over a day with no NTP is logged once a day ───

        private sealed class WarningLog : ILogger<TimeSyncService> {
            public List<string> Warnings { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel == LogLevel.Warning) {
                    lock (Warnings) { Warnings.Add(formatter(state, exception)); }
                }
            }
        }

        private static long Hours(double h) => (long)TimeSpan.FromHours(h).TotalMilliseconds;

        [Test]
        public async Task A_client_sync_over_a_day_old_with_no_NTP_is_logged_once_a_day() {
            var log = new WarningLog();
            long tick = 0;
            var svc = new TimeSyncService(log, new FakeClockSetter(), kernelClock: new FakeKernelClock { Synced = false }) {
                Now = () => T0,
                TickMs = () => tick,
            };
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);
            tick = Hours(23);
            Assert.That(svc.TryGetTrustedUtc(out _), Is.True);
            Assert.That(log.Warnings, Is.Empty, "under a day old");

            tick = Hours(25);
            Assert.That(svc.TryGetTrustedUtc(out var utc), Is.True, "still trusted: a log, no expiry");
            Assert.That(utc, Is.EqualTo(T0.AddHours(25)));
            Assert.That(log.Warnings, Has.Count.EqualTo(1));
            Assert.That(log.Warnings[0], Does.Contain("last synced from the app 25 h ago and NTP is not running"));

            tick = Hours(48);
            svc.TryGetTrustedUtc(out _);
            Assert.That(log.Warnings, Has.Count.EqualTo(1), "once a day, not on every read");

            tick = Hours(49);
            svc.TryGetTrustedUtc(out _);
            Assert.That(log.Warnings, Has.Count.EqualTo(2));
            Assert.That(log.Warnings[1], Does.Contain("49 h ago"));
        }

        [Test]
        public async Task A_day_old_client_sync_is_not_logged_while_NTP_keeps_the_clock() {
            var log = new WarningLog();
            long tick = 0;
            var svc = new TimeSyncService(log, new FakeClockSetter(), kernelClock: new FakeKernelClock { Synced = true }) {
                Now = () => T0,
                TickMs = () => tick,
            };
            await svc.PushAsync(new TimeSyncPushRequestDto("client", T0), CancellationToken.None);
            tick = Hours(30);
            Assert.That(svc.TryGetTrustedUtc(out _), Is.True);
            Assert.That(log.Warnings, Is.Empty, "NTP keeps the clock: the push's age does not matter");
        }

        [TestCase(0, 0x2001, 50_000L, true)] // TIME_OK, PLL + NANO, 50 ms bound: chrony or timesyncd keeping it
        [TestCase(0, 0x0041, 50_000L, false)] // STA_UNSYNC set (a clock_settime sets it)
        [TestCase(5, 0x2001, 50_000L, false)] // TIME_ERROR
        [TestCase(-1, 0x2001, 50_000L, false)] // the call failed
        [TestCase(0, 0x2001, 16_000_000L, false)] // the bound grew to the kernel's 16 s limit: nothing disciplines it
        [TestCase(0, 0x2001, 2_000_000L, true)] // at the 2 s limit
        [TestCase(0, 0x2001, 2_000_001L, false)]
        public void The_adjtimex_rule_needs_the_sync_flag_clear_and_a_small_error_bound(int state, int status, long maxErrorUs, bool trusted) {
            Assert.That(LinuxKernelClockSyncProbe.Evaluate(state, status, maxErrorUs), Is.EqualTo(trusted));
        }

        [Test]
        public void The_adjtimex_read_matches_the_kernels_struct_layout() {
            Assume.That(OperatingSystem.IsLinux() && Environment.Is64BitProcess && BitConverter.IsLittleEndian, "64-bit little-endian Linux only");
            var read = LinuxKernelClockSyncProbe.Read();
            Assert.That(read, Is.Not.Null);
            var t = read!.Value;
            Assert.Multiple(() => {
                Assert.That(t.State, Is.InRange(0, 5), "a read-only adjtimex needs no privilege");
                Assert.That(t.Tolerance, Is.EqualTo(500L << 16), "the kernel's fixed 500 ppm (MAXFREQ) at the tolerance offset");
                Assert.That(t.MaxErrorMicroseconds, Is.InRange(0L, 16_000_000L), "maxerror at its offset, within the kernel's 16 s cap");
            });
        }
    }
}
