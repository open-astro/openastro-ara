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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §58.12 — <see cref="UnattendedShutdownService"/>: the awaiting-user
    /// countdown, the "user came back" cancellation, the stale-state guard, and
    /// the graceful-shutdown ladder over fully mocked equipment. All waits are
    /// compressed to milliseconds through the internal knobs.
    /// </summary>
    [TestFixture]
    public class UnattendedShutdownServiceTest {

        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<IGuiderMediator> guider = null!;
        private Mock<ITelescopeMediator> telescope = null!;
        private Mock<ICameraService> camera = null!;
        private Mock<IFilterWheelService> filterWheel = null!;
        private Mock<IFocuserService> focuser = null!;
        private Mock<IRotatorService> rotator = null!;
        private Mock<IFlatDeviceService> flatDevice = null!;
        private Mock<INotificationService> notifications = null!;
        private List<NotificationDto> published = null!;
        private List<(bool on, double? target)> coolerCalls = null!;

        private static readonly Guid SeqId = Guid.NewGuid();
        private static readonly Guid RunId = Guid.NewGuid();

        private static SafetyPoliciesDto Safety(bool enabled = true, int waitMinutes = 10) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: false, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                UnattendedShutdownEnabled: enabled, UnattendedShutdownWaitMinutes: waitMinutes);

        private static SequenceRunStateDto RunState(SequenceRunState state, Guid? runId = null) =>
            new(SeqId, runId ?? RunId, state, null, null, DateTimeOffset.UtcNow, null, 0, 2, null);

        private static CameraDto Camera(bool coolerOn, double? temperature) =>
            new("cam-1", "Test Cam", EquipmentConnectionState.Connected, null,
                new CameraStateDto("idle", temperature, 80, coolerOn, null));

        [SetUp]
        public void SetUp() {
            profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(Safety());
            profiles.Setup(p => p.GetImagingDefaults()).Returns(new ImagingDefaultsDto(
                ExposureSeconds: 60, Gain: 100, Offset: 10, Bin: 1, FrameKind: "light",
                CoolerTargetC: -10, CoolerRampCPerMin: 5, WarmupAtSessionEnd: false));

            sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.GetRunStateAsync(SeqId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(RunState(SequenceRunState.PausedAwaitingUser));

            guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(true);

            telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo {
                Connected = true, CanPark = true, AtPark = false, TrackingEnabled = true,
            });
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            coolerCalls = new List<(bool, double?)>();
            camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Camera(coolerOn: true, temperature: -10));
            camera.Setup(c => c.SetCoolerAsync(It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .Callback<bool, double?, CancellationToken>((on, t, _) => {
                    lock (coolerCalls) coolerCalls.Add((on, t));
                })
                .Returns(Task.CompletedTask);
            camera.Setup(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "camera.disconnect", DateTimeOffset.UtcNow, null));

            static Mock<T> Disconnectable<T>() where T : class {
                var m = new Mock<T>();
                return m;
            }
            filterWheel = Disconnectable<IFilterWheelService>();
            filterWheel.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "filterwheel.disconnect", DateTimeOffset.UtcNow, null));
            focuser = Disconnectable<IFocuserService>();
            focuser.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "focuser.disconnect", DateTimeOffset.UtcNow, null));
            rotator = Disconnectable<IRotatorService>();
            rotator.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "rotator.disconnect", DateTimeOffset.UtcNow, null));
            flatDevice = Disconnectable<IFlatDeviceService>();
            flatDevice.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "flatpanel.disconnect", DateTimeOffset.UtcNow, null));

            published = new List<NotificationDto>();
            notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => {
                    lock (published) published.Add(n);
                })
                .Returns(Task.CompletedTask);
        }

        private UnattendedShutdownService CreateSUT() =>
            new(profiles.Object, () => sequencer.Object, guider.Object, telescope.Object,
                camera.Object, filterWheel.Object, focuser.Object, rotator.Object,
                flatDevice.Object, notifications.Object) {
                // Compress every wait: the countdown's "minutes" become 50 ms
                // each, and the warm-up's per-minute steps tick every 10 ms.
                WaitFromMinutes = _ => TimeSpan.FromMilliseconds(50),
                WarmTimeScale = 6000, // 1 "minute" = 10 ms
                WarmHardCap = TimeSpan.FromSeconds(5),
                ParkTimeout = TimeSpan.FromSeconds(2),
            };

        private static async Task WaitUntilAsync(Func<bool> condition, string reason) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.That(condition(), Is.True, reason);
        }

        [Test]
        public async Task Elapsed_countdown_runs_the_full_ladder_in_order() {
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            Assert.That(sut.IsCountingDown, Is.True);

            await WaitUntilAsync(() => { lock (published) return published.Count > 0; },
                "the summary notification marks the ladder's completion");

            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
            filterWheel.Verify(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            focuser.Verify(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            rotator.Verify(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            flatDevice.Verify(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

            // The warm-up ramped: set-points climb from -10 toward +10 and the
            // LAST cooler call switches it off (camera disconnect only after).
            lock (coolerCalls) {
                Assert.That(coolerCalls, Is.Not.Empty);
                Assert.That(coolerCalls[^1].on, Is.False, "cooler ends OFF");
                var setpoints = coolerCalls.FindAll(c => c.on).ConvertAll(c => c.target!.Value);
                Assert.That(setpoints, Is.Ordered.Ascending, "the set-point only ever climbs");
                Assert.That(setpoints[^1], Is.EqualTo(UnattendedShutdownService.WarmTargetC).Within(0.001));
            }

            var n = published[0];
            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Warning),
                "stable now — a Critical would re-trigger the §35.5 alarm the shutdown resolved");
            Assert.That(n.Message, Does.Contain("Mount parked"));
            Assert.That(n.Message, Does.Contain("Camera disconnected"));
            Assert.That(sut.IsCountingDown, Is.False, "the countdown disarmed itself");
        }

        [Test]
        public async Task User_activity_cancels_the_countdown_before_it_fires() {
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            Assert.That(sut.IsCountingDown, Is.True);

            sut.NotifyUserActivity("sequencer.resume");
            Assert.That(sut.IsCountingDown, Is.False);

            // Give the (cancelled) worker ample time to have fired if it were going to.
            await Task.Delay(300);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published, Is.Empty);
        }

        [Test]
        public async Task Disabled_policy_never_starts_a_countdown() {
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(Safety(enabled: false));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            Assert.That(sut.IsCountingDown, Is.False);
            await Task.Delay(200);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Stale_run_state_after_the_window_declines_to_shut_down() {
            // A resume raced the timer's last tick without routing through
            // NotifyUserActivity: the re-check must catch it and do NOTHING —
            // a wrong shutdown tears down a rig that resumed imaging.
            sequencer.Setup(s => s.GetRunStateAsync(SeqId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(RunState(SequenceRunState.Running));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);

            await WaitUntilAsync(() => !sut.IsCountingDown, "the window elapses");
            await Task.Delay(200);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published, Is.Empty);
        }

        [Test]
        public async Task A_different_run_in_the_slot_after_the_window_declines_to_shut_down() {
            // Same sequence restarted (new RunId) and re-suspended: the elapsed
            // countdown belongs to the OLD run — its window must not shut the
            // new run's rig down; the new run's own entry starts a fresh clock.
            sequencer.Setup(s => s.GetRunStateAsync(SeqId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(RunState(SequenceRunState.PausedAwaitingUser, runId: Guid.NewGuid()));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);

            await WaitUntilAsync(() => !sut.IsCountingDown, "the window elapses");
            await Task.Delay(200);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(published, Is.Empty);
        }

        [Test]
        public void A_second_awaiting_user_entry_joins_the_running_countdown() {
            // Use a long wait so the first countdown is still pending.
            using var sut = CreateSUT();
            sut.WaitFromMinutes = _ => TimeSpan.FromMinutes(10);
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            sut.NotifyRunPausedAwaitingUser(SeqId, Guid.NewGuid()); // second failure — same absent user
            Assert.That(sut.IsCountingDown, Is.True);
            sut.NotifyUserActivity("test-teardown");
        }

        [Test]
        public async Task Park_incapable_mount_gets_tracking_stopped_instead() {
            telescope.Setup(t => t.GetInfo()).Returns(new TelescopeInfo {
                Connected = true, CanPark = false, AtPark = false, TrackingEnabled = true,
            });
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, "ladder completes");

            telescope.Verify(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.SetTrackingEnabled(false), Times.Once);
            Assert.That(published[0].Message, Does.Contain("tracking stopped"));
        }

        [Test]
        public async Task A_park_that_THROWS_still_gets_tracking_stopped() {
            // r1 — the 90 s park cap fires as an OperationCanceledException, not
            // a false return; the fallback must still run (a tracking,
            // unattended mount is the exact outcome this feature prevents).
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException("park timed out"));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, "ladder completes");

            telescope.Verify(t => t.SetTrackingEnabled(false), Times.Once);
            Assert.That(published[0].Message, Does.Contain("tracking stopped instead"));
        }

        [Test]
        public async Task A_new_awaiting_user_entry_during_a_running_ladder_is_dropped() {
            // r1 — the disarm happens before the (up to ~30 min) ladder; a
            // second failure landing in that window must NOT start a second
            // countdown/ladder against the same shared rig. Stall the ladder at
            // the warm-up read to hold it mid-flight deterministically.
            var gate = new TaskCompletionSource<CameraDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            await WaitUntilAsync(
                () => guider.Invocations.Count > 0,
                "the ladder is mid-flight (guider step reached)");

            sut.NotifyRunPausedAwaitingUser(SeqId, Guid.NewGuid());
            Assert.That(sut.IsCountingDown, Is.False,
                "no second clock while the first ladder still runs");

            gate.SetResult(Camera(coolerOn: false, temperature: 15)); // release
            await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, "first ladder completes");
            await Task.Delay(200);
            guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once);
            lock (published) Assert.That(published, Has.Count.EqualTo(1), "exactly one ladder ran");
        }

        [Test]
        public async Task A_dead_device_mid_ladder_does_not_stop_the_rest() {
            focuser.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("focuser driver crashed"));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, "ladder completes");

            // Everything after the dead focuser still ran.
            rotator.Verify(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(published[0].Message, Does.Contain("Focuser disconnect FAILED"));
        }

        [Test]
        public async Task Cooler_already_off_skips_the_ramp_but_camera_still_disconnects() {
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Camera(coolerOn: false, temperature: 15));
            using var sut = CreateSUT();
            sut.NotifyRunPausedAwaitingUser(SeqId, RunId);
            await WaitUntilAsync(() => { lock (published) return published.Count > 0; }, "ladder completes");

            lock (coolerCalls) Assert.That(coolerCalls, Is.Empty, "nothing to warm");
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
