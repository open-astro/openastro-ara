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
    /// → abort exposure → stop guiding → stop mount → flat light off), best-effort
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

        private static readonly string[] StopMountFailed = { "stop_mount" };
        private static readonly string[] AbortThenTrackingOff = { "abort", "tracking_off" };

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
            // Connected by default — the rung probes cached connection state
            // and treats a disconnected device as not-available, not failed.
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CameraDto("c1", "Cam", EquipmentConnectionState.Connected, null, null!));
            guider.Setup(g => g.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GuiderDto("g1", "PHD2", EquipmentConnectionState.Connected, null!));
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null, null!));
            flat.Setup(f => f.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlatDeviceDto("f1", "Panel", EquipmentConnectionState.Connected, null!));
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            guider.Setup(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
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
                Assert.That(result.MountStopped, Is.True);
                Assert.That(result.FlatPanelLightOff, Is.True);
                Assert.That(result.FailedRungs, Is.Empty);
            });
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            camera.Verify(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()), Times.Once);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            flat.Verify(f => f.ApplyFlatPanelAsync(
                It.Is<FlatPanelRequestDto>(r => r.LightOn == false && r.OpenCover == null), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_StopsARunningGuideCameraFocusLoop() {
            // The loop takes the guide camera's frames through the guider; stopping guiding does not end it.
            var focus = new Mock<IGuideFocusService>();
            focus.SetupGet(f => f.IsActive).Returns(true);
            focus.Setup(f => f.StopAsync()).Returns(Task.CompletedTask);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                guideFocus: focus.Object).ExecuteAsync();

            focus.Verify(f => f.StopAsync(), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_StopsARunningBahtinovReadout() {
            var bahtinov = new Mock<IBahtinovFocusService>();
            bahtinov.SetupGet(b => b.IsActive).Returns(true);
            bahtinov.Setup(b => b.StopAsync()).Returns(Task.CompletedTask);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                bahtinov: bahtinov.Object).ExecuteAsync();

            bahtinov.Verify(b => b.StopAsync(), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_CancelsARunningAutofocus() {
            var tracker = new AutofocusRunTracker();
            using var run = new CancellationTokenSource();
            tracker.Begin("classic", 100, 5, null, null, run);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                autofocus: tracker).ExecuteAsync();

            Assert.That(run.IsCancellationRequested, Is.True);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_StopsPolarAlignment() {
            var polar = new Mock<IPolarAlignService>();
            polar.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PolarAlignStateDto("adjusting", 12, 10, 2, 5, null));
            polar.Setup(p => p.IsActive).Returns(true);
            polar.Setup(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAlign: polar.Object).ExecuteAsync();

            polar.Verify(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_StopsPolarAlignment_InTheCheckStageToo() {
            // #1591 added homing and checking: a mount mid-Find-Home or between two check turns must stop
            // like one in the adjust loop (the rung asks IsActive, not a list of state names).
            var polar = new Mock<IPolarAlignService>();
            polar.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PolarAlignStateDto("checking", null, null, null, 0, null));
            polar.Setup(p => p.IsActive).Returns(true);
            polar.Setup(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAlign: polar.Object).ExecuteAsync();

            polar.Verify(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }


        [Test]
        public async Task Execute_AFinishedPolarAlignment_IsNotReset() {
            // A stray Stop publishes "stopped", which clears the client's aligned result.
            var polar = new Mock<IPolarAlignService>();
            polar.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PolarAlignStateDto("stopped", 2.8, 2.3, 1.5, 99, null));
            polar.Setup(p => p.IsActive).Returns(false);

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAlign: polar.Object).ExecuteAsync();

            polar.Verify(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_AnIdleGuideCameraFocusLoop_IsNotPoked() {
            var focus = new Mock<IGuideFocusService>();
            focus.SetupGet(f => f.IsActive).Returns(false);

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                guideFocus: focus.Object).ExecuteAsync();

            focus.Verify(f => f.StopAsync(), Times.Never);
        }

        [Test]
        public async Task Execute_HaltsAConnectedRotator() {
            // A rotator turning when Stop is pressed (from Simple Mode, the full UI or a plan's
            // rotate) must stop where it is: aborting the run only stops the sequencer waiting.
            var rotator = new Mock<IRotatorService>();
            rotator.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RotatorDto("r1", "Rotator", EquipmentConnectionState.Connected, null,
                    new RotatorStateDto("moving", 88, 92, false)));
            rotator.Setup(r => r.HaltAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                rotator: rotator.Object).ExecuteAsync();

            rotator.Verify(r => r.HaltAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_ARotatorThatRefusesToHalt_IsCalledOut_AndTheLadderGoesOn() {
            var rotator = new Mock<IRotatorService>();
            rotator.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RotatorDto("r1", "Rotator", EquipmentConnectionState.Connected, null,
                    new RotatorStateDto("moving", 88, 92, false)));
            rotator.Setup(r => r.HaltAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("rotator driver hung"));

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                rotator: rotator.Object).ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.FailedRungs, Has.Count.EqualTo(1).And.Contains("halt_rotator"));
                Assert.That(result.ExposureAborted, Is.True, "the rungs after it still run");
                Assert.That(result.FlatPanelLightOff, Is.True);
            });
        }

        [Test]
        public async Task Execute_ADisconnectedRotator_IsNotPoked() {
            var rotator = new Mock<IRotatorService>();
            rotator.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RotatorDto("r1", "Rotator", EquipmentConnectionState.Disconnected, null,
                    new RotatorStateDto("", null, null, false)));

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                rotator: rotator.Object).ExecuteAsync();

            Assert.That(result.FailedRungs, Is.Empty);
            rotator.Verify(r => r.HaltAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_SwitchesACooledCamerasCoolerOff() {
            // User, 2026-11-02: the stop left the cooler on.
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CameraDto(
                "c1", "Cam", EquipmentConnectionState.Connected, null,
                new CameraStateDto("idle", CcdTemperature: -10, CoolerPowerPct: 60, CoolerOn: true, ExposureProgressPct: null)));
            camera.Setup(c => c.SetCoolerAsync(false, null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var result = await Build().ExecuteAsync();

            Assert.That(result.CoolerOff, Is.True);
            Assert.That(result.FailedRungs, Is.Empty);
            camera.Verify(c => c.SetCoolerAsync(false, null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_LeavesACameraWithItsCoolerOff_Alone() {
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CameraDto(
                "c1", "Cam", EquipmentConnectionState.Connected, null,
                new CameraStateDto("idle", CcdTemperature: 18, CoolerPowerPct: 0, CoolerOn: false, ExposureProgressPct: null)));

            var result = await Build().ExecuteAsync();

            Assert.That(result.CoolerOff, Is.False);
            Assert.That(result.FailedRungs, Is.Empty, "nothing to switch off is not a failure");
            camera.Verify(c => c.SetCoolerAsync(It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_ACoolerThatRefuses_IsCalledOut() {
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CameraDto(
                "c1", "Cam", EquipmentConnectionState.Connected, null,
                new CameraStateDto("idle", CcdTemperature: -10, CoolerPowerPct: 60, CoolerOn: true, ExposureProgressPct: null)));
            camera.Setup(c => c.SetCoolerAsync(false, null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("driver rejected"));

            var result = await Build().ExecuteAsync();

            Assert.That(result.CoolerOff, Is.False);
            Assert.That(result.FailedRungs, Does.Contain("cooler_off"));
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
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("mount driver hung"));

            var result = await Build().ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.MountStopped, Is.False, "the result reports the failed rung honestly");
                Assert.That(result.FailedRungs, Has.Count.EqualTo(1).And.Contains("stop_mount"),
                    "an attempted-but-faulted rung is named; the untouched rungs are not");
                Assert.That(result.FlatPanelLightOff, Is.True, "the rung AFTER the dead device still runs");
                Assert.That(result.GuidingStopped, Is.True);
            });
            flat.Verify(f => f.ApplyFlatPanelAsync(It.IsAny<FlatPanelRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_StopsTheMountWhereItIs_WithNoParkHomeOrSlew() {
            var calls = new List<string>();
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("abort")).Returns(Task.CompletedTask);
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("tracking_off")).Returns(Task.CompletedTask);

            await Build().ExecuteAsync();

            Assert.That(calls, Is.EqualTo(AbortThenTrackingOff));
            telescope.Verify(t => t.GetAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
            telescope.Verify(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()), Times.Once);
            telescope.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Execute_AnAbortFault_StillSendsTrackingOff_AndFailsTheMountRung() {
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("abort refused"));

            var result = await Build().ExecuteAsync();

            Assert.That(result.FailedRungs, Is.EqualTo(StopMountFailed));
            telescope.Verify(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Execute_AHungAbort_StillSendsTrackingOff_AndFailsTheMountRung() {
            var never = new TaskCompletionSource();
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>())).Returns(never.Task);
            var service = Build();
            service.RungTimeout = TimeSpan.FromMilliseconds(50);

            var result = await service.ExecuteAsync();

            Assert.That(result.FailedRungs, Is.EqualTo(StopMountFailed));
            telescope.Verify(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()), Times.Once);
            never.TrySetResult();
        }

        [Test]
        public async Task Execute_ATrackingFault_FailsTheMountRung_AndLaterRungsStillRun() {
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("tracking refused"));

            var result = await Build().ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.FailedRungs, Is.EqualTo(StopMountFailed));
                Assert.That(result.FlatPanelLightOff, Is.True, "the rung after the mount still runs");
            });
        }

        [Test]
        public async Task Execute_AParkedMount_GetsNeitherCall_AndIsNotAFailure() {
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null,
                    new TelescopeStateDto("parked", null, null, Tracking: false, Parked: true, AtHome: false)));

            var result = await Build().ExecuteAsync();

            Assert.That(result.FailedRungs, Is.Empty);
            telescope.Verify(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_ADisconnectedMount_GetsNeitherCall_AndIsNotAFailure() {
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Disconnected, null, null!));

            var result = await Build().ExecuteAsync();

            Assert.That(result.FailedRungs, Is.Empty);
            telescope.Verify(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_TheActionTakenPayload_SaysMountStopped_NotParkRequested() {
            string? actionTaken = null;
            ws.Setup(w => w.PublishAsync(WsEventCatalog.SafetyActionTaken, It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, System.Text.Json.JsonElement, CancellationToken>((_, e, _) => actionTaken = e.GetRawText())
                .Returns(Task.CompletedTask);

            await Build().ExecuteAsync();

            Assert.That(actionTaken, Does.Contain("\"mount_stopped\":true").And.Not.Contain("park_requested"));
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
                Assert.That(result.MountStopped, Is.False);
                Assert.That(result.FlatPanelLightOff, Is.False);
                Assert.That(result.FailedRungs, Is.Empty,
                    "absent devices are not failures — a headless daemon must not scream");
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
        public async Task Execute_ADisconnectedDevice_IsNotAvailable_NotAFailure() {
            // A rig without a flat panel: the service is registered in DI but
            // the device is disconnected — its "is not connected" throw must
            // NOT land in failed_rungs and scare a panicking operator.
            flat.Setup(f => f.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlatDeviceDto("f1", "Panel", EquipmentConnectionState.Disconnected, null!));

            var result = await Build().ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.FlatPanelLightOff, Is.False);
                Assert.That(result.FailedRungs, Is.Empty,
                    "disconnected is a routine state, not a fault");
                Assert.That(result.MountStopped, Is.True, "the connected rungs still run");
            });
            flat.Verify(f => f.ApplyFlatPanelAsync(It.IsAny<FlatPanelRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
                "a disconnected device is never poked — its throw would be a false alarm");
        }

        [Test]
        public async Task Execute_AHungDriver_TimesOutAsFailed_AndTheLadderAndGateSurvive() {
            // A camera that HANGS (stuck USB, unresponsive firmware) rather
            // than throwing: without the per-rung timeout this stalled the
            // ladder forever and wedged the single-flight gate for the whole
            // session.
            var never = new TaskCompletionSource();
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()))
                .Returns(never.Task);
            var service = Build();
            service.RungTimeout = TimeSpan.FromMilliseconds(50);

            var result = await service.ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.ExposureAborted, Is.False);
                Assert.That(result.FailedRungs, Has.Count.EqualTo(1).And.Contains("abort_exposure"),
                    "a hang degrades to a loud failure, never a stall");
                Assert.That(result.MountStopped, Is.True, "the rungs behind the hung device still run");
                Assert.That(result.FlatPanelLightOff, Is.True);
            });

            // The gate re-opened — the button works for the rest of the night.
            var second = await service.ExecuteAsync();
            Assert.That(second.AlreadyInProgress, Is.False);
            never.TrySetResult();
        }

        [Test]
        public async Task Execute_NoRunningSequence_ReportsZeroAborted() {
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

            var result = await Build().ExecuteAsync();

            Assert.That(result.RunsAborted, Is.EqualTo(0));
            Assert.That(result.MountStopped, Is.True, "an idle rig's mount is still stopped — the point is 'nothing moves'");
        }
    }
}
