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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2071 — the §31 time sync corrected the Pi's clock and never the mount's: on RC51 the mount kept
    /// the stale boot time, 6 h 48 min slow, and its sidereal time was about 102° off. After a trusted
    /// sync, and on every connect, the daemon writes the mount's UTCDate from the synced clock and checks
    /// the mount's UTCDate and SiderealTime against it; GoTo, Sync and polar alignment wait for that check.
    /// </summary>
    [TestFixture]
    public class MountClockTest {

        // RC51's night: the real time, and the time the mount kept from the Pi's stale boot clock.
        private static readonly DateTimeOffset Now = new(2026, 11, 19, 15, 54, 55, TimeSpan.Zero);
        private static readonly DateTimeOffset StaleMount = Now - new TimeSpan(6, 48, 58);
        private const double Lat = -36.85, Lng = 174.76;
        private static readonly string[] SiteThenClock = ["lat", "lng", "utc"]; // the elevation (120 m) is already there

        private static double Lst(DateTimeOffset at, double lng) => SiteAstrometry.LocalSiderealTimeDeg(at, lng) / 15.0;

        /// <summary>A mount whose sidereal time follows its own clock and longitude, as a real one does.</summary>
        private sealed class FakeMountClock : TelescopeService.IMountSite, TelescopeService.IMountClock {
            public readonly List<string> Writes = [];
            public DateTime Clock { get; set; }
            private double latitude = Lat, longitude = Lng;
            public double Latitude { get => Answer(latitude); set => latitude = value; }
            public double Longitude { get => Answer(longitude); set => longitude = value; }
            public double Elevation { get; set; } = 120;
            public int SlewingReads;
            public int SlewingFor;
            public bool RefuseWrite;
            /// <summary>The next this many UTCDate writes fail (a transient bridge error).</summary>
            public int WriteFailures;
            /// <summary>Like AlpacaBridge: UTCDate reads "not established" until a write has landed.</summary>
            public bool UtcUnknownUntilWritten;
            public bool UtcNotImplemented;
            /// <summary>Runs after each clock or site reading is taken: a slow bridge's round trip.</summary>
            public Action? AfterRead;
            private T Answer<T>(T value) {
                AfterRead?.Invoke();
                return value;
            }
            public bool Slewing => ++SlewingReads <= SlewingFor;
            public DateTime UtcDate => UtcNotImplemented ? throw new ASCOM.PropertyNotImplementedException("UTCDate", false)
                : UtcUnknownUntilWritten ? throw new ASCOM.InvalidOperationException("UTCDate not established") : Answer(Clock);
            public double SiderealTime => Answer(Lst(new DateTimeOffset(Clock), longitude));
            public void SetUtcDate(DateTime utc) {
                if (RefuseWrite) {
                    throw new ASCOM.InvalidOperationException("the driver keeps its own clock");
                }
                if (WriteFailures > 0) {
                    WriteFailures--;
                    throw new ASCOM.DriverException("bridge timeout");
                }
                UtcUnknownUntilWritten = false;
                Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc), "the wire carries no offset: only UTC goes out");
                Writes.Add("utc");
                Clock = utc;
            }
            public void SetLatitude(double v) { Writes.Add("lat"); Latitude = v; }
            public void SetLongitude(double v) { Writes.Add("lng"); Longitude = v; }
            public void SetElevation(double v) { Writes.Add("elev"); Elevation = v; }
        }

        private sealed class CapturingLogger : ILogger<TelescopeService> {
            public List<string> Lines { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (Lines) { Lines.Add(formatter(state, exception)); }
            }
        }

        private static SiteSettingsDto Site(double lat, double lng) =>
            new("Home", lat, lng, 120, "Pacific/Auckland", false, 0, 4, 2, "astronomical");

        /// <summary>A mount behind the seams, pushes run inline, the real time-sync service on a manual
        /// wall and monotonic clock.</summary>
        private sealed class Rig : IDisposable {
            public DateTimeOffset Wall = Now;
            public long Tick = 7_000_000;
            public readonly FakeMountClock Mount = new() { Clock = StaleMount.UtcDateTime };
            public readonly TimeSyncService Time;
            public readonly TelescopeService Svc;
            public readonly CapturingLogger Log = new();
            public int Waits;

            /// <summary>#2071 — whether the kernel reports NTP sync (the probe behind the time-sync service).</summary>
            public bool KernelSynced;

            public Rig(SiteSettingsDto? site = null) {
                var kernel = new Mock<IKernelClockSyncProbe>();
                kernel.Setup(k => k.IsSynchronized()).Returns(() => KernelSynced);
                Time = new TimeSyncService(NullLogger<TimeSyncService>.Instance,
                    Mock.Of<ISystemClockSetter>(c => c.TrySet(It.IsAny<DateTimeOffset>()) == true), kernelClock: kernel.Object) {
                    Now = () => Wall,
                    TickMs = () => Tick,
                };
                var store = new Mock<IProfileStore>();
                store.Setup(s => s.GetSiteSettings()).Returns(site ?? Site(Lat, Lng));
                Svc = new TelescopeService(Log, timeSyncResolver: () => Time) {
                    MountClockSource = () => Mount,
                    MountSiteSource = () => Mount,
                    RunSitePush = push => push(),
                    SitePushWait = _ => { Waits++; return Task.CompletedTask; },
                    UtcNow = () => Wall,
                    TickMs = () => Tick,
                };
                Svc.FollowProfileSite(store.Object);
                Svc.FollowTimeSync(Time);
            }

            public Task<TimeSyncPushResultDto> SyncAsync(string source = "client") =>
                Time.PushAsync(new TimeSyncPushRequestDto(source, Wall), CancellationToken.None);

            public int UtcWrites => Mount.Writes.Count(w => w == "utc");

            public void Dispose() => Svc.Dispose();
        }

        // ─── the push ───

        [Test]
        public async Task A_slow_bridge_does_not_count_its_round_trips_as_mount_clock_error() {
            // #2087 review: the trusted time was taken after the UTCDate, SiderealTime and site reads, so on a
            // bridge with 2 s round trips a mount that is right was 8 s "off". Each reading now has its own.
            using var rig = new Rig();
            rig.Mount.AfterRead = () => {
                rig.Wall += TimeSpan.FromSeconds(2);
                rig.Tick += 2_000;
                rig.Mount.Clock += TimeSpan.FromSeconds(2);
            };
            await rig.SyncAsync();
            Assert.That(rig.UtcWrites, Is.EqualTo(1));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null, string.Join(" | ", rig.Log.Lines));
        }

        [Test]
        public async Task A_trusted_time_sync_sets_the_mount_clock_and_opens_the_gate() {
            using var rig = new Rig();
            rig.Svc.OnMountConnected(); // connected before the app pushed the time: nothing trusted yet
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("not been synced"));

            await rig.SyncAsync();

            Assert.That(rig.UtcWrites, Is.EqualTo(1));
            Assert.That(rig.Mount.Clock, Is.EqualTo(Now.UtcDateTime));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null, "the mount's clock is set and checked");
            var line = rig.Log.Lines.Single(l => l.StartsWith("Mount clock (time sync)", StringComparison.Ordinal));
            Assert.That(line, Does.Contain(StaleMount.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), "the mount's UTC before");
            Assert.That(line, Does.Contain(Now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), "the pushed UTC and the mount's after");
        }

        [Test]
        public async Task A_manual_time_sync_never_writes_the_mount_clock() {
            using var rig = new Rig();
            rig.Svc.OnMountConnected();
            await rig.SyncAsync("manual");
            Assert.That(rig.UtcWrites, Is.Zero, "a typed-in time never reaches the mount");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("not been synced"));
        }

        [Test]
        public async Task A_connect_writes_the_site_first_then_the_clock() {
            using var rig = new Rig(Site(-37.2, 175.0));
            await rig.SyncAsync();
            rig.Mount.Writes.Clear(); // the sync's own write, made before the connect
            rig.Mount.Clock = StaleMount.UtcDateTime;
            rig.Svc.OnMountConnected();
            Assert.That(rig.Mount.Writes, Is.EqualTo(SiteThenClock),
                "the sidereal time is checked against the longitude just written");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void A_connect_with_no_synced_rig_clock_writes_no_time_and_keeps_the_gate_closed() {
            using var rig = new Rig();
            rig.Svc.OnMountConnected();
            Assert.That(rig.UtcWrites, Is.Zero, "an unverified host clock never goes to the mount");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("not been synced"));
        }

        [Test]
        public async Task A_refused_UTCDate_write_still_verifies_a_mount_whose_clock_already_agrees() {
            using var rig = new Rig();
            rig.Mount.RefuseWrite = true;
            rig.Mount.Clock = Now.UtcDateTime.AddSeconds(1); // a mount that keeps its own (right) clock
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public async Task A_refused_UTCDate_write_on_a_wrong_mount_clock_keeps_the_gate_closed() {
            using var rig = new Rig();
            rig.Mount.RefuseWrite = true;
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("6 h 48 min"));
        }

        [Test]
        public async Task A_driver_that_cannot_report_UTCDate_is_verified_by_its_sidereal_time() {
            using var rig = new Rig();
            rig.Mount.UtcNotImplemented = true;
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public async Task The_clock_write_waits_out_a_slew() {
            using var rig = new Rig();
            rig.Mount.SlewingFor = 3;
            await rig.SyncAsync();
            Assert.That(rig.Waits, Is.EqualTo(3));
            Assert.That(rig.UtcWrites, Is.EqualTo(1));
        }

        [Test]
        public async Task A_host_clock_step_closes_the_gate_and_sets_the_mount_clock_again() {
            using var rig = new Rig();
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);

            rig.Wall += TimeSpan.FromMinutes(1); // the wall clock jumps; the monotonic clock does not
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("stepped"));
            rig.Svc.WatchMountClock();

            Assert.That(rig.UtcWrites, Is.EqualTo(2), "set again from the trusted (monotonic) time");
            Assert.That(rig.Mount.Clock, Is.EqualTo(Now.UtcDateTime), "the step did not leak into the pushed time");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
            rig.Svc.WatchMountClock();
            Assert.That(rig.UtcWrites, Is.EqualTo(2), "one push per step, no loop");
        }

        [Test]
        public async Task An_unverified_mount_clock_is_checked_again_and_opens_the_gate_once_it_is_right() {
            using var rig = new Rig();
            rig.Mount.RefuseWrite = true;
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Not.Null);

            rig.Mount.Clock = Now.UtcDateTime; // the bridge (or another client) fixed the mount's clock
            rig.Svc.WatchMountClock();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Not.Null, "not before the recheck period");
            rig.Tick += 31_000;
            rig.Wall += TimeSpan.FromSeconds(31);
            rig.Mount.Clock = rig.Wall.UtcDateTime;
            rig.Svc.WatchMountClock();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public async Task A_manual_sync_during_the_slew_wait_writes_nothing_into_the_mount() {
            using var rig = new Rig();
            rig.Mount.SlewingFor = 2;
            rig.Svc.SitePushWait = _ => rig.SyncAsync("manual"); // a typed-in time lands while the push waits
            await rig.SyncAsync();
            Assert.That(rig.UtcWrites, Is.Zero, "the trust went away during the wait: no time (and never year 1) goes out");
            Assert.That(rig.Mount.Clock, Is.EqualTo(StaleMount.UtcDateTime));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("not been synced"));
        }

        [Test]
        public async Task A_site_push_that_throws_still_sets_and_checks_the_mount_clock_on_connect() {
            using var rig = new Rig();
            await rig.SyncAsync();
            rig.Mount.Writes.Clear();
            rig.Mount.Clock = StaleMount.UtcDateTime;
            var broken = new Mock<IProfileStore>();
            broken.Setup(s => s.GetSiteSettings()).Throws(new System.IO.IOException("profile unreadable"));
            rig.Svc.FollowProfileSite(broken.Object);
            rig.Svc.OnMountConnected();
            Assert.That(rig.UtcWrites, Is.EqualTo(1), "the clock push runs after a failed site push");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null, "checked against the mount's own longitude");
        }

        [Test]
        public async Task A_mount_clock_no_trusted_time_reached_is_written_on_the_recheck() {
            using var rig = new Rig();
            rig.Svc.OnMountConnected(); // no trusted time yet
            rig.Svc.MountClockSource = () => null; // the sync lands while the mount is between sessions
            await rig.SyncAsync();
            rig.Svc.MountClockSource = () => rig.Mount;
            Assert.That(rig.UtcWrites, Is.Zero);
            rig.Tick += 31_000;
            rig.Wall += TimeSpan.FromSeconds(31);
            rig.Svc.WatchMountClock();
            Assert.That(rig.UtcWrites, Is.EqualTo(1), "a recheck of a clock never written writes it");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void A_UTCDate_write_that_failed_once_at_connect_is_written_again_by_the_recheck() {
            // #2087 review — on an NTP-only rig with no app, the connect's write failed once and the mount then could not
            // report its clock: a read-only recheck kept the gate closed for the whole session.
            using var rig = new Rig { KernelSynced = true };
            rig.Mount.UtcUnknownUntilWritten = true;
            rig.Mount.WriteFailures = 1;
            rig.Svc.OnMountConnected();
            Assert.That(rig.UtcWrites, Is.Zero);
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("did not report its clock"));

            rig.Tick += 31_000;
            rig.Wall += TimeSpan.FromSeconds(31);
            rig.Svc.WatchMountClock();

            Assert.That(rig.UtcWrites, Is.EqualTo(1), "the recheck writes the trusted time again");
            Assert.That(rig.Mount.Clock, Is.EqualTo(rig.Wall.UtcDateTime));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null, string.Join(" | ", rig.Log.Lines));
        }

        [Test]
        public async Task A_GoTo_that_reads_the_gate_as_a_host_clock_step_is_recorded_waits_for_the_push() {
            // #2087 review — between the step's unverified record and the push being counted, the gate read "not
            // verified" with no push in flight, so a GoTo landing there was refused at once instead of waiting.
            using var rig = new Rig();
            await rig.SyncAsync();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
            Task<string?>? goTo = null;
            var completedAtOnce = false;
            // The push reads the mount's clock source right after the step is recorded, before it runs: a GoTo lands there.
            rig.Svc.MountClockSource = () => {
                if (goTo is null) {
                    goTo = rig.Svc.MountClockRefusalAfterPushAsync(CancellationToken.None);
                    completedAtOnce = goTo.IsCompleted;
                }
                return rig.Mount;
            };

            rig.Wall += TimeSpan.FromMinutes(1);
            rig.Svc.WatchMountClock();

            Assert.That(goTo, Is.Not.Null, "the GoTo landed inside the window");
            Assert.That(completedAtOnce, Is.False, "the GoTo waits for the push instead of being refused at once");
            Assert.That(await goTo!.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null, "the push set the clock: the GoTo goes");
        }

        // ─── the kernel's NTP sync (no client) ───

        [Test]
        public void After_a_daemon_restart_an_NTP_synced_rig_sets_the_mount_clock_on_connect_without_the_app() {
            using var rig = new Rig { KernelSynced = true };
            rig.Svc.OnMountConnected();
            Assert.That(rig.UtcWrites, Is.EqualTo(1), "the kernel's synchronised clock is trusted time");
            Assert.That(rig.Mount.Clock, Is.EqualTo(Now.UtcDateTime));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void An_unsynchronised_kernel_clock_never_reaches_the_mount() {
            using var rig = new Rig { KernelSynced = false };
            rig.Svc.OnMountConnected();
            Assert.That(rig.UtcWrites, Is.Zero);
            Assert.That(rig.Svc.MountClockMotionRefusal(), Does.Contain("not been synced"));
        }

        [Test]
        public void A_kernel_that_gains_NTP_sync_after_the_connect_sets_the_mount_clock_on_the_recheck() {
            using var rig = new Rig { KernelSynced = false };
            rig.Svc.OnMountConnected();
            rig.KernelSynced = true; // the network came up after boot
            rig.Tick += 31_000;
            rig.Wall += TimeSpan.FromSeconds(31);
            rig.Svc.WatchMountClock();
            Assert.That(rig.UtcWrites, Is.EqualTo(1));
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void An_NTP_clock_step_sets_the_mount_clock_again_from_the_stepped_clock() {
            using var rig = new Rig { KernelSynced = true };
            rig.Svc.OnMountConnected();
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);

            rig.Wall += TimeSpan.FromMinutes(1); // NTP steps the OS clock; the monotonic clock does not move
            rig.Svc.WatchMountClock();

            Assert.That(rig.UtcWrites, Is.EqualTo(2), "a host clock step re-checks");
            Assert.That(rig.Mount.Clock, Is.EqualTo(rig.Wall.UtcDateTime), "the kernel-synchronised clock is the truth after the step");
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void Without_a_time_sync_service_the_gate_is_off() {
            using var svc = new TelescopeService();
            Assert.That(svc.MountClockMotionRefusal(), Is.Null);
        }

        [Test]
        public void MountSiderealTimeHours_reads_the_mounts_own_sidereal_time() {
            // #1589 — the clearance watch's hour angle while the mount's clock is unverified: the mount's LST,
            // which follows its own clock (here 6 h 49 m behind the rig's), never the rig's.
            var mount = new FakeMountClock { Clock = StaleMount.UtcDateTime };
            using var svc = new TelescopeService { MountClockSource = () => mount };
            Assert.That(svc.MountSiderealTimeHours(), Is.EqualTo(Lst(StaleMount, Lng)).Within(1e-9));
            Assert.That(svc.MountSiderealTimeHours(), Is.Not.EqualTo(Lst(Now, Lng)).Within(0.1), "the mount's clock, not the rig's");
        }

        [Test]
        public void MountSiderealTimeHours_is_null_with_no_mount_clock() {
            using var svc = new TelescopeService { MountClockSource = () => null };
            Assert.That(svc.MountSiderealTimeHours(), Is.Null);
        }

        // ─── the check ───

        private static (bool Verified, string Reason) Check(DateTimeOffset? mountUtc, double? mountLst,
                double? mountLat = Lat, double? mountLng = Lng, bool notImplemented = false,
                double? siteLat = Lat, double? siteLng = Lng) =>
            TelescopeService.EvaluateMountClock(mountUtc, notImplemented, mountLst, mountLat, mountLng, Now, siteLat, siteLng);

        [Test]
        public void The_RC51_mount_clock_is_not_verified() {
            var (verified, reason) = Check(StaleMount, Lst(StaleMount, Lng));
            Assert.That(verified, Is.False);
            Assert.That(reason, Does.Contain("-6 h 48 min"));
        }

        [Test]
        public void A_mount_three_seconds_off_is_verified() {
            var at = Now.AddSeconds(3);
            Assert.That(Check(at, Lst(at, Lng)).Verified, Is.True);
        }

        [Test]
        public void A_mount_whose_sidereal_time_is_off_is_not_verified() {
            var (verified, reason) = Check(Now, Lst(Now.AddMinutes(-2), Lng));
            Assert.That(verified, Is.False);
            Assert.That(reason, Does.Contain("sidereal time"));
        }

        [Test]
        public void A_sidereal_time_across_midnight_is_verified() {
            // A longitude that puts the computed LST 2 s before 0 h; the mount reads 1 s after.
            var lng = SiteAstrometry.LocalSiderealTimeDeg(Now, 0) is var gmst ? (24.0 - 2.0 / 3600.0) * 15.0 - gmst : 0;
            lng -= 360.0 * Math.Round(lng / 360.0);
            Assert.That(Lst(Now, lng), Is.GreaterThan(23.99), "precondition: just before midnight");
            Assert.That(Check(Now, 1.0 / 3600.0, mountLng: lng, siteLng: lng).Verified, Is.True);
        }

        [Test]
        public void A_mount_longitude_off_the_profile_is_not_verified() {
            var (verified, reason) = Check(Now, Lst(Now, Lng), mountLng: Lng + 0.02);
            Assert.That(verified, Is.False);
            Assert.That(reason, Does.Contain("site"));
        }

        [Test]
        public void A_UTCDate_the_driver_cannot_report_leaves_the_sidereal_time_to_decide() {
            Assert.That(Check(null, Lst(Now, Lng), notImplemented: true).Verified, Is.True);
            Assert.That(Check(null, Lst(StaleMount, Lng), notImplemented: true).Verified, Is.False);
        }

        [Test]
        public void A_UTCDate_read_that_fails_is_not_verified() {
            // AlpacaBridge answers "UTCDate has not been established" when it does not know the mount's
            // clock, and may still compute a host-based sidereal time: that must not pass.
            var (verified, reason) = Check(null, Lst(Now, Lng));
            Assert.That(verified, Is.False);
            Assert.That(reason, Does.Contain("UTCDate"));
        }

        [Test]
        public void With_no_profile_site_the_sidereal_time_is_checked_against_the_mounts_longitude() {
            Assert.That(Check(Now, Lst(Now, Lng), siteLat: null, siteLng: null).Verified, Is.True);
            Assert.That(Check(Now, Lst(Now, 0), siteLat: null, siteLng: null).Verified, Is.False);
        }

        [Test]
        public void A_UTCDate_read_back_in_local_time_is_normalised_to_UTC() {
            var local = new DateTime(2026, 11, 19, 15, 54, 55, DateTimeKind.Utc).ToLocalTime();
            Assert.That(AlpacaTelescopeDevice.AsUtc(local), Is.EqualTo(new DateTime(2026, 11, 19, 15, 54, 55, DateTimeKind.Utc)));
            var unspecified = new DateTime(2026, 11, 19, 15, 54, 55, DateTimeKind.Unspecified);
            Assert.That(AlpacaTelescopeDevice.AsUtc(unspecified).Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(AlpacaTelescopeDevice.AsUtc(unspecified).Ticks, Is.EqualTo(unspecified.Ticks), "unspecified means UTC on the wire");
        }
    }
}
