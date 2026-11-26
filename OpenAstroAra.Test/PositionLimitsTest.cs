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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2140 (record 0026) — the position limits (lowest altitude, counterweight) bind every asker but the safety moves: a
    /// Session's GoTo, a Center and a flip to a place past a limit are refused before anything reaches the mount, and a
    /// park or a home goes out from anywhere. Through the real Telescope service on the in-memory fake mount.
    /// </summary>
    [TestFixture]
    public class PositionLimitsTest {

        // Latitude 40°, longitude 0, a 20° floor.
        private static IProfileStore Site() {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("site", 40.0, 0, 0, "UTC", false, 20, 4, 2.0, "astronomical"));
            store.Setup(p => p.GetCustomHorizon()).Returns(new CustomHorizonDto(Array.Empty<CustomHorizonPointDto>()));
            return store.Object;
        }

        private static Coordinates AtHourAngle(double hourAngleHours, double decDeg) {
            var lstHours = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0;
            return new Coordinates(((lstHours - hourAngleHours) % 24.0 + 24.0) % 24.0, decDeg, Epoch.J2000, Coordinates.RAType.Hours);
        }

        /// <summary>10° up on the meridian: under the 20° floor.</summary>
        private static Coordinates BelowTheFloor() => AtHourAngle(0, -40);

        /// <summary>Below the pole an hour from the lower meridian, 30° up: the counterweight would be up.</summary>
        private static Coordinates PastTheCounterweight() => AtHourAngle(-11, 80);

        private static async Task<SequenceEntityFailedException> RefusedAsync(Func<Task> move, RefusalCause cause) {
            var failed = await RefusedOnlyAsync(move);
            Assert.That((failed.InnerException as RefusalException)?.Cause, Is.EqualTo(cause), "the instruction fails carrying the Refusal's cause");
            return failed;
        }

        private static async Task<SequenceEntityFailedException> RefusedOnlyAsync(Func<Task> move) {
            try {
                await move();
            } catch (SequenceEntityFailedException failed) {
                return failed;
            }
            Assert.Fail("the move was not refused");
            throw new InvalidOperationException();
        }

        [Test]
        public async Task A_session_goto_below_the_lowest_altitude_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(Site());
            await RefusedAsync(() => rig.Service.SlewToCoordinatesAsync(BelowTheFloor(), MoveAsker.Session, CancellationToken.None), RefusalCatalog.BelowHorizonLimit);
            Assert.That(rig.Fake.Slews, Is.Zero, "nothing reached the mount");
        }

        // The same kind of notice as "target is low": a warning in the Sequence category that says why.
        [Test]
        public async Task A_refused_session_goto_posts_a_notice_that_says_why() {
            var notices = new System.Collections.Generic.List<NotificationDto>();
            var store = new Mock<INotificationService>();
            store.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => notices.Add(n)).Returns(Task.CompletedTask);
            using var rig = await FakeMountRig.ConnectAsync(Site(), notifications: store.Object);

            await RefusedAsync(() => rig.Service.SlewToCoordinatesAsync(BelowTheFloor(), MoveAsker.Session, CancellationToken.None), RefusalCatalog.BelowHorizonLimit);

            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.Multiple(() => {
                Assert.That(notices[0].Severity, Is.EqualTo(NotificationSeverity.Warning));
                Assert.That(notices[0].Category, Is.EqualTo(NotificationCategory.Sequence));
                Assert.That(notices[0].Title, Does.Contain("below the horizon limit"));
                Assert.That(notices[0].Message, Does.Contain("horizon limit").And.Contain("Nothing was sent"));
            });
        }

        [Test]
        public async Task A_session_goto_past_the_counterweight_limit_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(Site());
            await RefusedAsync(() => rig.Service.SlewToCoordinatesAsync(PastTheCounterweight(), MoveAsker.Session, CancellationToken.None), RefusalCatalog.CounterweightLimit);
            Assert.That(rig.Fake.Slews, Is.Zero, "nothing reached the mount");
        }

        [Test]
        public async Task A_center_past_the_counterweight_limit_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(Site());
            await RefusedAsync(() => rig.Service.SlewToCoordinatesAsync(PastTheCounterweight(), MoveAsker.Center, CancellationToken.None), RefusalCatalog.CounterweightLimit);
            Assert.That(rig.Fake.Slews, Is.Zero, "nothing reached the mount");
        }

        [Test]
        public async Task A_flip_whose_destination_is_below_the_floor_is_refused() {
            using var rig = await FakeMountRig.ConnectAsync(Site(), configure: f => f.CanSetPierSideValue = true);
            await RefusedAsync(() => rig.Service.MeridianFlip(BelowTheFloor(), CancellationToken.None), RefusalCatalog.BelowHorizonLimit);
            Assert.That((rig.Fake.Slews, rig.Fake.PierSideWrites), Is.EqualTo((0, 0)), "nothing reached the mount");
        }

        // The exempt askers stay green on both sides of the change: a park and a home are the way out, and a safety move is
        // never refused by a position limit. The mount points past both limits.
        [Test]
        public async Task The_safety_fault_and_shutdown_parks_and_a_persons_park_and_home_go_out_from_past_the_limits() {
            using var rig = await FakeMountRig.ConnectAsync(Site(), configure: f => { f.Dec = 80; });
            // Home first: a home from a parked mount is refused (#2140), a park on a parked one is not.
            await rig.Service.FindHomeAsync(null, CancellationToken.None);
            await FakeMountRig.WaitForAsync(() => rig.Fake.Homes == 1, "a person's home");
            await rig.Service.ParkUnlessMotionOwnedAsync(new ParkRequestDto(), null, CancellationToken.None);
            await FakeMountRig.WaitForAsync(() => rig.Fake.Parks == 1, "a person's park");
            await rig.Service.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None);
            await FakeMountRig.WaitForAsync(() => rig.Fake.Parks == 2, "the safety park");
            await rig.Service.ParkAsync(new ParkRequestDto("equipment.fault"), null, CancellationToken.None);
            await FakeMountRig.WaitForAsync(() => rig.Fake.Parks == 3, "the fault park");
            Assert.That(await rig.Service.ParkTelescope(new Progress<ApplicationStatus>(), MoveAsker.Shutdown, CancellationToken.None), Is.True, "the shutdown park");
            Assert.That(rig.Fake.Parks, Is.EqualTo(4));
        }
    }
}
