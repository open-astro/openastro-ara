#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§31.1 step 2 — the USB-GPS worker's probe pass against a fake serial source:
    /// a valid RMC applies a gps-internal/high sync (with position), noise is skipped, a dead
    /// device falls through to the next, and no devices means no sync.</summary>
    [TestFixture]
    public class UsbGpsTimeSyncWorkerTest {

        private sealed class FakeClockSetter : ISystemClockSetter {
            public DateTimeOffset? LastSet { get; private set; }
            public bool TrySet(DateTimeOffset utc) {
                LastSet = utc;
                return true;
            }
        }

        private sealed class FakeSerialSource : ISerialNmeaSource {
            public Dictionary<string, List<string>> LinesByDevice { get; } = new();
            public List<string> DevicesRead { get; } = new();
            public HashSet<string> ThrowOnOpen { get; } = new();

            public IReadOnlyList<string> EnumerateDevices() => [.. LinesByDevice.Keys];

            public async IAsyncEnumerable<string> ReadLinesAsync(string devicePath,
                    [EnumeratorCancellation] CancellationToken ct) {
                DevicesRead.Add(devicePath);
                if (ThrowOnOpen.Contains(devicePath)) {
                    throw new UnauthorizedAccessException("port busy");
                }
                foreach (var line in LinesByDevice[devicePath]) {
                    ct.ThrowIfCancellationRequested();
                    yield return line;
                    await Task.Yield();
                }
                // The fake's line list ran out — hold until the listen window cancels, like a
                // real port that simply goes quiet.
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        private static string WithChecksum(string bodyWithDollar) {
            byte sum = 0;
            foreach (var c in bodyWithDollar[1..]) {
                sum ^= (byte)c;
            }
            return $"{bodyWithDollar}*{sum:X2}";
        }

        private static readonly string ValidRmc =
            WithChecksum("$GPRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110726,,,A");
        private static readonly string[] BothUsbDevices = ["/dev/ttyUSB0", "/dev/ttyUSB1"];

        private static (UsbGpsTimeSyncWorker Worker, TimeSyncService TimeSync, FakeClockSetter Setter) NewWorker(FakeSerialSource source) {
            var setter = new FakeClockSetter();
            var timeSync = new TimeSyncService(NullLogger<TimeSyncService>.Instance, setter) {
                GpsDeviceProbe = () => true,
            };
            using var worker = new UsbGpsTimeSyncWorker(timeSync, NullLogger<UsbGpsTimeSyncWorker>.Instance, source) {
                PerDeviceListenWindow = TimeSpan.FromMilliseconds(500),
            };
            return (worker, timeSync, setter);
        }

        [Test]
        public async Task A_valid_rmc_fix_applies_a_gps_internal_high_sync_with_position() {
            var source = new FakeSerialSource();
            // A GGA before the RMC — the real per-second interleave — supplies the altitude the
            // RMC lacks (#834 r1: the fix's instant comes from RMC, its altitude from GGA).
            source.LinesByDevice["/dev/ttyUSB0"] = [
                "$GPGSV,noise*00",
                "garbage",
                WithChecksum("$GPGGA,061529.00,3016.20,N,09744.40,W,1,08,1.0,165.0,M,0.0,M,,"),
                ValidRmc,
            ];
            var (worker, timeSync, setter) = NewWorker(source);

            var applied = await worker.ProbeOnceAsync(CancellationToken.None);

            Assert.That(applied, Is.True);
            Assert.That(setter.LastSet, Is.EqualTo(new DateTimeOffset(2026, 7, 11, 6, 15, 30, TimeSpan.Zero)));
            var state = await timeSync.GetStateAsync(CancellationToken.None);
            Assert.That(state.Source, Is.EqualTo("gps-internal"));
            Assert.That(state.Trust, Is.EqualTo("high"));
            Assert.That(state.Synced, Is.True);
            Assert.That(state.Location, Is.Not.Null);
            Assert.That(state.Location!.Lat, Is.EqualTo(30.27).Within(1e-6));
            Assert.That(state.Location.Lng, Is.EqualTo(-97.74).Within(1e-6));
            Assert.That(state.Location.Alt, Is.EqualTo(165.0), "the GGA altitude pairs with the RMC fix");
        }

        [Test]
        public async Task An_rmc_only_fix_leaves_the_profile_elevation_untouched() {
            // #834 r1 — RMC carries no altitude; a GPS sync without a GGA in the window must NOT
            // zero the site elevation the twilight/airmass math depends on.
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(store.GetSiteSettings() with { ElevationM = 1234.0 });
            var setter = new FakeClockSetter();
            var timeSync = new TimeSyncService(NullLogger<TimeSyncService>.Instance, setter, store) {
                GpsDeviceProbe = () => true,
            };
            var source = new FakeSerialSource();
            source.LinesByDevice["/dev/ttyUSB0"] = [ValidRmc];
            using var worker = new UsbGpsTimeSyncWorker(timeSync, NullLogger<UsbGpsTimeSyncWorker>.Instance, source) {
                PerDeviceListenWindow = TimeSpan.FromMilliseconds(500),
            };

            var applied = await worker.ProbeOnceAsync(CancellationToken.None);

            Assert.That(applied, Is.True);
            var site = store.GetSiteSettings();
            Assert.That(site.LatitudeDeg, Is.EqualTo(30.27).Within(1e-6), "the position still lands");
            Assert.That(site.ElevationM, Is.EqualTo(1234.0), "unknown altitude preserves the existing elevation");
        }

        [Test]
        public async Task A_dead_device_is_skipped_and_the_next_one_supplies_the_fix() {
            var source = new FakeSerialSource();
            source.LinesByDevice["/dev/ttyUSB0"] = [];
            source.ThrowOnOpen.Add("/dev/ttyUSB0");
            source.LinesByDevice["/dev/ttyUSB1"] = [ValidRmc];
            var (worker, timeSync, _) = NewWorker(source);

            var applied = await worker.ProbeOnceAsync(CancellationToken.None);

            Assert.That(applied, Is.True, "the busy port must not abort the pass");
            Assert.That(source.DevicesRead, Is.EqualTo(BothUsbDevices));
            Assert.That((await timeSync.GetStateAsync(CancellationToken.None)).Source, Is.EqualTo("gps-internal"));
        }

        [Test]
        public async Task A_silent_port_times_out_via_the_listen_window_without_a_sync() {
            var source = new FakeSerialSource();
            source.LinesByDevice["/dev/ttyUSB0"] = ["$GPGSV,only,noise*00"]; // never an RMC
            var (worker, timeSync, setter) = NewWorker(source);

            var applied = await worker.ProbeOnceAsync(CancellationToken.None);

            Assert.That(applied, Is.False);
            Assert.That(setter.LastSet, Is.Null);
            Assert.That((await timeSync.GetStateAsync(CancellationToken.None)).Source, Is.EqualTo("none"));
        }

        [Test]
        public async Task No_devices_means_no_sync_and_no_error() {
            var (worker, _, setter) = NewWorker(new FakeSerialSource());
            var applied = await worker.ProbeOnceAsync(CancellationToken.None);
            Assert.That(applied, Is.False);
            Assert.That(setter.LastSet, Is.Null);
        }

        [Test]
        public async Task An_rmc_with_an_implausible_year_is_rejected_not_applied() {
            // A cold receiver with a wrong almanac can emit garbage dates; the plausibility window
            // on ApplyGpsSyncAsync must refuse it. Year 2010 is a valid NMEA sentence but outside
            // the 2020 floor. The worker logs and moves on (per-device catch), applying nothing.
            var source = new FakeSerialSource();
            source.LinesByDevice["/dev/ttyUSB0"] =
                [WithChecksum("$GPRMC,061530.00,A,3016.20,N,09744.40,W,0.0,0.0,110710,,,A")];
            var (worker, timeSync, setter) = NewWorker(source);

            var applied = await worker.ProbeOnceAsync(CancellationToken.None);

            Assert.That(applied, Is.False);
            Assert.That(setter.LastSet, Is.Null);
            Assert.That((await timeSync.GetStateAsync(CancellationToken.None)).Source, Is.EqualTo("none"));
        }
    }
}
