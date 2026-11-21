#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1589 — <see cref="MountClearanceWatch"/>: with the clearance limit on, a mount that tracks up to it has its
    /// tracking and guiding stopped, its run paused and one Critical notification that says why, and it is never
    /// moved; off (the default), nothing happens. The site is 0.1° off the pole, so the mount's altitude is its
    /// declination (±0.1°) at any time of day.
    /// </summary>
    [TestFixture]
    public class MountClearanceWatchTest {

        private Mock<ITelescopeMediator> telescope = null!;
        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<IGuiderService> guider = null!;
        private Mock<INotificationService> notifications = null!;
        private List<NotificationDto> published = null!;
        private TelescopeInfo info = null!;
        private SafetyPoliciesDto safety = null!;

        private static SafetyPoliciesDto Safety(bool enabled, double limitDeg = 80) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true) {
                ClearanceLimitEnabled = enabled, ClearanceMaxAltitudeDeg = limitDeg,
            };

        private static TelescopeInfo Tracking(double decDeg, bool tracking = true, bool slewing = false, bool parked = false) => new() {
            Connected = true,
            TrackingEnabled = tracking,
            Slewing = slewing,
            AtPark = parked,
            Coordinates = new Coordinates(Angle.ByHours(3), Angle.ByDegree(decDeg), Epoch.JNOW),
        };

        [SetUp]
        public void SetUp() {
            safety = Safety(enabled: true);
            info = Tracking(85);
            telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(() => info);
            telescope.Setup(t => t.SetTrackingEnabled(It.IsAny<bool>())).Returns(true);
            profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(() => safety);
            profiles.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", false, 0, 4, 2.0, "astronomical"));
            sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Guid.NewGuid() });
            guider = new Mock<IGuiderService>();
            guider.Setup(g => g.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new GuiderDto("phd2", "PHD2", EquipmentConnectionState.Connected,
                new GuiderStateDto("guiding", null, null, null, null)));
            guider.Setup(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "guider.stop", DateTimeOffset.UtcNow, null));
            published = new List<NotificationDto>();
            notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((dto, _) => published.Add(dto))
                .Returns(Task.CompletedTask);
        }

        private MountClearanceWatch Watch() =>
            new(telescope.Object, profiles.Object, () => sequencer.Object, guider.Object, notifications.Object);

        private void NeverMoved() {
            telescope.Verify(t => t.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private void NothingDone() {
            telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published, Is.Empty);
        }

        [Test]
        public async Task With_the_clearance_limit_off_a_mount_tracking_near_the_zenith_is_left_alone() {
            safety = Safety(enabled: false);
            info = Tracking(88);

            using var watch = Watch();

            Assert.That(await watch.TickAsync(), Is.False);

            NothingDone();
        }

        [Test]
        public async Task A_mount_tracking_up_to_the_clearance_limit_stops_tracking_and_guiding_pauses_the_run_and_says_why() {
            using var watch = Watch();

            Assert.That(await watch.TickAsync(), Is.True);

            telescope.Verify(t => t.SetTrackingEnabled(false), Times.Once);
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.Never);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(published, Has.Count.EqualTo(1));
            Assert.Multiple(() => {
                Assert.That(published[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
                Assert.That(published[0].Category, Is.EqualTo(NotificationCategory.Safety));
                Assert.That(published[0].Message, Does.Contain("80° clearance limit").And.Contain("not moved"));
            });
            NeverMoved();
        }

        [Test]
        public async Task A_mount_tracking_just_under_the_clearance_limit_keeps_tracking() {
            info = Tracking(79.5);

            using var watch = Watch();

            Assert.That(await watch.TickAsync(), Is.False);

            NothingDone();
        }

        [Test]
        public async Task A_mount_that_keeps_tracking_after_the_stop_is_stopped_again_without_a_second_notification() {
            using var watch = Watch();

            await watch.TickAsync();
            await watch.TickAsync();

            telescope.Verify(t => t.SetTrackingEnabled(false), Times.Exactly(2));
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(published, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Tracking_turned_back_on_above_the_clearance_limit_is_stopped_and_reported_again() {
            using var watch = Watch();
            await watch.TickAsync();
            info = Tracking(85, tracking: false);
            await watch.TickAsync();
            info = Tracking(85);

            Assert.That(await watch.TickAsync(), Is.True);

            telescope.Verify(t => t.SetTrackingEnabled(false), Times.Exactly(2));
            Assert.That(published, Has.Count.EqualTo(2), "each time tracking comes back on above the limit, the user hears why it stopped");
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task A_slewing_or_parked_mount_is_left_alone(bool slewing, bool parked) {
            info = Tracking(85, slewing: slewing, parked: parked);

            using var watch = Watch();

            Assert.That(await watch.TickAsync(), Is.False);

            NothingDone();
        }
    }
}
