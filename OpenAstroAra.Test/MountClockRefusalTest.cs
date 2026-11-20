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
            await svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None);
            await svc.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None);
            await svc.FindHomeAsync(null, CancellationToken.None);
            await WaitForAsync(() => device.Parks == 2 && device.Homes == 1, "a park or a home waited for the clock");
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
            gate.Setup(g => g.MountClockMotionRefusal()).Returns("Mount clock not verified: the rig clock has not been synced from the app or a GPS.");
            return gate;
        }

        [Test]
        public void Center_is_refused_before_a_job_is_enqueued() {
            var jobs = new Mock<IBatchJobService>(MockBehavior.Strict); // any Enqueue = test failure
            var profile = new Mock<IProfileService>();
            var refusal = Assert.Throws<RefusalException>(() => PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0),
                Mock.Of<ICenteringService>(), jobs.Object, profile.Object, ClosedGate().Object));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
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
