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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.3 — the fault-reaction episode machine: pause-first for sequence-critical
    /// devices, the hot-reconnect ladder, resume-on-recovery, terminal actions on give-up,
    /// and the one-episode-per-device-type discipline.</summary>
    [TestFixture]
    public class FaultReactionServiceTest {

        private Mock<IEquipmentReconnector> reconnector = null!;
        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<ITelescopeService> telescope = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private List<(string Type, JsonElement Payload)> published = null!;
        private List<NotificationDto> posted = null!;
        private FaultReactionService service = null!;

        private static readonly OperationAcceptedDto Accepted =
            new(Guid.NewGuid(), "test", DateTimeOffset.UtcNow, null);

        private static readonly string[] PauseReconnectRecover = ["sequence_paused", "reconnecting", "recovered"];
        private static readonly string[] ReconnectRecover = ["reconnecting", "recovered"];
        private static readonly string[] NotifyOnly = ["notify_only"];

        [SetUp]
        public void SetUp() {
            reconnector = new Mock<IEquipmentReconnector>();
            profiles = new Mock<IProfileStore>();
            sequencer = new Mock<ISequencerService>();
            telescope = new Mock<ITelescopeService>();
            notifications = new Mock<INotificationService>();
            ws = new Mock<IWsBroadcaster>();
            published = new List<(string, JsonElement)>();
            posted = new List<NotificationDto>();

            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => { lock (published) { published.Add((t, p)); } })
                .Returns(Task.CompletedTask);
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (posted) { posted.Add(n); } })
                .Returns(Task.CompletedTask);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid>());
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
            telescope.Setup(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            telescope.Setup(t => t.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            SetPolicy();

            service = new FaultReactionService(
                hub: null, reconnector.Object, profiles.Object, () => sequencer.Object,
                telescope.Object, notifications.Object, ws.Object) {
                DelayShaper = _ => TimeSpan.Zero,             // collapse the ladder for tests
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(200),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
            };
        }

        [TearDown]
        public void TearDown() => service.Dispose();

        private void SetPolicy(bool hotReconnect = true, string onCameraLost = "reconnect_then_pause",
                string onMountLost = "reconnect_then_abort_park") =>
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 1,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause_sequence", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false,
                HotReconnectEnabled: hotReconnect, OnCameraLost: onCameraLost, OnMountLost: onMountLost));

        private static EquipmentFaultEvent Fault(DeviceType type = DeviceType.Camera,
                EquipmentFaultKind kind = EquipmentFaultKind.Disconnected) =>
            new(type, "dev-1", "Test Device", kind, "3 probes failed",
                new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero));

        private void ReconnectSucceeds(DeviceType type) {
            reconnector.Setup(r => r.ReconnectAsync(type, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            reconnector.Setup(r => r.GetConnectionStateAsync(type, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Connected);
        }

        private void ReconnectAlwaysFails(DeviceType type) {
            reconnector.Setup(r => r.ReconnectAsync(type, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            reconnector.Setup(r => r.GetConnectionStateAsync(type, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Error);
        }

        private List<string> Actions() {
            lock (published) {
                return published
                    .Where(e => e.Type == WsEventCatalog.EquipmentFaultActionTaken)
                    .Select(e => e.Payload.GetProperty("action").GetString()!)
                    .ToList();
            }
        }

        private JsonElement ActionPayload(string action) {
            lock (published) {
                return published.First(e => e.Type == WsEventCatalog.EquipmentFaultActionTaken
                    && e.Payload.GetProperty("action").GetString() == action).Payload;
            }
        }

        [Test]
        public async Task A_lost_camera_pauses_the_sequence_reconnects_and_resumes() {
            var run = Guid.NewGuid();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { run });
            sequencer.Setup(s => s.ResumeRunsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(run)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            ReconnectSucceeds(DeviceType.Camera);

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            Assert.That(Actions(), Is.EqualTo(PauseReconnectRecover),
                "pause FIRST, then the ladder, then resume");
            Assert.That(ActionPayload("recovered").GetProperty("resumed_runs").GetInt32(), Is.EqualTo(1));
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(posted.Select(n => n.Severity), Does.Contain(NotificationSeverity.Info),
                "recovery posts the all-clear");
        }

        [Test]
        public async Task An_unrecovered_camera_exhausts_the_ladder_and_stays_paused() {
            ReconnectAlwaysFails(DeviceType.Camera);

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            Assert.That(Actions(), Does.Contain("gave_up"));
            Assert.That(ActionPayload("gave_up").GetProperty("terminal").GetString(), Is.EqualTo("pause_sequence"));
            Assert.That(ActionPayload("gave_up").GetProperty("attempts").GetInt32(), Is.EqualTo(5));
            reconnector.Verify(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()), Times.Exactly(5),
                "every rung of the 0/5/15/30/60 ladder ran");
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never,
                "a dead camera must leave the sequence paused");
            Assert.That(posted.Select(n => n.Severity), Does.Contain(NotificationSeverity.Error));
        }

        [Test]
        public async Task An_unrecovered_mount_aborts_and_best_effort_parks() {
            ReconnectAlwaysFails(DeviceType.Telescope);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            service.OnFault(Fault(DeviceType.Telescope));
            await service.WhenIdleAsync();

            var gaveUp = ActionPayload("gave_up");
            Assert.That(gaveUp.GetProperty("terminal").GetString(), Is.EqualTo("abort_and_park"));
            Assert.That(gaveUp.GetProperty("aborted_runs").GetInt32(), Is.EqualTo(1));
            Assert.That(gaveUp.GetProperty("parked").GetBoolean(), Is.True);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task A_failed_park_is_reported_not_thrown() {
            ReconnectAlwaysFails(DeviceType.Telescope);
            telescope.Setup(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("mount is gone"));

            service.OnFault(Fault(DeviceType.Telescope));
            await service.WhenIdleAsync();

            Assert.That(ActionPayload("gave_up").GetProperty("parked").GetBoolean(), Is.False,
                "the usual §42.3 case — a mount that never reconnected can't park either");
        }

        [Test]
        public async Task Lost_tracking_reenables_tracking_instead_of_reconnecting() {
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null,
                    new TelescopeStateDto("tracking", null, null, Tracking: true, Parked: false, AtHome: false)));

            service.OnFault(Fault(DeviceType.Telescope, EquipmentFaultKind.TrackingLost));
            await service.WhenIdleAsync();

            telescope.Verify(t => t.SetTrackingAsync(true, It.IsAny<CancellationToken>()), Times.Once);
            reconnector.Verify(r => r.ReconnectAsync(It.IsAny<DeviceType>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(Actions(), Does.Contain("recovered"));
        }

        [Test]
        public async Task A_guider_fault_is_left_entirely_to_the_guider_service() {
            service.OnFault(Fault(DeviceType.Guider));
            await service.WhenIdleAsync();

            Assert.That(published, Is.Empty, "no reaction events — GuiderService.FaultReaction owns it");
            Assert.That(posted, Is.Empty);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Notify_only_policy_posts_the_notification_and_touches_nothing() {
            SetPolicy(onCameraLost: "notify_only");

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            Assert.That(Actions(), Is.EqualTo(NotifyOnly));
            Assert.That(posted, Has.Count.EqualTo(1));
            reconnector.Verify(r => r.ReconnectAsync(It.IsAny<DeviceType>(), It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Hot_reconnect_off_goes_straight_to_the_terminal_action() {
            SetPolicy(hotReconnect: false);
            var run = Guid.NewGuid();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { run });

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            reconnector.Verify(r => r.ReconnectAsync(It.IsAny<DeviceType>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(ActionPayload("gave_up").GetProperty("terminal").GetString(), Is.EqualTo("pause_sequence"));
        }

        [Test]
        public async Task A_second_fault_during_an_active_episode_folds_into_it() {
            var firstAttemptStarted = new TaskCompletionSource();
            var releaseFirst = new TaskCompletionSource();
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .Returns(async (DeviceType _, CancellationToken _) => {
                    firstAttemptStarted.TrySetResult();
                    await releaseFirst.Task;
                    return new ReconnectOutcome(1, 1);
                });
            reconnector.Setup(r => r.GetConnectionStateAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Connected);

            service.OnFault(Fault());
            await firstAttemptStarted.Task;
            service.OnFault(Fault()); // detection re-fired while the episode is still recovering
            releaseFirst.SetResult();
            await service.WhenIdleAsync();

            Assert.That(Actions().Count(a => a == "reconnecting"), Is.EqualTo(1), "one episode, not two");
            reconnector.Verify(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task A_throwing_sequencer_does_not_kill_the_episode() {
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("sequencer down"));
            ReconnectAlwaysFails(DeviceType.Camera);

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            Assert.That(Actions(), Does.Contain("gave_up"), "the ladder and terminal still ran");
        }

        [Test]
        public async Task A_lost_peripheral_reconnects_without_touching_the_sequence() {
            ReconnectSucceeds(DeviceType.Focuser);

            service.OnFault(Fault(DeviceType.Focuser));
            await service.WhenIdleAsync();

            Assert.That(Actions(), Is.EqualTo(ReconnectRecover), "no pause for a peripheral");
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task An_unrecovered_peripheral_gives_up_with_a_warning_and_no_terminal_action() {
            ReconnectAlwaysFails(DeviceType.Focuser);

            service.OnFault(Fault(DeviceType.Focuser));
            await service.WhenIdleAsync();

            Assert.That(ActionPayload("gave_up").GetProperty("terminal").GetString(), Is.EqualTo("none"));
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(posted.Last().Severity, Is.EqualTo(NotificationSeverity.Warning),
                "a peripheral that stays gone is a warning, not an error — the instruction that needs it will fail loudly");
        }

        [Test]
        public async Task Recovery_on_a_later_rung_reports_the_attempt_number() {
            var calls = 0;
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ReconnectOutcome(1, ++calls >= 3 ? 1 : 0));
            reconnector.Setup(r => r.GetConnectionStateAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Connected);

            service.OnFault(Fault());
            await service.WhenIdleAsync();

            Assert.That(ActionPayload("recovered").GetProperty("attempts").GetInt32(), Is.EqualTo(3));
        }
    }
}
