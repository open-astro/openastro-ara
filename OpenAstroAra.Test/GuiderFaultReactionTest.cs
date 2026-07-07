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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §42.2 — the mid-sequence guider fault flow on <see cref="GuiderService"/>:
    /// the on_guider_lost token mapping, the per-policy sequencer action
    /// (pause/skip/abort), the no-sequence-running quiet path, and the
    /// notification/WS surfacing. Drives the internal reaction directly with
    /// mocked collaborators (the socket-drop trigger itself is covered by the
    /// bench <c>GuiderFakeIntegrationTest</c> connection-loss scenario).
    /// </summary>
    [TestFixture]
    public class GuiderFaultReactionTest {

        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private List<string> publishedEvents = null!;
        private List<NotificationDto> postedNotifications = null!;
        private GuiderService service = null!;

        [SetUp]
        public void SetUp() {
            profiles = new Mock<IProfileStore>();
            sequencer = new Mock<ISequencerService>();
            notifications = new Mock<INotificationService>();
            ws = new Mock<IWsBroadcaster>();
            publishedEvents = new List<string>();
            postedNotifications = new List<NotificationDto>();

            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (publishedEvents) { publishedEvents.Add(t); } })
                .Returns(Task.CompletedTask);
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (postedNotifications) { postedNotifications.Add(n); } })
                .Returns(Task.CompletedTask);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid>());
            sequencer.Setup(s => s.SkipActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
            SetPolicy("pause_and_retry");

            service = new GuiderService(
                new HeadlessProfileService(),
                new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                    Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                NullLogger<GuiderService>.Instance,
                Mock.Of<IGuiderProcessSupervisor>(),
                ws.Object,
                profiles.Object,
                () => sequencer.Object,
                notifications.Object);
        }

        [TearDown]
        public void TearDown() => service.Dispose();

        private void SetPolicy(string onGuiderLost) =>
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: onGuiderLost,
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));

        [Test]
        public void ParseGuiderLostAction_maps_tokens_with_protective_fallback() {
            Assert.Multiple(() => {
                Assert.That(GuiderService.ParseGuiderLostAction("pause_and_retry"), Is.EqualTo(GuiderService.GuiderLostAction.PauseAndRetry));
                Assert.That(GuiderService.ParseGuiderLostAction("skip_target"), Is.EqualTo(GuiderService.GuiderLostAction.SkipTarget));
                Assert.That(GuiderService.ParseGuiderLostAction("abort_sequence"), Is.EqualTo(GuiderService.GuiderLostAction.AbortSequence));
                Assert.That(GuiderService.ParseGuiderLostAction("garbage"), Is.EqualTo(GuiderService.GuiderLostAction.PauseAndRetry));
                Assert.That(GuiderService.ParseGuiderLostAction(null), Is.EqualTo(GuiderService.GuiderLostAction.PauseAndRetry));
            });
        }

        [Test]
        public async Task Pause_and_retry_pauses_the_active_runs_and_notifies() {
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            await service.ReactToGuidingLossAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.SkipActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(publishedEvents, Does.Contain("guider.fault_action_taken"));
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(postedNotifications[0].Title, Does.Contain("paused"));
        }

        [Test]
        public async Task Abort_sequence_aborts_instead_of_pausing() {
            SetPolicy("abort_sequence");
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            await service.ReactToGuidingLossAsync();

            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(postedNotifications[0].Title, Does.Contain("aborted"));
        }

        [Test]
        public async Task Skip_target_skips_the_current_instructions() {
            SetPolicy("skip_target");
            sequencer.Setup(s => s.SkipActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            await service.ReactToGuidingLossAsync();

            sequencer.Verify(s => s.SkipActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(postedNotifications[0].Title, Does.Contain("skipped"));
        }

        [Test]
        public async Task No_running_sequence_stays_quiet() {
            // The §63.3 recovery posts its own crash notifications; with nothing
            // running, a sequence-action notification would be pure noise.
            await service.ReactToGuidingLossAsync();

            Assert.That(publishedEvents, Is.Empty);
            Assert.That(postedNotifications, Is.Empty);
        }

        [Test]
        public async Task Unknown_token_falls_back_to_pause() {
            SetPolicy("do_a_barrel_roll");
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            await service.ReactToGuidingLossAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Profile_read_fault_still_pauses_with_the_default() {
            profiles.Setup(p => p.GetSafetyPolicies()).Throws(new InvalidOperationException("store fault"));
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            await service.ReactToGuidingLossAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
        }
    }
}
