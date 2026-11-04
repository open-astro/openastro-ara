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
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The profile is the site's one source of truth: the daemon writes it into the mount
    /// (Alpaca SiteLatitude / SiteLongitude / SiteElevation) on connect and whenever it changes, so
    /// every other Alpaca client reads Ara's site from the mount.</summary>
    [TestFixture]
    public class MountSitePushTest {
        private static readonly string[] AllThree = ["lat -36.85", "lng 174.76", "elev 120"];
        private static readonly string[] LatLng = ["lat -36.85", "lng 174.76"];
        private static readonly string[] AllThreeThenLat = ["lat -36.85", "lng 174.76", "elev 120", "lat -36.85"];

        private sealed class FakeMount : TelescopeService.IMountSite {
            public readonly List<string> Writes = [];
            public double Latitude { get; set; } = -37.2036;
            public double Longitude { get; set; } = 174.8825;
            public double Elevation { get; set; } = 80;
            public bool Slewing { get; set; }
            public int Reads;
            public bool RefuseElevation;
            double TelescopeService.IMountSite.Latitude { get { Reads++; return Latitude; } }
            double TelescopeService.IMountSite.Longitude { get { Reads++; return Longitude; } }
            double TelescopeService.IMountSite.Elevation { get { Reads++; return Elevation; } }
            public Action? OnWrite;
            public void SetLatitude(double v) { OnWrite?.Invoke(); Writes.Add($"lat {v}"); Latitude = v; }
            public void SetLongitude(double v) { OnWrite?.Invoke(); Writes.Add($"lng {v}"); Longitude = v; }
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
        private static (TelescopeService svc, FakeMount mount, Mock<IProfileStore> store) Following(SiteSettingsDto site,
                Action<TimeSpan>? wait = null) {
            Func<TimeSpan, Task> waitAsync = d => { wait?.Invoke(d); return Task.CompletedTask; };
            var mount = new FakeMount();
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetSiteSettings()).Returns(() => site);
            var svc = new TelescopeService {
                MountSiteSource = () => mount,
                RunSitePush = push => push(),
                SitePushWait = waitAsync,
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

        [Test]
        public void A_profile_save_that_leaves_the_site_alone_does_not_touch_the_mount() {
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                store.Raise(s => s.Changed += null, EventArgs.Empty); // the site itself: pushed
                var reads = mount.Reads;
                store.Raise(s => s.Changed += null, EventArgs.Empty); // autofocus, storage, a saved target...
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Reads, Is.EqualTo(reads), "no Alpaca round trip for a save that isn't the site");
                Assert.That(mount.Writes, Is.EqualTo(AllThree));
            }
        }

        [Test]
        public void A_push_the_mount_refused_part_of_is_tried_again_on_the_next_save() {
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                mount.RefuseElevation = true;
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                mount.RefuseElevation = false;
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.EqualTo(AllThree), "the refused elevation, not the two it took");
            }
        }

        [Test]
        public void A_new_connect_pushes_the_site_again_even_when_it_has_not_changed() {
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                mount.Latitude = 0; // another mount, or one that forgot
                svc.PushProfileSite(force: true);
                Assert.That(mount.Writes, Is.EqualTo(AllThreeThenLat));
            }
        }

        [Test]
        [Category("IO")] // loopback Alpaca stub
        public async Task A_push_that_lands_after_a_reconnect_forgot_the_site_still_leaves_the_new_mount_pushed() {
            // ALP-4982 (#1486 review): a push begun for the old mount can mark the site pushed after the
            // reconnect's ForgetPushedSite(); that is safe only because every connect forces its push. This
            // fails if the connect stops forcing it.
            await using var stub = ScriptedAlpacaDevice.Start(_ => null);
            var (svc, oldMount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                svc.RunSitePush = push => Task.Run(push);
                using var hold = new ManualResetEventSlim();
                oldMount.OnWrite = () => hold.Wait(TimeSpan.FromSeconds(10));
                store.Raise(s => s.Changed += null, EventArgs.Empty); // the old mount's push, held mid-write
                var newMount = new FakeMount { Latitude = 0, Longitude = 0, Elevation = 0 };
                svc.MountSiteSource = () => newMount;
                var device = new DiscoveredDeviceDto("mount-under-test", "Bench mount", DeviceType.Telescope,
                    stub.BaseUri.Host, stub.BaseUri.Host, stub.BaseUri.Port, 0, false);
                await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None).ConfigureAwait(false);
                var until = DateTime.UtcNow.AddSeconds(5);
                while (svc.GetAsync(CancellationToken.None).GetAwaiter().GetResult()?.State != EquipmentConnectionState.Connected) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(until), "precondition: connected");
                    await Task.Delay(10).ConfigureAwait(false);
                }
                hold.Set(); // the old push ends now, after the connect forgot the site
                until = DateTime.UtcNow.AddSeconds(5);
                while (newMount.Writes.Count < 3 && DateTime.UtcNow < until) {
                    await Task.Delay(10).ConfigureAwait(false);
                }
                Assert.That(newMount.Writes, Is.EqualTo(AllThree), "the new mount got the site");
            }
        }

        [Test]
        public void Two_close_saves_push_one_at_a_time() {
            var mount = new FakeMount();
            var store = new Mock<IProfileStore>();
            var site = Site(-36.85, 174.76, 120);
            store.Setup(s => s.GetSiteSettings()).Returns(() => site);
            var pushes = new List<Func<Task>>();
            using var svc = new TelescopeService {
                MountSiteSource = () => mount,
                RunSitePush = push => { pushes.Add(push); return Task.CompletedTask; },
                SitePushWait = _ => Task.CompletedTask,
            };
            svc.FollowProfileSite(store.Object);
            store.Raise(s => s.Changed += null, EventArgs.Empty);
            site = Site(-37.2, 175.0, 50);
            store.Raise(s => s.Changed += null, EventArgs.Empty);
            var inside = 0;
            var overlapped = false;
            mount.OnWrite = () => { if (Interlocked.Increment(ref inside) > 1) overlapped = true; Thread.Sleep(20); Interlocked.Decrement(ref inside); };
            Task.WaitAll(Task.Run(pushes[0]), Task.Run(pushes[1]));
            Assert.That(overlapped, Is.False, "the second push waits for the first");
            Assert.That((mount.Latitude, mount.Longitude, mount.Elevation), Is.EqualTo((-37.2, 175.0, 50.0)),
                "both read the store when they run, so the mount ends on the newest site");
        }

        [Test]
        public void A_push_waits_for_a_slew_to_end() {
            var waits = 0;
            FakeMount? m = null;
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120), _ => { if (++waits == 3) m!.Slewing = false; });
            m = mount;
            using (svc) {
                mount.Slewing = true;
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(waits, Is.EqualTo(3));
                Assert.That(mount.Writes, Is.EqualTo(AllThree), "written once the mount stopped");
            }
        }

        [Test]
        public void A_slew_that_never_ends_leaves_the_site_for_the_next_save() {
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                mount.Slewing = true;
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.Empty, "nothing written mid-slew");
                mount.Slewing = false;
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.EqualTo(AllThree));
            }
        }

        [Test]
        public void An_elevation_of_zero_is_unset_and_leaves_the_mounts_alone() {
            var mount = new FakeMount();
            Assert.That(TelescopeService.PushSite(mount, -36.85, 174.76, 0), Is.True);
            Assert.That(mount.Writes, Is.EqualTo(LatLng), "0 m is the default nobody changed (user, 2026-11-03)");
            Assert.That(mount.Elevation, Is.EqualTo(80));
        }

        [Test]
        public async Task A_push_waiting_out_a_slew_holds_no_thread() {
            // Review of #1486: the wait was Thread.Sleep under a lock, a pool thread per waiting push.
            var mount = new FakeMount { Slewing = true };
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetSiteSettings()).Returns(Site(-36.85, 174.76, 120));
            var tick = new TaskCompletionSource();
            Task? push = null;
            using var svc = new TelescopeService {
                MountSiteSource = () => mount,
                RunSitePush = p => push = p(), // on the caller's thread: a blocking wait would hang here
                SitePushWait = _ => tick.Task,
            };
            svc.FollowProfileSite(store.Object);
            store.Raise(s => s.Changed += null, EventArgs.Empty);
            Assert.That(push, Is.Not.Null);
            Assert.That(push!.IsCompleted, Is.False, "parked on the wait, the caller already back");
            mount.Slewing = false;
            tick.SetResult();
            await push.ConfigureAwait(false);
            Assert.That(mount.Writes, Is.EqualTo(AllThree));
        }

        [Test]
        public void After_a_disconnect_the_next_mount_gets_the_site_even_on_a_plain_save() {
            // Review of #1486: the site last written survived a disconnect, so only the forced push on
            // connect kept a new mount from being skipped.
            var (svc, mount, store) = Following(Site(-36.85, 174.76, 120));
            using (svc) {
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                mount.Latitude = 0; // another mount
                svc.DisconnectAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                store.Raise(s => s.Changed += null, EventArgs.Empty);
                Assert.That(mount.Writes, Is.EqualTo(AllThreeThenLat));
            }
        }
    }
}
