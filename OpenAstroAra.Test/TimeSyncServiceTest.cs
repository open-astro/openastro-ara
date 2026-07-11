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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
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
            var svc = new TimeSyncService(NullLogger<TimeSyncService>.Instance, setter ?? new FakeClockSetter(), profiles) {
                GpsDeviceProbe = () => false,
            };
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
        public async Task A_gps_sync_with_an_out_of_range_position_drops_the_location_but_applies_the_time() {
            // Unlike a wire push (422), the internal GPS path must never fail on a garbled
            // position — the time is still trustworthy even when the coordinates aren't.
            var store = new InMemoryProfileStore();
            var before = store.GetSiteSettings();
            var svc = NewService(profiles: store);

            var result = await svc.ApplyGpsSyncAsync(T0, new TimeSyncLocationDto(91, 0, 0), CancellationToken.None);

            Assert.That(result.LocationUpdated, Is.False);
            Assert.That(store.GetSiteSettings(), Is.EqualTo(before), "a bad position must not poison the profile");
            var state = await svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.Synced, Is.True, "the time sync still applies");
            Assert.That(state.Source, Is.EqualTo("gps-internal"));
            Assert.That(state.Location, Is.Null);
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
    }
}
