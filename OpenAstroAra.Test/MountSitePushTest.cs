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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The profile is the site's one source of truth: the daemon writes it into the mount
    /// (Alpaca SiteLatitude / SiteLongitude / SiteElevation) on connect and whenever it changes, so
    /// every other Alpaca client reads Ara's site from the mount.</summary>
    [TestFixture]
    public class MountSitePushTest {
        private static readonly string[] AllThree = ["lat -36.85", "lng 174.76", "elev 120"];
        private static readonly string[] LatLng = ["lat -36.85", "lng 174.76"];

        private sealed class FakeMount : TelescopeService.IMountSite {
            public readonly List<string> Writes = [];
            public double Latitude { get; set; } = -37.2036;
            public double Longitude { get; set; } = 174.8825;
            public double Elevation { get; set; } = 80;
            public bool RefuseElevation;
            public void SetLatitude(double v) { Writes.Add($"lat {v}"); Latitude = v; }
            public void SetLongitude(double v) { Writes.Add($"lng {v}"); Longitude = v; }
            public void SetElevation(double v) {
                if (RefuseElevation) throw new InvalidOperationException("not settable");
                Writes.Add($"elev {v}"); Elevation = v;
            }
        }

        [Test]
        public void The_profiles_site_is_written_into_the_mount() {
            var mount = new FakeMount();
            var result = TelescopeService.PushSite(mount, -36.85, 174.76, 120);
            Assert.That(mount.Writes, Is.EqualTo(AllThree));
            Assert.That(result, Is.True);
        }

        [Test]
        public void A_site_the_mount_already_has_is_not_written_again() {
            var mount = new FakeMount();
            Assert.That(TelescopeService.PushSite(mount, -37.2036, 174.8825, 80), Is.True);
            Assert.That(mount.Writes, Is.Empty);
        }

        [Test]
        public void A_profile_with_no_site_yet_never_overwrites_the_mounts() {
            Assert.That(TelescopeService.HasSite(0, 0), Is.False, "the default 0, 0 is not a place anyone observes from");
            Assert.That(TelescopeService.HasSite(-37.2036, 174.8825), Is.True);
            Assert.That(TelescopeService.HasSite(0, 174.8825), Is.True);
        }

        private static SiteSettingsDto Site(double lat, double lng, double elev) =>
            new("Home", lat, lng, elev, "Pacific/Auckland", false, 0, 4, 2, "astronomical");

        // A connected mount behind the seam, the push run inline, following a profile store.
        private static (TelescopeService svc, FakeMount mount, Mock<IProfileStore> store) Following(SiteSettingsDto site) {
            var mount = new FakeMount();
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetSiteSettings()).Returns(site);
            var svc = new TelescopeService {
                MountSiteSource = () => mount,
                RunSitePush = push => { push(); return Task.CompletedTask; },
            };
            svc.FollowProfileSite(store.Object);
            return (svc, mount, store);
        }

        [Test]
        public void A_profile_change_pushes_its_site_into_the_connected_mount() {
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.EqualTo(AllThree));
            }
        }

        [Test]
        public void A_profile_change_with_no_site_leaves_the_mounts_alone() {
            var (svc, mount, store) = Following(Site(0, 0, 0));
            using (svc) {
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.Empty);
            }
        }

        [Test]
        public void With_no_mount_connected_a_profile_change_pushes_nothing() {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetSiteSettings()).Returns(Site(-36.85, 174.76, 120));
            var pushes = 0;
            using var svc = new TelescopeService { RunSitePush = _ => { pushes++; return Task.CompletedTask; } };
            svc.FollowProfileSite(store.Object);
            store.Raise(s => s.Changed += null, EventArgs.Empty);
            Assert.That(pushes, Is.Zero, "a fresh service has no mount connected");
        }

        [Test]
        public void A_value_the_mount_refuses_does_not_stop_the_others() {
            var mount = new FakeMount { RefuseElevation = true };
            var result = TelescopeService.PushSite(mount, -36.85, 174.76, 120);
            Assert.That(mount.Writes, Is.EqualTo(LatLng));
            Assert.That(result, Is.False, "reported, so the caller can log it");
        }
    }
}
