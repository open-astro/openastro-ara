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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
// Both the server contracts and the equipment interfaces define a DeviceType; the §58.9
// reconnector tests speak the server-contract one (same alias as the executor itself).
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §58.4 — <see cref="MeridianFlipExecutor"/>. Verifies the post-flip recovery orchestration (stop guiding →
    /// pass meridian → flip slew → recenter → resume guiding → settle → side-of-pier) over fully mocked
    /// mediators + a mocked centering service. No real equipment is touched; SettleTime + timeToFlip are 0 so
    /// the wait loops are skipped and the tests run instantly.
    /// </summary>
    [TestFixture]
    public class MeridianFlipExecutorTest {

        private Mock<IProfileService> profileService = null!;
        private Mock<ITelescopeMediator> telescope = null!;
        private Mock<IGuiderMediator> guider = null!;
        private Mock<ICenteringService> centering = null!;
        private Mock<IDomeMediator> dome = null!;
        private Mock<IDomeFollower> domeFollower = null!;
        private List<string> callLog = null!;

        private static readonly Coordinates Target = new(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);

        private static readonly string[] FullSequenceWithGuider = {
            "StopGuiding",
            "SetTracking(False)",
            "SetTracking(True)",
            "Flip",
            "Recenter",
            "AutoSelectGuideStar",
            "StartGuiding",
        };

        private void SetupProfile(bool recenter = true, bool autoFocus = false, int settleTime = 0) {
            var settings = new Mock<IMeridianFlipSettings>();
            settings.SetupGet(s => s.Recenter).Returns(recenter);
            settings.SetupGet(s => s.AutoFocusAfterFlip).Returns(autoFocus);
            settings.SetupGet(s => s.SettleTime).Returns(settleTime);
            profileService.SetupGet(p => p.ActiveProfile.MeridianFlipSettings).Returns(settings.Object);
        }

        [SetUp]
        public void SetUp() {
            callLog = new List<string>();
            profileService = new Mock<IProfileService>();

            telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo { Connected = true, SideOfPier = PierSide.pierEast });
            telescope.Setup(t => t.SetTrackingEnabled(It.IsAny<bool>()))
                .Callback<bool>(on => callLog.Add($"SetTracking({on})")).Returns(true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("Flip")).ReturnsAsync(true);

            guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("StopGuiding")).ReturnsAsync(true);
            guider.Setup(g => g.AutoSelectGuideStar(It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("AutoSelectGuideStar")).ReturnsAsync(true);
            guider.Setup(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("StartGuiding")).ReturnsAsync(true);

            centering = new Mock<ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => callLog.Add("Recenter")).ReturnsAsync(new PlateSolveResult { Success = true });

            dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = false });
            domeFollower = new Mock<IDomeFollower>();
        }

        private MeridianFlipExecutor CreateSUT() =>
            new(profileService.Object, telescope.Object, guider.Object, centering.Object, dome.Object, domeFollower.Object);

        // ─── §58.9 harness — the safety-collaborator overload with test-speed timing knobs ───

        private Mock<IProfileStore> profileStore = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IEquipmentReconnector> reconnector = null!;
        private Mock<ICameraMediator> camera = null!;
        private Mock<IFocuserMediator> focuser = null!;
        private List<NotificationDto> published = null!;

        /// <summary>A safety-policies DTO with §58.9 enabled and everything else at inert values.
        /// <c>firstFlipConfirmed</c> defaults TRUE here (unlike the DTO's false) so the many
        /// existing layer tests exercise their own concern without the §58.8 announce+wait in
        /// front; the dedicated §58.8 tests opt into false.</summary>
        private static SafetyPoliciesDto Safety(bool enabled = true, int expectedSlewSeconds = 90,
                bool recalGuider = false, bool firstFlipConfirmed = true) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: recalGuider, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                FlipSafetyEnabled: enabled, ExpectedFlipSlewSeconds: expectedSlewSeconds,
                FirstFlipConfirmed: firstFlipConfirmed);

        /// <summary>Site at lat 40 N with a 20° horizon floor. The default <see cref="SafeTarget"/>
        /// (dec 89°) is circumpolar with min altitude ≈ 39° — above the floor at ANY sidereal time,
        /// so flight-check-passing tests never depend on the clock. <see cref="LowTarget"/> (dec
        /// −40°) peaks at 10° — below the floor at any time.</summary>
        private static SiteSettingsDto Site(double horizon = 20) =>
            new(SiteName: "Test", LatitudeDeg: 40, LongitudeDeg: 0, ElevationM: 0, TimeZone: "UTC",
                UseCustomHorizon: false, DefaultHorizonAltitudeDeg: horizon, BortleClass: 4,
                TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        private static readonly Coordinates SafeTarget = new(Angle.ByHours(5), Angle.ByDegree(89), Epoch.J2000);
        private static readonly Coordinates LowTarget = new(Angle.ByHours(5), Angle.ByDegree(-40), Epoch.J2000);

        private void SetupSafety(SafetyPoliciesDto safety) {
            profileStore = new Mock<IProfileStore>();
            profileStore.Setup(p => p.GetSafetyPolicies()).Returns(safety);
            profileStore.Setup(p => p.GetSiteSettings()).Returns(Site());

            published = new List<NotificationDto>();
            notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((dto, _) => published.Add(dto))
                .Returns(Task.CompletedTask);

            reconnector = new Mock<IEquipmentReconnector>();
            camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = true });
            focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = true });
        }

        private MeridianFlipExecutor CreateSafetySUT() =>
            new(profileService.Object, telescope.Object, guider.Object, centering.Object, dome.Object,
                domeFollower.Object, profileStore.Object, notifications.Object, reconnector.Object,
                camera.Object, focuser.Object) {
                // Spec timings shrunk so the watchdog/reconnect tests run in milliseconds.
                WatchdogSampleInterval = TimeSpan.FromMilliseconds(20),
                WatchdogStallWindow = TimeSpan.FromMilliseconds(60),
                WatchdogHardCap = TimeSpan.FromSeconds(30),
                ReconnectPollInterval = TimeSpan.FromMilliseconds(10),
                ReconnectWait = TimeSpan.FromMilliseconds(60),
                FirstFlipConfirmWait = TimeSpan.FromMilliseconds(30),
            };

        private static IProgress<ApplicationStatus> Progress => new Progress<ApplicationStatus>();

        [Test]
        public async Task MeridianFlip_runs_the_full_recovery_sequence_in_order_when_a_guider_is_connected() {
            SetupProfile(recenter: true);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // Stop guiding → pass meridian (tracking off then on) → flip → recenter → select+resume guiding.
            Assert.That(callLog, Is.EqualTo(FullSequenceWithGuider));
        }

        [Test]
        public async Task MeridianFlip_skips_all_guider_steps_when_no_guider_is_connected() {
            SetupProfile(recenter: true);
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = false });
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.AutoSelectGuideStar(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            // The flip + recenter still run.
            Assert.That(callLog, Does.Contain("Flip"));
            Assert.That(callLog, Does.Contain("Recenter"));
        }

        [Test]
        public async Task MeridianFlip_does_not_recenter_when_recenter_is_disabled() {
            SetupProfile(recenter: false);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task MeridianFlip_succeeds_even_when_the_recenter_solve_fails() {
            SetupProfile(recenter: true);
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSUT();

            // Recenter is best-effort — a failed solve logs + continues, the flip still succeeds.
            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
        }

        [Test]
        public async Task MeridianFlip_does_not_throw_when_autofocus_after_flip_is_enabled() {
            SetupProfile(recenter: true, autoFocus: true);
            var sut = CreateSUT();

            // The live AF sweep is unbuilt; the executor logs the skip rather than failing.
            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
        }

        [Test]
        public async Task MeridianFlip_fails_and_restores_tracking_when_the_mount_reports_the_flip_did_not_succeed() {
            SetupProfile(recenter: true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            // No recenter once the flip failed.
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            // Tracking is re-enabled exactly twice: once resuming after PassMeridian, once in the failure-path
            // TryRestoreTracking. The guider is resumed once (the failure-path best-effort resume).
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.Exactly(2));
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task MeridianFlip_fails_and_restores_tracking_when_the_flip_slew_throws() {
            SetupProfile(recenter: true);
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("mount fault"));
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.AtLeastOnce);
        }

        [Test]
        public void MeridianFlip_propagates_cancellation_and_restores_tracking() {
            SetupProfile(recenter: true);
            using var cts = new CancellationTokenSource();
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => cts.Cancel())
                .ThrowsAsync(new OperationCanceledException());
            var sut = CreateSUT();

            Assert.ThrowsAsync<OperationCanceledException>(
                () => sut.MeridianFlip(Target, TimeSpan.Zero, Progress, cts.Token));
            // Cancellation still restores tracking before propagating...
            telescope.Verify(t => t.SetTrackingEnabled(true), Times.AtLeastOnce);
            // ...but deliberately does NOT resume the guider — a cancel is the user stopping, so PHD2 stays
            // stopped rather than being restarted against that intent.
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void MeridianFlip_throws_on_a_null_target() {
            SetupProfile();
            var sut = CreateSUT();
            Assert.ThrowsAsync<ArgumentNullException>(
                () => sut.MeridianFlip(null!, TimeSpan.Zero, Progress, CancellationToken.None));
        }

        [Test]
        public async Task MeridianFlip_succeeds_when_the_pier_side_changes_across_the_flip() {
            SetupProfile(recenter: false);
            // §58.5 — pier side is snapshotted before the flip (pierEast) and re-read after (pierWest): a genuine
            // change is the verified-flip path. The check is log-only, so the flip still succeeds either way.
            // A third pierWest fallback guards against a future refactor adding a GetInfo() call (Moq would
            // otherwise return null past the sequence end → a misleading NRE rather than the regression).
            telescope.SetupSequence(t => t.GetInfo())
                .Returns(new TelescopeInfo { Connected = true, SideOfPier = PierSide.pierEast })
                .Returns(new TelescopeInfo { Connected = true, SideOfPier = PierSide.pierWest })
                .Returns(new TelescopeInfo { Connected = true, SideOfPier = PierSide.pierWest });
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
        }

        [Test]
        public async Task MeridianFlip_does_not_resume_or_restore_when_stopping_the_guider_threw_first() {
            SetupProfile(recenter: true);
            // StopAutoguider throws before PassMeridian runs: guiding was never stopped and tracking never
            // disabled, so the failure path must NOT resume the guider or "restore" tracking (both would log a
            // state change that never happened).
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider fault"));
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public async Task MeridianFlip_synchronizes_a_connected_following_dome_after_the_flip() {
            SetupProfile(recenter: false);
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetAzimuth = true });
            domeFollower.SetupGet(d => d.IsFollowing).Returns(true);
            domeFollower.Setup(d => d.WaitForDomeSynchronization(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask).Verifiable();
            var sut = CreateSUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            domeFollower.Verify(d => d.WaitForDomeSynchronization(It.IsAny<CancellationToken>()), Times.Once);
        }

        // ─── §58.9 Layer 1 — pre-flip flight check ───

        [Test]
        public void Predicted_altitude_matches_the_transit_geometry() {
            // dec −40 from lat 40: peak altitude 90 − |40 − (−40)| = 10°, at ANY time ≤ 10°.
            var alt = MeridianFlipExecutor.PredictedTargetAltitudeDeg(LowTarget, 40, 0, DateTimeOffset.UtcNow);
            Assert.That(alt, Is.LessThanOrEqualTo(10.0 + 1e-6));
            // dec 89 from lat 40 is circumpolar: min altitude = 40 + 89 − 90 = 39°.
            var safe = MeridianFlipExecutor.PredictedTargetAltitudeDeg(SafeTarget, 40, 0, DateTimeOffset.UtcNow);
            Assert.That(safe, Is.GreaterThanOrEqualTo(39.0 - 1e-6));
        }

        [Test]
        public async Task Flight_check_blocks_a_flip_to_a_target_below_the_horizon_floor() {
            SetupProfile();
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(LowTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never,
                "the flip must never start on a failed flight check");
            telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never,
                "the mount stays untouched in its known-safe pre-flip state");
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published, Has.Count.EqualTo(1));
            Assert.That(published[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(published[0].Message, Does.Contain("horizon floor"));
        }

        [Test]
        public async Task Flight_check_blocks_on_a_parked_or_untracked_or_slewing_mount() {
            SetupProfile();
            SetupSafety(Safety());
            var sut = CreateSafetySUT();

            foreach (var (info, expected) in new (TelescopeInfo, string)[] {
                (new TelescopeInfo { Connected = false }, "not connected"),
                (new TelescopeInfo { Connected = true, AtPark = true }, "parked"),
                (new TelescopeInfo { Connected = true, Slewing = true }, "already slewing"),
                (new TelescopeInfo { Connected = true, TrackingEnabled = false }, "not tracking"),
            }) {
                published.Clear();
                telescope.Setup(t => t.GetInfo()).Returns(info);
                var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);
                Assert.That(ok, Is.False, expected);
                Assert.That(published.Single().Message, Does.Contain(expected));
            }
            telescope.Verify(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_throwing_flight_check_is_a_failed_check_not_an_unnotified_escape() {
            // #629 review: an equipment/profile query throwing INSIDE the flight check must yield
            // the same outcome as a failed gate — no flip, notification fired — never an uncaught
            // escape that skips the §35.5 alarm.
            SetupProfile();
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            profileStore.Setup(p => p.GetSiteSettings()).Throws(new InvalidOperationException("profile store on fire"));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published.Single().Message, Does.Contain("flight check itself failed"));
        }

        [Test]
        public async Task A_disconnected_camera_that_hot_reconnects_lets_the_flip_proceed() {
            SetupProfile(recenter: false);
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            var connected = false;
            camera.Setup(c => c.GetInfo()).Returns(() => new CameraInfo { Connected = connected });
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .Callback(() => connected = true)   // the background reconnect "lands" immediately
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            reconnector.Verify(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task A_camera_that_stays_disconnected_blocks_the_flip() {
            SetupProfile();
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = false });
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(published.Single().Message, Does.Contain("Camera"));
            telescope.Verify(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ─── §58.9 Layer 2 — in-slew watchdog + hard pier-side gate ───

        [Test]
        public async Task Watchdog_aborts_a_stalled_slew_and_fails_the_flip() {
            SetupProfile();
            SetupSafety(Safety());
            SetupHealthyTrackingMount(); // fixed RA/Dec → the position never progresses
            var neverCompletes = new TaskCompletionSource<bool>();
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Returns(neverCompletes.Task);
            // StopSlew is how the real driver ends an aborted slew — unwind the flip call then.
            telescope.Setup(t => t.StopSlew()).Callback(() => neverCompletes.TrySetCanceled());
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.StopSlew(), Times.Once);
            Assert.That(published.Single().Message, Does.Contain("stalled"));
        }

        [Test]
        public async Task Watchdog_aborts_on_the_hard_timeout_even_while_the_mount_is_moving() {
            SetupProfile();
            SetupSafety(Safety(expectedSlewSeconds: 1)); // hard timeout = 3 s… shrunk below
            var ra = 0.0;
            telescope.Setup(t => t.GetInfo()).Returns(() => new TelescopeInfo {
                Connected = true, TrackingEnabled = true, SideOfPier = PierSide.pierEast,
                RightAscension = ra += 0.1, Declination = 0,   // always progressing — only the timeout can fire
            });
            var neverCompletes = new TaskCompletionSource<bool>();
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Returns(neverCompletes.Task);
            telescope.Setup(t => t.StopSlew()).Callback(() => neverCompletes.TrySetCanceled());
            var sut = CreateSafetySUT();
            sut.WatchdogHardCap = TimeSpan.FromMilliseconds(80); // min(3 s, 80 ms) → 80 ms

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.StopSlew(), Times.Once);
            Assert.That(published.Single().Message, Does.Contain("timeout"));
        }

        [Test]
        public async Task An_unchanged_known_pier_side_hard_fails_with_safety_on_but_only_warns_with_it_off() {
            // A healthy tracking mount whose pier side stays pierEast through the "flip".
            SetupProfile(recenter: false);
            SetupSafety(Safety());
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo {
                Connected = true, TrackingEnabled = true, SideOfPier = PierSide.pierEast,
                RightAscension = 5, Declination = 20,
            });
            var sut = CreateSafetySUT();
            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);
            Assert.That(ok, Is.False, "safety ON → an unflipped pier side must fail the flip");
            Assert.That(published.Single().Message, Does.Contain("pier side"));

            // Same rig with the §58.9 toggle off → the §58.5 warn-and-continue baseline.
            SetupSafety(Safety(enabled: false));
            var relaxed = CreateSafetySUT();
            var okRelaxed = await relaxed.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);
            Assert.That(okRelaxed, Is.True, "safety OFF → misreporting drivers keep working");
        }

        // ─── §58.9 Layer 3 — post-flip verification gate ───

        [Test]
        public async Task Recenter_that_never_verifies_fails_the_flip_after_three_attempts() {
            SetupProfile(recenter: true);
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False, "no verified pointing → imaging must not resume");
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            Assert.That(published.Single().Message, Does.Contain("verification failed"));
        }

        [Test]
        public async Task A_solve_far_from_the_target_is_not_trusted_but_a_close_retry_passes() {
            SetupProfile(recenter: true);
            SetupSafety(Safety());
            SetupHealthyTrackingMount();
            // First solve says the scope is ~30° away (don't trust it); the retry lands 0.5° out.
            var wayOff = new Coordinates(Angle.ByHours(5), Angle.ByDegree(59), Epoch.J2000);
            var close = new Coordinates(Angle.ByHours(5), Angle.ByDegree(88.5), Epoch.J2000);
            centering.SetupSequence(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = wayOff })
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = close });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True, "the in-bounds retry verifies the pointing");
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Test]
        public async Task With_safety_off_a_failed_recenter_still_continues() {
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false));
            SetupHealthyTrackingMount();
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True, "baseline §58.4: re-centering stays best-effort");
            centering.Verify(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ─── §58.9 Layer 4 — safe rest on failure ───

        [Test]
        public async Task A_failed_flip_parks_a_parkable_mount_and_leaves_the_guider_stopped() {
            SetupProfile(recenter: true);
            SetupSafety(Safety());
            var side = PierSide.pierEast;
            telescope.Setup(t => t.GetInfo()).Returns(() => new TelescopeInfo {
                Connected = true, TrackingEnabled = true, SideOfPier = side,
                RightAscension = 5, Declination = 20, CanPark = true,
            });
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => side = PierSide.pierWest).ReturnsAsync(true);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });   // Layer 3 fails → Layer 4
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
            guider.Verify(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never, "PHD2 corrections on a mis-aimed scope drift it further — the guider stays stopped");
            Assert.That(published.Single().Message, Does.Contain("parked"));
        }

        [Test]
        public async Task A_pier_side_failure_after_guider_resume_still_ends_with_the_guider_stopped() {
            // #630 review: the pier-side hard fail fires AFTER the guider-resume step, so safe
            // rest must stop the guider again — the "guider is stopped" claim in the notification
            // has to hold for every path into the failure handler, not just pre-resume ones.
            SetupProfile(recenter: false);
            SetupSafety(Safety());
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo {
                Connected = true, TrackingEnabled = true, SideOfPier = PierSide.pierEast,   // never flips
                RightAscension = 5, Declination = 20,
            });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            var resumeIndex = callLog.LastIndexOf("StartGuiding");
            var finalStopIndex = callLog.LastIndexOf("StopGuiding");
            Assert.That(resumeIndex, Is.GreaterThanOrEqualTo(0), "the resume step ran before the pier-side check");
            Assert.That(finalStopIndex, Is.GreaterThan(resumeIndex),
                "safe rest must stop the guider AFTER the resume that preceded the pier-side failure");
        }

        [Test]
        public async Task A_failed_park_attempt_reports_itself_honestly_not_as_cannot_park() {
            // #630 review: CanPark=true but the attempt fails → the notification must say the
            // ATTEMPT failed (an operator diagnostic), not misreport "the mount cannot park".
            SetupProfile(recenter: true);
            SetupSafety(Safety());
            var side = PierSide.pierEast;
            telescope.Setup(t => t.GetInfo()).Returns(() => new TelescopeInfo {
                Connected = true, TrackingEnabled = true, SideOfPier = side,
                RightAscension = 5, Declination = 20, CanPark = true,
            });
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => side = PierSide.pierWest).ReturnsAsync(true);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);   // parkable, but this attempt fails
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(callLog.Last(), Is.EqualTo("SetTracking(False)"), "fallback action still runs");
            Assert.That(published.Single().Message, Does.Contain("park attempt failed"));
            Assert.That(published.Single().Message, Does.Not.Contain("cannot park"));
        }

        [Test]
        public async Task A_failed_flip_on_an_unparkable_mount_stops_tracking_instead() {
            SetupProfile(recenter: true);
            SetupSafety(Safety());
            SetupHealthyTrackingMount();   // CanPark defaults to false
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(callLog.Last(), Is.EqualTo("SetTracking(False)"),
                "safe rest ends with tracking OFF — the §58.4 restore-to-tracking path must not run");
            Assert.That(published.Single().Message, Does.Contain("tracking was stopped"));
        }

        // ─── §58.8 — the first-flip confirmation safety net ───

        [Test]
        public async Task The_first_flip_announces_waits_out_the_window_and_persists_the_confirmation() {
            // recenter off: the §58.9 Layer-3 solve gate is another test's concern.
            SetupProfile(recenter: false);
            SetupSafety(Safety(firstFlipConfirmed: false));
            SetupHealthyTrackingMount();
            SafetyPoliciesDto? persisted = null;
            profileStore.Setup(p => p.UpdateSafetyPolicies(It.IsAny<Func<SafetyPoliciesDto, SafetyPoliciesDto?>>()))
                .Returns<Func<SafetyPoliciesDto, SafetyPoliciesDto?>>(f => {
                    // Apply the executor's transform to the armed policies, like the real store would.
                    persisted = f(Safety(firstFlipConfirmed: false));
                    return persisted!;
                });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(published[0].Title, Does.Contain("First meridian flip"),
                "the announce fires BEFORE any flip state is touched");
            Assert.That(published[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(persisted, Is.Not.Null, "the window elapsing counts as consent");
            Assert.That(persisted!.FirstFlipConfirmed, Is.True,
                "subsequent flips must run without the announce");
        }

        [Test]
        public async Task A_confirmed_profile_flips_without_the_announce() {
            SetupProfile(recenter: false);
            SetupSafety(Safety());   // firstFlipConfirmed defaults true in the test helper
            SetupHealthyTrackingMount();
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(published, Is.Empty, "a confirmed profile's flip is silent");
            profileStore.Verify(p => p.UpdateSafetyPolicies(It.IsAny<Func<SafetyPoliciesDto, SafetyPoliciesDto?>>()), Times.Never);
        }

        [Test]
        public async Task The_announce_is_independent_of_the_flip_safety_toggle() {
            // §58.8 is its own net — a rig that turned the §58.9 layers off (misreporting pier
            // side) still gets the one-time first-flip announce.
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false, firstFlipConfirmed: false));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(published.Single().Title, Does.Contain("First meridian flip"));
        }

        [Test]
        public void Stopping_the_sequence_during_the_window_leaves_the_flag_unconfirmed() {
            // The user's "no" is cancellation: it propagates as CANCELLED (not a failed flip),
            // nothing was touched, and the flag stays false so the next attempt announces again.
            SetupProfile(recenter: true);
            SetupSafety(Safety(firstFlipConfirmed: false));
            SetupHealthyTrackingMount();
            var sut = CreateSafetySUT();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(
                () => sut.MeridianFlip(SafeTarget, TimeSpan.Zero, Progress, cts.Token));
            profileStore.Verify(p => p.UpdateSafetyPolicies(It.IsAny<Func<SafetyPoliciesDto, SafetyPoliciesDto?>>()), Times.Never,
                "an unconsented window must not confirm");
            telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never,
                "the announce runs before any state is touched");
        }

        // ─── §58.7 — failure notifications on the BASELINE (safety-off) paths ───

        [Test]
        public async Task A_failed_flip_notifies_critically_even_without_the_safety_layers() {
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false));
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            var n = published.Single();
            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Critical),
                "an unattended sequence just halted — the user must hear it in BOTH modes");
            Assert.That(n.Message, Does.Contain("halted"));
            Assert.That(n.Message, Does.Not.Contain("could NOT be resumed"),
                "the guider resume succeeded here, so the message must not cry wolf");
        }

        [Test]
        public async Task An_early_failure_does_not_claim_a_tracking_restore_or_an_unresumed_guider() {
            // StopAutoguider throws BEFORE PassMeridian: tracking was never disabled and guiding
            // was never stopped — the message must not claim a restore that never ran, nor cry
            // wolf about a guider that was never stopped in the first place.
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false));
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("PHD2 hiccup"));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            telescope.Verify(t => t.SetTrackingEnabled(It.IsAny<bool>()), Times.Never,
                "tracking was never touched — no disable, no restore");
            var n = published.Single();
            Assert.That(n.Message, Does.Contain("never disabled"));
            Assert.That(n.Message, Does.Not.Contain("could NOT be resumed"));
        }

        [Test]
        public async Task The_failure_notification_reports_an_unresumable_guider() {
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false));
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            guider.Setup(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("PHD2 is gone"));
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(published.Single().Message, Does.Contain("could NOT be resumed"),
                "'restored but unguided' and 'restored' are different mornings");
        }

        [Test]
        public async Task A_best_effort_recenter_failure_notifies_but_does_not_fail_the_flip() {
            SetupProfile(recenter: true);
            SetupSafety(Safety(enabled: false));
            centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>>(),
                    It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            var sut = CreateSafetySUT();

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True, "without the safety layers re-centering stays best-effort");
            var n = published.Single();
            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Error));
            Assert.That(n.Message, Does.Contain("unverified pointing"));
        }

        [Test]
        public async Task A_failed_post_flip_autofocus_notifies_a_warning_and_continues() {
            SetupProfile(recenter: false, autoFocus: true);
            SetupSafety(Safety(enabled: false));
            var autofocus = new Mock<OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor>();
            autofocus.Setup(a => a.RunAutofocusAsync(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var sut = new MeridianFlipExecutor(profileService.Object, telescope.Object, guider.Object,
                centering.Object, dome.Object, domeFollower.Object, profileStore.Object,
                notifications.Object, reconnector.Object, camera.Object, focuser.Object, autofocus.Object);

            var ok = await sut.MeridianFlip(Target, TimeSpan.Zero, Progress, CancellationToken.None);

            Assert.That(ok, Is.True, "per §58.7 a re-focus failure never aborts the night");
            var n = published.Single();
            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(n.Message, Does.Contain("focus may have drifted"));
        }

        /// <summary>A healthy tracking mount whose pier side flips east→west when MeridianFlip is
        /// called — so safety tests that should PASS the pier-side gate do.</summary>
        private void SetupHealthyTrackingMount() {
            var side = PierSide.pierEast;
            telescope.Setup(t => t.GetInfo()).Returns(() => new TelescopeInfo {
                Connected = true, TrackingEnabled = true, AtPark = false, Slewing = false,
                SideOfPier = side, RightAscension = 5, Declination = 20,
            });
            telescope.Setup(t => t.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => { callLog.Add("Flip"); side = PierSide.pierWest; })
                .ReturnsAsync(true);
        }
    }
}
