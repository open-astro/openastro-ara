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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2071 — while the mount's clock is not verified, a GoTo, a Sync, a Center or a polar alignment is
    /// refused before anything reaches the mount (<c>mount-clock-not-verified</c>, 409, ADR 0022), and a
    /// sequencer slew fails its step. Park and Home are never gated: iOptron parks and homes by encoder,
    /// and a safety, fault or shutdown park must not wait for a clock.
    /// </summary>
    [TestFixture]
    public class MountClockRefusalTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), failure);
                await Task.Delay(10);
            }
        }

        /// <summary>A connected mount whose rig clock was never synced: the connect's check closes the gate.</summary>
        private static async Task<(TelescopeService Svc, FakeTelescopeDevice Device)> UnverifiedAsync(IProfileService? profile = null) {
            var device = new FakeTelescopeDevice();
            var time = new TimeSyncService(NullLogger<TimeSyncService>.Instance, Mock.Of<ISystemClockSetter>());
            var svc = new TelescopeService(profileService: profile, timeSyncResolver: () => time) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => device,
                RunSitePush = push => push(),
                MountClockPushWait = TimeSpan.Zero,
            };
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.MountClockMotionRefusal()?.Contains("not been synced", StringComparison.Ordinal) == true,
                "the connect's clock check never ran");
            return (svc, device);
        }

        private static Coordinates Target(double raHours) => new(Angle.ByHours(raHours), Angle.ByDegree(30), Epoch.JNOW);

        [Test]
        public async Task A_REST_GoTo_is_refused_and_nothing_reaches_the_mount() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.SlewAsync(new SlewRequestDto(5, 30), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            Assert.That(device.Slews, Is.Zero);
        }

        /// <summary>A mount clock whose sidereal time follows its own clock at longitude 0.</summary>
        private sealed class FollowingMountClock : TelescopeService.IMountClock {
            private readonly Lock gate = new();
            private DateTime clock = DateTime.UtcNow.AddHours(-7);
            public DateTime UtcDate { get { lock (gate) { return clock; } } }
            public void SetUtcDate(DateTime utc) { lock (gate) { clock = utc; } }
            public double SiderealTime => SiteAstrometry.LocalSiderealTimeDeg(new DateTimeOffset(UtcDate), 0) / 15.0;
            public double Latitude => 0;
            public double Longitude => 0;
            public volatile bool IsSlewing;
            public bool Slewing => IsSlewing;
        }

        /// <summary>#2087 review — a connected mount whose rig clock has just been synced, with the mount-clock
        /// push that sync started held in <see cref="Pushes"/> until the test runs it. Every connect gets a new
        /// <see cref="FakeTelescopeDevice"/>; the mount's clock is one <see cref="FollowingMountClock"/>.</summary>
        private sealed class HeldPush : IDisposable {
            public readonly System.Collections.Concurrent.ConcurrentQueue<Func<Task>> Pushes = new();
            public required System.Collections.Generic.List<FakeTelescopeDevice> Devices { get; init; }
            public required TimeSyncService Time { get; init; }
            public required TelescopeService Svc { get; init; }
            public required FollowingMountClock Clock { get; init; }
            public FakeTelescopeDevice Device { get { lock (Devices) { return Devices[^1]; } } }

            /// <summary>Runs the oldest held push.</summary>
            public async Task ReleaseAsync() {
                Assert.That(Pushes.TryDequeue(out var push), Is.True, "a clock push is held");
                await push!();
            }

            /// <summary>Holds every push from now on (the connect's own, inline until then, has run).</summary>
            public void HoldPushes() => Svc.RunSitePush = push => { Pushes.Enqueue(push); return Task.CompletedTask; };

            public void Dispose() {
                Svc.Dispose();
                lock (Devices) {
                    foreach (var d in Devices) {
                        d.Dispose();
                    }
                }
            }
        }

        private static async Task<HeldPush> HeldPushAsync(IProfileService? profile = null, Action<FakeTelescopeDevice>? configure = null) {
            var time = new TimeSyncService(NullLogger<TimeSyncService>.Instance, Mock.Of<ISystemClockSetter>());
            var clock = new FollowingMountClock();
            var devices = new System.Collections.Generic.List<FakeTelescopeDevice>();
            var svc = new TelescopeService(profileService: profile, timeSyncResolver: () => time) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                SlewSettleMaxPolls = 30,
                DeviceFactory = _ => {
                    var d = new FakeTelescopeDevice();
                    configure?.Invoke(d);
                    lock (devices) { devices.Add(d); }
                    return d;
                },
                MountClockSource = () => clock,
                RunSitePush = push => push(),
                MountClockPushWait = TimeSpan.FromSeconds(10),
            };
            var rig = new HeldPush { Time = time, Svc = svc, Devices = devices, Clock = clock };
            svc.FollowTimeSync(time);
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.MountClockMotionRefusal()?.Contains("not been synced", StringComparison.Ordinal) == true,
                "the connect's clock check never ran");
            rig.HoldPushes();
            await time.PushAsync(new TimeSyncPushRequestDto("client", DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.That(rig.Pushes, Has.Count.EqualTo(1), "the sync started a mount-clock push");
            return rig;
        }

        /// <summary>A reconnect while <paramref name="waiting"/> waits for the held push: the new session's
        /// connect push is held too, then both run, the old session's first.</summary>
        private static async Task ReconnectThenReleaseAsync(HeldPush rig, Task waiting) {
            await rig.Svc.DisconnectAsync(null, CancellationToken.None);
            await rig.Svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => rig.Pushes.Count == 2, "the reconnect never started its clock push");
            Assert.That(waiting.IsCompleted, Is.False, "still waiting for the clock");
            await rig.ReleaseAsync(); // the old session's: its verdict is dropped
            await rig.ReleaseAsync(); // the new session's: opens the gate
        }

        [Test]
        public async Task A_REST_Sync_sent_while_the_mount_clock_is_being_set_waits_for_it_and_goes_through() {
            // #2087 review: right after the app connects, its time sync is still setting the mount's clock; a
            // REST GoTo or Sync then was refused although the push was about to open the gate. It waits now,
            // as a sequencer GoTo does (MountClockPushWait).
            using var rig = await HeldPushAsync();
            var sync = rig.Svc.SlewAsync(new SlewRequestDto(5, 30, Sync: true), null, CancellationToken.None);
            await Task.Delay(100);
            Assert.That(sync.IsCompleted, Is.False, "the Sync waits for the push in flight instead of a refusal");

            await rig.ReleaseAsync();
            await sync;
            await WaitForAsync(() => rig.Device.Syncs == 1, "the Sync never reached the mount after the clock was verified");
        }

        [Test]
        public async Task A_sequencer_GoTo_sent_while_the_mount_clock_is_being_set_waits_for_it_and_reaches_the_mount() {
            // #2087 review: a §42.3 reconnect just before a sequencer slew starts a clock push; the slew must wait
            // for it, not fail the step.
            using var rig = await HeldPushAsync();
            var slew = ((ITelescopeMediator)rig.Svc).SlewToCoordinatesAsync(Target(5), CancellationToken.None);
            await Task.Delay(100);
            Assert.That(slew.IsCompleted, Is.False, "the slew waits for the push in flight instead of failing the step");
            Assert.That(rig.Device.Slews, Is.Zero);

            await rig.ReleaseAsync();
            Assert.That(await slew, Is.True);
            Assert.That(rig.Device.Slews, Is.EqualTo(1), "the goto reached the mount once the clock was verified");
        }

        [Test]
        public async Task A_meridian_flip_sent_while_the_mount_clock_is_being_set_waits_for_it_and_reaches_the_mount() {
            // #2087 review: a time sync landing at the flip must not abort it (a failed flip parks).
            using var rig = await HeldPushAsync();
            var flip = ((ITelescopeMediator)rig.Svc).MeridianFlip(Target(5), CancellationToken.None);
            await Task.Delay(100);
            Assert.That(flip.IsCompleted, Is.False, "the flip waits for the push in flight instead of failing");
            Assert.That(rig.Device.Slews, Is.Zero);

            await rig.ReleaseAsync();
            Assert.That(await flip, Is.True);
            Assert.That(rig.Device.Slews, Is.EqualTo(1), "the flip goto reached the mount once the clock was verified");
        }

        [Test]
        public async Task A_REST_GoTo_waiting_for_the_mount_clock_goes_to_the_reconnected_mount_not_the_old_one() {
            // #2087 review: the GoTo kept the device read before the wait; after a reconnect in that window it
            // went to the old, disposed one.
            using var rig = await HeldPushAsync();
            var old = rig.Device;
            var goTo = rig.Svc.SlewAsync(new SlewRequestDto(5, 30), null, CancellationToken.None);

            await ReconnectThenReleaseAsync(rig, goTo);
            await goTo;
            var current = rig.Device;
            Assert.That(current, Is.Not.SameAs(old), "precondition: a new device");
            await WaitForAsync(() => current.Slews == 1, "the GoTo never reached the reconnected mount");
            Assert.That(old.Slews, Is.Zero, "nothing goes to the disposed device");
        }

        [Test]
        public async Task A_sequencer_GoTo_waiting_for_the_mount_clock_goes_to_the_reconnected_mount_not_the_old_one() {
            // #2087 review: the sequencer slew read the device before the clock wait; after a reconnect in that
            // window it must re-read it and send the goto to the new device.
            using var rig = await HeldPushAsync();
            var old = rig.Device;
            var slew = ((ITelescopeMediator)rig.Svc).SlewToCoordinatesAsync(Target(5), CancellationToken.None);

            await ReconnectThenReleaseAsync(rig, slew);
            Assert.That(await slew, Is.True);
            var current = rig.Device;
            Assert.That(current, Is.Not.SameAs(old), "precondition: a new device");
            Assert.That((old.Slews, current.Slews), Is.EqualTo((0, 1)), "the goto went to the reconnected mount only");
        }

        [Test]
        public async Task A_sequencer_GoTo_waiting_for_the_mount_clock_is_not_sent_when_the_reconnected_mount_is_parked() {
            // #2087 review: the parked check before the wait saw the old mount; the reconnected one is parked, so
            // the slew fails cleanly and nothing is sent.
            var connects = 0;
            using var rig = await HeldPushAsync(configure: d => {
                if (Interlocked.Increment(ref connects) > 1) {
                    d.ReportsArrival = true;
                    d.Park(); // the reconnected mount reports AtPark
                }
            });
            var old = rig.Device;
            var slew = ((ITelescopeMediator)rig.Svc).SlewToCoordinatesAsync(Target(5), CancellationToken.None);

            await rig.Svc.DisconnectAsync(null, CancellationToken.None);
            await rig.Svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => rig.Pushes.Count == 2, "the reconnect never started its clock push");
            await WaitForAsync(() => rig.Svc.GetInfo().AtPark, "the reconnected mount never read as parked");
            Assert.That(slew.IsCompleted, Is.False, "still waiting for the clock");
            await rig.ReleaseAsync();
            await rig.ReleaseAsync();

            Assert.That(await slew, Is.False, "a parked mount is not slewed");
            var current = rig.Device;
            Assert.That(current, Is.Not.SameAs(old), "precondition: a new device");
            Assert.That((old.Slews, current.Slews), Is.EqualTo((0, 0)), "nothing reaches either mount");
        }

        [Test]
        public async Task A_REST_GoTo_that_times_out_while_the_clock_push_waits_out_a_slew_says_so() {
            // #2087 review: the push waits up to ~3 min for a slew to end before it writes the mount's clock; a
            // GoTo that gives up meanwhile names the slew instead of "setting and checking the clock".
            using var rig = await HeldPushAsync();
            rig.Svc.MountClockPushWait = TimeSpan.FromMilliseconds(300);
            rig.Svc.SitePushWait = _ => Task.Delay(10);

            var held = await Assert.ThrowsAsync<RefusalException>(() => rig.Svc.SlewAsync(new SlewRequestDto(5, 30), null, CancellationToken.None));
            Assert.That(held!.Message, Does.Not.Contain("slewing"), "a push not in its slew wait keeps its own text");

            rig.Clock.IsSlewing = true;
            var pushing = rig.ReleaseAsync();
            var waiting = await Assert.ThrowsAsync<RefusalException>(() => rig.Svc.SlewAsync(new SlewRequestDto(5, 30), null, CancellationToken.None));
            Assert.That(waiting!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            Assert.That(waiting.Message, Is.EqualTo(TelescopeService.SlewWaitRefusal));
            Assert.That(rig.Device.Slews, Is.Zero);

            rig.Clock.IsSlewing = false;
            await pushing;
            Assert.That(rig.Svc.MountClockMotionRefusal(), Is.Null, "the push set and checked the clock once the slew ended");
        }

        [Test]
        public async Task A_meridian_flip_waiting_for_the_mount_clock_sends_the_pier_side_hint_to_the_reconnected_mount() {
            // #2087 review: the flip kept the device read before the wait, so after a reconnect the pier-side
            // hint (some mounts start the flip on it) went to the old, disposed device.
            using var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = 0;
            profile.MeridianFlipSettings.UseSideOfPier = true;
            profile.TelescopeSettings.SettleTime = 0;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            // A target the hint moves to the west side (the mount reads east), so the hint is written.
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0;
            var ra = OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(Target((lst + 3) % 24), Angle.ByHours(lst)) == PierSide.pierWest
                ? (lst + 3) % 24 : (lst + 21) % 24;
            // Each mount reports the target, so the flip goto settles.
            using var rig = await HeldPushAsync(profileService.Object, d => { d.CanSetPierSide = true; d.Ra = ra; });
            var old = rig.Device;
            var flip = ((ITelescopeMediator)rig.Svc).MeridianFlip(Target(ra), CancellationToken.None);

            await ReconnectThenReleaseAsync(rig, flip);
            Assert.That(await flip, Is.True);
            var current = rig.Device;
            Assert.That(current, Is.Not.SameAs(old), "precondition: a new device");
            Assert.That(current.PierSideWrites, Is.EqualTo(1), "the hint went to the reconnected mount");
            Assert.That(old.PierSideWrites, Is.Zero, "nothing goes to the disposed device");
            Assert.That((old.Slews, current.Slews), Is.EqualTo((0, 1)));
        }

        [Test]
        public async Task A_REST_Sync_sent_just_after_a_rig_clock_step_waits_for_the_recheck_and_goes_through() {
            // #2087 review: the verified-but-stepped branch. The check passed, then the rig clock stepped; until
            // the next 2 s refresh sets the mount's clock again the gate is closed. A Sync in that window waits
            // for the recheck instead of a refusal. No refresh ticks here: the test runs the 2 s hook itself.
            var step = TimeSpan.Zero;
            var time = new TimeSyncService(NullLogger<TimeSyncService>.Instance, Mock.Of<ISystemClockSetter>());
            var clock = new FollowingMountClock();
            using var device = new FakeTelescopeDevice();
            using var svc = new TelescopeService(timeSyncResolver: () => time) {
                RefreshPeriod = TimeSpan.FromHours(1),
                DeviceFactory = _ => device,
                MountClockSource = () => clock,
                RunSitePush = push => push(),
                UtcNow = () => DateTimeOffset.UtcNow + step,
                MountClockPushWait = TimeSpan.FromSeconds(10),
            };
            svc.FollowTimeSync(time);
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.MountClockMotionRefusal()?.Contains("not been synced", StringComparison.Ordinal) == true,
                "the connect's clock check never ran");
            await time.PushAsync(new TimeSyncPushRequestDto("client", DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.That(svc.MountClockMotionRefusal(), Is.Null, "precondition: the sync set and verified the mount's clock");

            step = TimeSpan.FromMinutes(1); // the wall clock jumps; the monotonic clock does not
            Assert.That(svc.MountClockMotionRefusal(), Does.Contain("stepped"));
            var sync = svc.SlewAsync(new SlewRequestDto(5, 30, Sync: true), null, CancellationToken.None);
            await Task.Delay(100);
            Assert.That(sync.IsCompleted, Is.False, "the Sync waits for the recheck instead of a refusal");
            Assert.That(device.Syncs, Is.Zero);

            svc.WatchMountClock(); // the 2 s refresh: sets the mount's clock again and verifies it
            await sync;
            await WaitForAsync(() => device.Syncs == 1, "the Sync never reached the mount after the recheck");
        }

        [Test]
        public async Task A_REST_Sync_is_refused_and_nothing_reaches_the_mount() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.SlewAsync(new SlewRequestDto(5, 30, Sync: true), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            await Task.Delay(50);
            Assert.That(device.Syncs, Is.Zero, "a sync on a wrong clock would bake the error into the pointing");
        }

        [Test]
        public async Task REST_park_a_safety_park_and_Home_go_ahead_with_the_mount_clock_unverified() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            // One at a time: since #2066 a park or a Find Home accepted while another is still on its way to
            // the mount counts as a stop, and the earlier one is dropped.
            await svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None);
            await WaitForAsync(() => device.Parks == 1, "a REST park waited for the clock");
            await svc.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None);
            await WaitForAsync(() => device.Parks == 2, "a safety park waited for the clock");
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => device.Homes == 1, "a Find Home waited for the clock");
        }

        [Test]
        public async Task A_sequencer_park_and_home_go_ahead_with_the_mount_clock_unverified() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            device.ReportsArrival = true;
            Assert.That(await svc.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None), Is.True);
            Assert.That(await svc.FindHome(new Progress<ApplicationStatus>(), CancellationToken.None), Is.True);
            Assert.That((device.Parks, device.Homes), Is.EqualTo((1, 1)));
        }

        [Test]
        public async Task A_sequencer_GoTo_fails_its_step_and_sends_nothing() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            var failed = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).SlewToCoordinatesAsync(Target(5), CancellationToken.None));
            Assert.That(failed!.Message, Does.Contain("Mount clock not verified"));
            Assert.That(device.Slews, Is.Zero);
        }

        [Test]
        public async Task A_sequencer_Sync_is_a_clean_not_synced() {
            var (svc, device) = await UnverifiedAsync();
            using var _ = svc;
            Assert.That(await ((ITelescopeMediator)svc).Sync(Target(5)), Is.False);
            Assert.That(device.Syncs, Is.Zero);
        }

        [Test]
        public async Task A_meridian_flip_is_refused_before_the_pier_side_hint() {
            using var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = 0;
            profile.MeridianFlipSettings.UseSideOfPier = true;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var (svc, device) = await UnverifiedAsync(profileService.Object);
            using var _ = svc;
            device.CanSetPierSide = true;
            // A target the hint would move to the west side (the mount reads east): without the gate first,
            // the SideOfPier write goes out, and some mounts start the flip slew on it.
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0;
            var ra = OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(Target((lst + 3) % 24), Angle.ByHours(lst)) == PierSide.pierWest
                ? (lst + 3) % 24 : (lst + 21) % 24;
            Assert.That(OpenAstroAra.Astrometry.MeridianFlip.ExpectedPierSide(Target(ra), Angle.ByHours(lst)), Is.EqualTo(PierSide.pierWest), "precondition");

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => ((ITelescopeMediator)svc).MeridianFlip(Target(ra), CancellationToken.None));
            Assert.That(device.PierSideWrites, Is.Zero);
            Assert.That(device.Slews, Is.Zero);
        }

        [Test]
        public async Task A_reconnect_hours_after_the_sync_sets_and_checks_the_mount_clock_again() {
            var now = new DateTimeOffset(2026, 11, 19, 15, 54, 55, TimeSpan.Zero);
            var wall = now;
            long tick = 1_000;
            var time = new TimeSyncService(NullLogger<TimeSyncService>.Instance, Mock.Of<ISystemClockSetter>()) {
                Now = () => wall,
                TickMs = () => tick,
            };
            using var device = new FakeTelescopeDevice { UTCDate = now.UtcDateTime.AddHours(-7) };
            using var svc = new TelescopeService(timeSyncResolver: () => time) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => device,
                RunSitePush = push => push(),
                UtcNow = () => wall,
                TickMs = () => tick,
            };
            await time.PushAsync(new TimeSyncPushRequestDto("client", now), CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            // FakeTelescopeDevice reports LST 0 h at longitude 0: verified only at the instant LST is 0,
            // so check the write rather than the verdict here.
            await WaitForAsync(() => device.UTCDate == now.UtcDateTime, "the connect did not set the mount's clock");

            await svc.DisconnectAsync(null, CancellationToken.None);
            await WaitForAsync(() => svc.MountClockMotionRefusal()?.Contains("not been checked", StringComparison.Ordinal) == true,
                "a new session forgets the last check");

            tick += (long)TimeSpan.FromHours(9).TotalMilliseconds; // a §42.3 reconnect late in the night
            wall += TimeSpan.FromHours(9);
            device.UTCDate = now.UtcDateTime; // the mount lost nine hours while it was away
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => device.UTCDate == wall.UtcDateTime, "the reconnect did not set the mount's clock from the trusted time");
        }

        [Test]
        public async Task The_status_refresh_sets_the_mount_clock_once_NTP_synchronises_the_rig_clock() {
            // Nothing calls WatchMountClock here: only the connected mount's status refresh can notice that
            // the kernel clock became trusted (NTP raises no event) and set the mount's clock.
            var now = new DateTimeOffset(2026, 11, 19, 15, 54, 55, TimeSpan.Zero);
            var wall = now;
            long tick = 1_000;
            var kernelSynced = false;
            var kernel = new Mock<IKernelClockSyncProbe>();
            kernel.Setup(k => k.IsSynchronized()).Returns(() => Volatile.Read(ref kernelSynced));
            var time = new TimeSyncService(NullLogger<TimeSyncService>.Instance, Mock.Of<ISystemClockSetter>(), kernelClock: kernel.Object) {
                Now = () => wall,
                TickMs = () => Interlocked.Read(ref tick),
            };
            var stale = now.UtcDateTime.AddHours(-7);
            using var device = new FakeTelescopeDevice { UTCDate = stale };
            using var svc = new TelescopeService(timeSyncResolver: () => time) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                DeviceFactory = _ => device,
                RunSitePush = push => push(),
                UtcNow = () => wall,
                TickMs = () => Interlocked.Read(ref tick),
            };
            await svc.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => svc.MountClockMotionRefusal()?.Contains("not been synced", StringComparison.Ordinal) == true,
                "the connect's clock check never ran");
            Assert.That(device.UTCDate, Is.EqualTo(stale), "no trusted time yet: nothing is written");

            // NTP disciplines the rig clock; 31 s later (wall and monotonic together, so no clock step).
            wall = now + TimeSpan.FromSeconds(31);
            Volatile.Write(ref kernelSynced, true);
            Interlocked.Add(ref tick, 31_000);
            await WaitForAsync(() => device.UTCDate == wall.UtcDateTime, "the status refresh never set the mount's clock");
        }

        // ─── Center and polar alignment ───

        private static Mock<IMountClockGate> ClosedGate() {
            var gate = new Mock<IMountClockGate>();
            const string refusal = "Mount clock not verified: the rig clock has not been synced from the app or a GPS.";
            gate.Setup(g => g.MountClockMotionRefusal()).Returns(refusal);
            gate.Setup(g => g.MountClockRefusalAfterPushAsync(It.IsAny<CancellationToken>())).ReturnsAsync(refusal);
            return gate;
        }

        [Test]
        public async Task Center_is_refused_before_a_job_is_enqueued() {
            var jobs = new Mock<IBatchJobService>(MockBehavior.Strict); // any Enqueue = test failure
            var profile = new Mock<IProfileService>();
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0),
                Mock.Of<ICenteringService>(), jobs.Object, profile.Object, ClosedGate().Object));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
        }

        [Test]
        public async Task A_Center_sent_while_the_mount_clock_is_being_set_waits_for_it_and_is_enqueued() {
            // #2087 review: a Center right after the app's time sync got a 409 a GoTo would not get. It waits
            // like the REST GoTo now; with no push in flight it is still refused at once, before Enqueue.
            var profile = new Mock<IProfileService>();
            var (closed, _) = await UnverifiedAsync();
            using (closed) {
                closed.MountClockPushWait = TimeSpan.FromSeconds(10);
                var started = System.Diagnostics.Stopwatch.StartNew();
                var refusal = await Assert.ThrowsAsync<RefusalException>(() => PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0),
                    Mock.Of<ICenteringService>(), new Mock<IBatchJobService>(MockBehavior.Strict).Object, profile.Object, closed));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
                Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "no push in flight: refused at once");
            }

            using var rig = await HeldPushAsync();
            var jobs = new InMemoryBatchJobService(null);
            var center = PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0), Mock.Of<ICenteringService>(), jobs, profile.Object, rig.Svc);
            await Task.Delay(100);
            Assert.That(center.IsCompleted, Is.False, "the Center waits for the push in flight instead of a 409");

            await rig.ReleaseAsync();
            Assert.That(await center, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted<BatchJobDto>>(),
                "enqueued once the clock was verified");
        }

        [Test]
        public async Task The_polar_align_start_endpoint_answers_the_refusal_with_its_cause() {
            var pa = new Mock<IPolarAlignService>();
            pa.Setup(p => p.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RefusalException(RefusalCatalog.MountClockNotVerified, "Mount clock not verified."));
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.PolarAlignStartAsync(pa.Object, null, null, CancellationToken.None),
                "RefusalHandler answers it with reason mount-clock-not-verified, not the guider's generic 409");
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
        }
    }
}
