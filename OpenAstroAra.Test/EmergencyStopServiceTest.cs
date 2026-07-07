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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §35.3 — <see cref="EmergencyStopService"/>: the stop ladder (abort runs
    /// → abort exposure → stop guiding → park → flat light off), best-effort
    /// rung isolation (one dead device never blocks the rest), the honest
    /// result DTO, the event-before-action ordering, headless null-dep
    /// tolerance, and the single-flight double-press guard.
    /// </summary>
    [TestFixture]
    public class EmergencyStopServiceTest {

        private Mock<ICameraService> camera = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<IGuiderService> guider = null!;
        private Mock<ITelescopeService> telescope = null!;
        private Mock<IFlatDeviceService> flat = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private List<string> publishedEvents = null!;
        private List<NotificationDto> postedNotifications = null!;

        private static readonly OperationAcceptedDto Accepted =
            new(Guid.NewGuid(), "test", DateTimeOffset.UtcNow, null);

        [SetUp]
        public void SetUp() {
            camera = new Mock<ICameraService>();
            sequencer = new Mock<ISequencerService>();
            guider = new Mock<IGuiderService>();
            telescope = new Mock<ITelescopeService>();
            flat = new Mock<IFlatDeviceService>();
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
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            guider.Setup(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            telescope.Setup(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            flat.Setup(f => f.ApplyFlatPanelAsync(It.IsAny<FlatPanelRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
        }

        private EmergencyStopService Build() => new(
            camera.Object, () => sequencer.Object, guider.Object,
            telescope.Object, flat.Object, notifications.Object, ws.Object);

        [Test]
        public async Task Execute_RunsEveryRung_AndReportsHonestly() {
            var result = await Build().ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.AlreadyInProgress, Is.False);
                Assert.That(result.RunsAborted, Is.EqualTo(1));
                Assert.That(result.ExposureAborted, Is.True);
                Assert.That(result.GuidingStopped, Is.True);
                Assert.That(result.ParkRequested, Is.True);
                Assert.That(result.FlatPanelLightOff, Is.True);
            });
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            camera.Verify(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()), Times.Once);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.ParkAsync(
                It.Is<ParkRequestDto>(r => r.Reason == "emergency_stop"), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            flat.Verify(f => f.ApplyFlatPanelAsync(
                It.Is<FlatPanelRequestDto>(r => r.LightOn == false && r.OpenCover == null), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_EmitsStopEventBeforeActionTaken_AndPostsCriticalNotification() {
            await Build().ExecuteAsync();

            Assert.That(publishedEvents, Is.EqualTo(new[] {
                WsEventCatalog.SafetyEmergencyStop,
                WsEventCatalog.SafetyActionTaken,
            }), "the stop event must fire BEFORE the rungs so the client can alarm while the daemon works");
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(postedNotifications[0].Category, Is.EqualTo(NotificationCategory.Safety));
        }

        [Test]
        public async Task Execute_ADeadMount_NeverBlocksTheRemainingRungs() {
            telescope.Setup(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("mount driver hung"));

            var result = await Build().ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.ParkRequested, Is.False, "the result reports the failed rung honestly");
                Assert.That(result.FlatPanelLightOff, Is.True, "the rung AFTER the dead device still runs");
                Assert.That(result.GuidingStopped, Is.True);
            });
            flat.Verify(f => f.ApplyFlatPanelAsync(It.IsAny<FlatPanelRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_Headless_AllDepsNull_CompletesWithAllFalse() {
            var service = new EmergencyStopService();

            var result = await service.ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.AlreadyInProgress, Is.False);
                Assert.That(result.RunsAborted, Is.EqualTo(0));
                Assert.That(result.ExposureAborted, Is.False);
                Assert.That(result.GuidingStopped, Is.False);
                Assert.That(result.ParkRequested, Is.False);
                Assert.That(result.FlatPanelLightOff, Is.False);
            });
        }

        [Test]
        public async Task Execute_ASecondTriggerMidFlight_ReturnsAlreadyInProgress_WithoutASecondVolley() {
            var firstAbortStarted = new TaskCompletionSource();
            var releaseAbort = new TaskCompletionSource();
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()))
                .Returns(async () => {
                    firstAbortStarted.TrySetResult();
                    await releaseAbort.Task.ConfigureAwait(false);
                });
            var service = Build();

            var first = service.ExecuteAsync();
            await firstAbortStarted.Task.ConfigureAwait(false);

            var second = await service.ExecuteAsync().ConfigureAwait(false);
            Assert.That(second.AlreadyInProgress, Is.True);

            releaseAbort.TrySetResult();
            var result = await first.ConfigureAwait(false);
            Assert.That(result.AlreadyInProgress, Is.False);
            camera.Verify(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()), Times.Once,
                "the double-press must not run a second full ladder");

            // The gate re-opens once the first pass finishes.
            var third = await service.ExecuteAsync().ConfigureAwait(false);
            Assert.That(third.AlreadyInProgress, Is.False);
        }

        [Test]
        public async Task Execute_NoRunningSequence_ReportsZeroAborted() {
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

            var result = await Build().ExecuteAsync();

            Assert.That(result.RunsAborted, Is.EqualTo(0));
            Assert.That(result.ParkRequested, Is.True, "an idle rig still parks — the point is 'nothing moves'");
        }
    }
}
