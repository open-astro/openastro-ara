#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1238 — <c>TelescopeService.MeridianFlip</c> was a stub returning false, so the §58
    /// executor could never complete a flip. It is now the pier-side hint (when the profile uses
    /// side of pier and the driver can set it) followed by the ordinary goto to the same target,
    /// with the cache refreshed afterwards so §58.5's verification reads the post-flip side. Also
    /// pins the SideOfPier not-implemented latch from the #1239 review.</summary>
    [TestFixture]
    [Category("bench")] // loopback-only, runs in the default job too
    public class TelescopeMeridianFlipSlewTest {

        private const double Longitude = 0.0;

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // A JNOW mount (no precession natives) sitting ON the target already, so the goto's settle
        // (not slewing + pointing near) passes at once. sideOfPier/canSetPierSide are the knobs.
        private static Func<string, string?> Mount(double raHours, string sideOfPier, string canSetPierSide, string? destinationSideOfPier = null) => path =>
            path.EndsWith("/destinationsideofpier", StringComparison.Ordinal) ? destinationSideOfPier
            : path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? raHours.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "30.0"
            : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? sideOfPier
            : path.EndsWith("/cansetpierside", StringComparison.Ordinal) ? canSetPierSide
            : null;

        private static (OpenAstroAra.Profile.Profile Profile, IProfileService Service) ProfileWith(bool useSideOfPier) {
            var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = Longitude;
            profile.MeridianFlipSettings.UseSideOfPier = useSideOfPier;
            var svc = new Mock<IProfileService>();
            svc.SetupGet(p => p.ActiveProfile).Returns(profile);
            return (profile, svc.Object);
        }

        private static async Task<TelescopeService> ConnectAsync(ScriptedAlpacaDevice box, IProfileService profile) {
            var svc = new TelescopeService(profileService: profile);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                var info = ((ITelescopeMediator)svc).GetInfo();
                if (info.Connected && info.Coordinates is not null) {
                    return svc;
                }
                await Task.Delay(100);
            }
            Assert.Fail("mount never connected and reported its position");
            return svc;
        }

        private static double LstHours() => SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, Longitude) / 15.0;

        private static double FormValue(string body, string key) {
            foreach (var pair in body.Split('&')) {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase)) {
                    return double.Parse(Uri.UnescapeDataString(kv[1]), CultureInfo.InvariantCulture);
                }
            }
            throw new AssertionException($"'{key}' missing from PUT body '{body}'");
        }

        [Test]
        public async Task The_flip_is_a_goto_to_the_same_target_and_reports_success() {
            var ra = (LstHours() + 3.0) % 24.0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(ra, sideOfPier: "0", canSetPierSide: "true"));
            var (profile, profileService) = ProfileWith(useSideOfPier: false);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(30.0), Epoch.JNOW);

                Assert.That(await ((ITelescopeMediator)svc).MeridianFlip(target, CancellationToken.None), Is.True,
                    "the stub returned false for every flip (#1238)");

                var goto_ = box.Puts.Single(p => p.Path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal));
                Assert.That(FormValue(goto_.Body, "RightAscension"), Is.EqualTo(ra).Within(1e-9));
                Assert.That(FormValue(goto_.Body, "Declination"), Is.EqualTo(30.0).Within(1e-9));
                Assert.That(box.Puts.Any(p => p.Path.EndsWith("/sideofpier", StringComparison.Ordinal)), Is.False,
                    "side of pier is off in the profile: no pier-side hint");
            }
        }

        [Test]
        public async Task With_side_of_pier_on_and_a_capable_mount_the_destination_side_is_commanded_before_the_goto() {
            // Target 3 h east of the meridian → expected pierWest (ThroughThePole); the mount reports
            // Normal (east), so the hint is a SideOfPier write BEFORE the goto.
            var ra = (LstHours() + 3.0) % 24.0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(ra, sideOfPier: "0", canSetPierSide: "true"));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(30.0), Epoch.JNOW);

                Assert.That(await ((ITelescopeMediator)svc).MeridianFlip(target, CancellationToken.None), Is.True);

                var puts = box.Puts.ToList();
                var pier = puts.FindIndex(p => p.Path.EndsWith("/sideofpier", StringComparison.Ordinal));
                var goto_ = puts.FindIndex(p => p.Path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal));
                Assert.That(pier, Is.GreaterThanOrEqualTo(0), "the pier-side hint was never sent");
                Assert.That(FormValue(puts[pier].Body, "SideOfPier"), Is.EqualTo(1), "ThroughThePole = pierWest");
                Assert.That(goto_, Is.GreaterThan(pier), "the hint precedes the goto");
            }
        }

        [Test]
        public async Task A_mount_that_cannot_set_pier_side_gets_only_the_goto() {
            var ra = (LstHours() + 3.0) % 24.0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(ra, sideOfPier: "0", canSetPierSide: "false"));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(30.0), Epoch.JNOW);

                Assert.That(await ((ITelescopeMediator)svc).MeridianFlip(target, CancellationToken.None), Is.True);
                Assert.That(box.Puts.Any(p => p.Path.EndsWith("/sideofpier", StringComparison.Ordinal)), Is.False);
                Assert.That(box.Puts.Count(p => p.Path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal)), Is.EqualTo(1));
            }
        }

        [Test]
        public async Task A_flip_on_a_disconnected_mount_is_false_and_sends_nothing() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(6.0, sideOfPier: "0", canSetPierSide: "true"));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = new TelescopeService(profileService: profileService);
                Assert.That(await ((ITelescopeMediator)svc).MeridianFlip(new Coordinates(Angle.ByHours(6), Angle.ByDegree(30), Epoch.JNOW), CancellationToken.None), Is.False);
                Assert.That(box.Puts, Is.Empty);
            }
        }

        [Test]
        public async Task DestinationSideOfPier_asks_the_driver_in_the_mounts_frame_and_maps_the_answer() {
            // ThroughThePole (1) from the driver → pierWest; the query carries the target in the
            // mount's native frame (JNOW here, so untransformed).
            await using var box = ScriptedAlpacaDevice.Start(Mount(6.0, sideOfPier: "0", canSetPierSide: "false", destinationSideOfPier: "1"));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                var target = new Coordinates(Angle.ByHours(9.5), Angle.ByDegree(-12.0), Epoch.JNOW);

                Assert.That(((ITelescopeMediator)svc).DestinationSideOfPier(target), Is.EqualTo(PierSide.pierWest));

                var query = box.Gets.Last(g => g.EndsWith("/destinationsideofpier", StringComparison.Ordinal));
                Assert.That(query, Is.Not.Null, "the driver was asked");
            }
        }

        [Test]
        public async Task DestinationSideOfPier_is_unknown_when_the_driver_cannot_say() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(6.0, sideOfPier: "0", canSetPierSide: "false",
                destinationSideOfPier: ScriptedAlpacaDevice.NotImplemented("DestinationSideOfPier is not implemented")));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                Assert.That(((ITelescopeMediator)svc).DestinationSideOfPier(new Coordinates(Angle.ByHours(9.5), Angle.ByDegree(-12.0), Epoch.JNOW)),
                    Is.EqualTo(PierSide.pierUnknown));
            }
        }

        [Test]
        public async Task A_mount_that_does_not_implement_SideOfPier_is_read_once_then_left_alone() {
            // The #1239 review: a per-tick GET on a property the driver will never answer. The
            // not-implemented answer latches; later refresh ticks skip the read.
            await using var box = ScriptedAlpacaDevice.Start(Mount(6.0,
                sideOfPier: ScriptedAlpacaDevice.NotImplemented("SideOfPier is not implemented in this driver"), canSetPierSide: "false"));
            var (profile, profileService) = ProfileWith(useSideOfPier: true);
            using (profile) {
                using var svc = await ConnectAsync(box, profileService);
                await Task.Delay(TimeSpan.FromSeconds(5)); // two or more refresh ticks after the first

                Assert.That(((ITelescopeMediator)svc).GetInfo().SideOfPier, Is.EqualTo(PierSide.pierUnknown));
                Assert.That(box.Gets.Count(g => g.EndsWith("/sideofpier", StringComparison.Ordinal)), Is.EqualTo(1),
                    "after the not-implemented answer the refresh must stop asking");
                Assert.That(box.Gets.Count(g => g.EndsWith("/rightascension", StringComparison.Ordinal)), Is.GreaterThan(1),
                    "the refresh itself kept running");
            }
        }
    }
}
