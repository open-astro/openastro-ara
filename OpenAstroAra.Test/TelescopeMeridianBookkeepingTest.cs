#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1229 — the headless <see cref="TelescopeService"/> left <c>TimeToMeridianFlip</c>
    /// and <c>SiderealTime</c> at their TelescopeInfo default of 0 and <c>SideOfPier</c> at
    /// pierEast. To the NINA-derived <see cref="MeridianFlipTrigger"/> a time-to-flip of 0 h is
    /// "the flip window is now", so every run with a flip trigger on a tracking mount with a known
    /// position flipped at its FIRST item boundary. The service now serves the sidereal time from
    /// the profile site, the flip time through the shared astrometry rule, and the pier side from
    /// the mount, with NaN for every "cannot say" (the trigger's own guard).</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class TelescopeMeridianBookkeepingTest {

        // Greenwich, so LST == GMST and the expectation below needs no longitude arithmetic.
        private const double Longitude = 0.0;
        private static readonly DateTimeOffset At = new(2026, 3, 20, 22, 0, 0, TimeSpan.Zero);

        private static double LstHours() => SiteAstrometry.LocalSiderealTimeDeg(At, Longitude) / 15.0;

        private static Coordinates JNowAt(double raHours) =>
            new(Angle.ByHours(raHours), Angle.ByDegree(30.0), Epoch.JNOW);

        private static OpenAstroAra.Profile.Profile ProfileWith(bool useSideOfPier, double maxMinutesAfter = 10) {
            var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = Longitude;
            profile.MeridianFlipSettings.UseSideOfPier = useSideOfPier;
            profile.MeridianFlipSettings.MaxMinutesAfterMeridian = maxMinutesAfter;
            profile.MeridianFlipSettings.MinutesAfterMeridian = 0;
            profile.MeridianFlipSettings.PauseTimeBeforeMeridian = 0;
            return profile;
        }

        [Test]
        public void No_site_longitude_means_both_values_are_unknown() {
            using var profile = ProfileWith(false);
            var (lst, flip) = TelescopeService.MeridianBookkeeping(JNowAt(6), PierSide.pierUnknown,
                profile.MeridianFlipSettings, siteLongitudeDeg: null, At);
            Assert.That(double.IsNaN(lst), "no site: no sidereal time");
            Assert.That(double.IsNaN(flip), "no site: no flip time (NaN, never 0 = 'flip now')");
        }

        [Test]
        public void No_position_means_the_flip_time_is_unknown_but_the_sidereal_time_is_served() {
            using var profile = ProfileWith(false);
            var (lst, flip) = TelescopeService.MeridianBookkeeping(null, PierSide.pierUnknown,
                profile.MeridianFlipSettings, Longitude, At);
            Assert.That(lst, Is.EqualTo(LstHours()).Within(1e-9));
            Assert.That(double.IsNaN(flip));
        }

        [Test]
        public void No_flip_settings_means_the_flip_time_is_unknown() {
            var (_, flip) = TelescopeService.MeridianBookkeeping(JNowAt(6), PierSide.pierUnknown,
                flipSettings: null, Longitude, At);
            Assert.That(double.IsNaN(flip));
        }

        [Test]
        public void Flip_time_is_the_hour_angle_to_the_meridian_plus_the_max_minutes_after() {
            // Target 3 h east of the meridian with a 10-minute post-meridian window: the NINA rule
            // shifts the LST back by the window, so the flip is due in 3 h 10 min.
            using var profile = ProfileWith(false);
            var (_, flip) = TelescopeService.MeridianBookkeeping(JNowAt(LstHours() + 3.0), PierSide.pierUnknown,
                profile.MeridianFlipSettings, Longitude, At);
            Assert.That(flip, Is.EqualTo(3.0 + 10.0 / 60.0).Within(1e-6));
        }

        [Test]
        public void The_issue_case_RA_0_to_12h_at_LST_0_is_hours_away_not_zero() {
            // #1229's worked example: with LST pinned at 0 h (the old default) a target at RA 4 h
            // was "flip now". With the real LST the same target is 4 h 10 min from its flip.
            var at = At; // pick the instant whose LST is nearest 0 h within the day
            var best = At; var bestLst = 24.0;
            for (var m = 0; m < 24 * 60; m += 4) {
                var t = at.AddMinutes(m);
                var l = SiteAstrometry.LocalSiderealTimeDeg(t, Longitude) / 15.0;
                if (l < bestLst) { bestLst = l; best = t; }
            }
            using var profile = ProfileWith(false);
            var (lst, flip) = TelescopeService.MeridianBookkeeping(JNowAt(4.0), PierSide.pierUnknown,
                profile.MeridianFlipSettings, Longitude, best);
            Assert.That(lst, Is.LessThan(0.1), "the sweep found an LST ~0 h instant");
            Assert.That(flip, Is.EqualTo(4.0 - lst + 10.0 / 60.0).Within(1e-6));
            Assert.That(flip, Is.GreaterThan(3.9));
        }

        [Test]
        public void Side_of_pier_feeds_the_12h_deferral_when_the_mount_already_flipped() {
            // Target 30 min before the meridian, mount already on the post-flip side (pierEast is
            // the expected side only once the target is PAST the meridian): the rule defers 12 h.
            using var profile = ProfileWith(true);
            var settings = profile.MeridianFlipSettings;
            var target = JNowAt(LstHours() + 0.5);
            var (_, notFlipped) = TelescopeService.MeridianBookkeeping(target, PierSide.pierWest, settings, Longitude, At);
            var (_, alreadyFlipped) = TelescopeService.MeridianBookkeeping(target, PierSide.pierEast, settings, Longitude, At);
            Assert.That(notFlipped, Is.EqualTo(0.5 + 10.0 / 60.0).Within(1e-6));
            Assert.That(alreadyFlipped, Is.EqualTo(notFlipped + 12.0).Within(1e-6));
        }

        [TestCase(PointingState.Normal, PierSide.pierEast)]
        [TestCase(PointingState.ThroughThePole, PierSide.pierWest)]
        [TestCase(PointingState.Unknown, PierSide.pierUnknown)]
        public void Pointing_state_maps_to_the_NINA_pier_side(PointingState state, PierSide expected) {
            Assert.That(TelescopeService.MapPointingState(state), Is.EqualTo(expected));
        }

        // ─── Bench: the real service against a scripted loopback mount ─────────────────────────

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // A JNOW mount (no precession natives needed), tracking, 3 h east of the meridian, on the
        // pre-flip (west) side. A non-integer sideOfPier makes the client-side read throw, which is
        // how a driver's PropertyNotImplemented lands on the service too.
        private static Func<string, string?> Mount(double raHours, string? sideOfPier) => path =>
            path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? raHours.ToString(CultureInfo.InvariantCulture)
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "30.0"
            : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? sideOfPier
            : null;

        private static async Task<TelescopeService> ConnectAsync(ScriptedAlpacaDevice box, OpenAstroAra.Profile.Profile profile) {
            var profileService = new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var svc = new TelescopeService(profileService: profileService.Object);
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

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task GetInfo_serves_the_flip_inputs_from_the_site_and_the_mount() {
            using var profile = ProfileWith(false);
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, Longitude) / 15.0;
            await using var box = ScriptedAlpacaDevice.Start(Mount((lst + 3.0) % 24.0, sideOfPier: "1"));
            using var svc = await ConnectAsync(box, profile);

            var info = ((ITelescopeMediator)svc).GetInfo();
            Assert.That(info.SideOfPier, Is.EqualTo(PierSide.pierWest), "ThroughThePole from the mount");
            Assert.That(info.SiderealTime, Is.EqualTo(lst).Within(0.01), "LST from the profile site, not the 0 default");
            Assert.That(info.TimeToMeridianFlip, Is.EqualTo(3.0 + 10.0 / 60.0).Within(0.02), "3 h east + the 10 min window");
        }

        [Test]
        [Category("bench")]
        public async Task A_mount_without_SideOfPier_reports_unknown_not_east() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(6.0, sideOfPier: "\"not-an-int\""));
            using var profile = ProfileWith(true);
            using var svc = await ConnectAsync(box, profile);

            Assert.That(((ITelescopeMediator)svc).GetInfo().SideOfPier, Is.EqualTo(PierSide.pierUnknown),
                "an unreadable pier side is 'unknown' — the old pierEast default made §58.5 'verify' a flip that never moved the pier");
        }

        [Test]
        [Category("bench")]
        public async Task The_flip_trigger_does_not_fire_at_the_first_boundary_for_a_target_hours_from_the_meridian() {
            // #1229 end-to-end: the real trigger over the real service. Before the fix this
            // returned true ("flip should happen now") for every tracking mount with a position.
            using var profile = ProfileWith(false);
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, Longitude) / 15.0;
            await using var box = ScriptedAlpacaDevice.Start(Mount((lst + 3.0) % 24.0, sideOfPier: "1"));
            using var svc = await ConnectAsync(box, profile);
            var profileService = new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var trigger = new MeridianFlipTrigger(profileService.Object, svc, Mock.Of<IMeridianFlipExecutor>());

            Assert.That(trigger.ShouldTrigger(null, null), Is.False,
                "a target 3 h east of the meridian is not in the flip window");
            Assert.That(trigger.LatestFlipTime, Is.GreaterThan(DateTime.Now + TimeSpan.FromHours(2.9)),
                "the trigger projects the flip ~3 h out from the served time-to-flip");
        }
    }
}
