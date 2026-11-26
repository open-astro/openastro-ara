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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §35.3 — <see cref="EmergencyStopService"/>: the stop ladder (abort runs
    /// → stop mount → abort exposure → stop guiding → flat light off), best-effort
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

            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (publishedEvents) { publishedEvents.Add(t); } })
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
                occupancy: new RigOccupancy().RegisterGuideFocus(() => focus.Object)).ExecuteAsync();

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
                occupancy: new RigOccupancy().RegisterBahtinov(() => bahtinov.Object)).ExecuteAsync();

            bahtinov.Verify(b => b.StopAsync(), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_CancelsARunningAutofocus() {
            // #2143 — autofocus is a Rig Occupancy routine: the run ends when its token is cancelled, as the sweep does.
            var tracker = new AutofocusRunTracker();
            using var run = new CancellationTokenSource();
            tracker.Begin("classic", 100, 5, null, null, run);
            run.Token.Register(() => tracker.Cancel(restoredPosition: 100));

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterAutofocus(() => tracker)).ExecuteAsync();

            Assert.That(run.IsCancellationRequested, Is.True);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_StopsPolarAlignment() {
            var polar = new Mock<IPolarAlignService>();
            polar.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PolarAlignStateDto("adjusting", 12, 10, 2, 5, null));
            var polarRuns = true; // until its stop lands: the register confirms the stop by this reading
            polar.Setup(p => p.IsActive).Returns(() => polarRuns);
            polar.Setup(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()))
                .Callback(() => polarRuns = false).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object)).ExecuteAsync();

            polar.Verify(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_StopsPolarAlignment_InTheCheckStageToo() {
            // #1591 added homing and checking: a mount mid-Find-Home or between two check turns must stop
            // like one in the adjust loop (the rung asks IsActive, not a list of state names).
            var polar = new Mock<IPolarAlignService>();
            polar.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PolarAlignStateDto("checking", null, null, null, 0, null));
            var polarRuns = true; // until its stop lands: the register confirms the stop by this reading
            polar.Setup(p => p.IsActive).Returns(() => polarRuns);
            polar.Setup(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()))
                .Callback(() => polarRuns = false).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object)).ExecuteAsync();

            polar.Verify(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()), Times.Once);
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
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object)).ExecuteAsync();

            polar.Verify(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_AnIdleGuideCameraFocusLoop_IsNotPoked() {
            var focus = new Mock<IGuideFocusService>();
            focus.SetupGet(f => f.IsActive).Returns(false);

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterGuideFocus(() => focus.Object)).ExecuteAsync();

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
                WsEventCatalog.SafetyEmergencyStop.Token,
                WsEventCatalog.SafetyActionTaken.Token,
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
            telescope.Verify(t => t.FenceMotion("emergency_stop"), Times.Once, "#2066 — the latch: counted, no command to the mount");
            telescope.VerifyNoOtherCalls();
        }

        private static readonly string[] MountRightAfterRuns = { "runs", "abort", "tracking_off", "rotator", "exposure" };

        [Test]
        public async Task Execute_StopsTheMountRightAfterRuns_AheadOfRotatorAndExposure() {
            var calls = new List<string>();
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("runs")).ReturnsAsync(1);
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("abort")).Returns(Task.CompletedTask);
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("tracking_off")).Returns(Task.CompletedTask);
            var rotator = new Mock<IRotatorService>();
            rotator.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RotatorDto("r1", "Rot", EquipmentConnectionState.Connected, null, null!));
            rotator.Setup(r => r.HaltAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("rotator")).ReturnsAsync(Accepted);
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("exposure")).Returns(Task.CompletedTask);

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                rotator: rotator.Object).ExecuteAsync();

            Assert.That(calls, Is.EqualTo(MountRightAfterRuns));
        }

        private static readonly string[] PolarAlignerFailed = { "stop_polar_aligner" };
        private static readonly string[] AlignerRightAfterMount = { "abort", "tracking_off", "aligner", "exposure" };

        [Test]
        public async Task Execute_StopsAPresentPolarAligner_RightAfterTheMount() {
            var calls = new List<string>();
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("abort")).Returns(Task.CompletedTask);
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("tracking_off")).Returns(Task.CompletedTask);
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("exposure")).Returns(Task.CompletedTask);
            var aligner = new Mock<IPolarAlignerService>();
            aligner.Setup(p => p.IsPresent).Returns(true);
            aligner.Setup(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("aligner")).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAligner: aligner.Object).ExecuteAsync();

            Assert.That(calls, Is.EqualTo(AlignerRightAfterMount));
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_NoPolarAligner_IsNotPoked() {
            var aligner = new Mock<IPolarAlignerService>();
            aligner.Setup(p => p.IsPresent).Returns(false);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAligner: aligner.Object).ExecuteAsync();

            aligner.Verify(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_APolarAlignerThatRefusesToStop_IsCalledOut_AndTheLadderGoesOn() {
            var aligner = new Mock<IPolarAlignerService>();
            aligner.Setup(p => p.IsPresent).Returns(true);
            aligner.Setup(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("motor driver fault"));

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                polarAligner: aligner.Object).ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.FailedRungs, Is.EqualTo(PolarAlignerFailed));
                Assert.That(result.ExposureAborted, Is.True, "the rungs after it still run");
            });
        }

        private static readonly string[] StoppedAgainAfterPolar = { "abort", "tracking_off", "polar", "abort", "tracking_off" };

        [Test]
        public async Task Execute_StopsTheMountAgain_AfterARunningPolarAlignmentEnds() {
            // Polar alignment can move the mount and turn tracking on until its Stop lands.
            var calls = new List<string>();
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("abort")).Returns(Task.CompletedTask);
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("tracking_off")).Returns(Task.CompletedTask);
            var polar = new Mock<IPolarAlignService>();
            var polarRuns = true;
            polar.Setup(p => p.IsActive).Returns(() => polarRuns);
            polar.Setup(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()))
                .Callback(() => { calls.Add("polar"); polarRuns = false; }).ReturnsAsync(Accepted);

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object)).ExecuteAsync();

            Assert.That(calls, Is.EqualTo(StoppedAgainAfterPolar));
        }

        // #2142 — a Center under way (a REST Center job: no run, no token this ladder knows) ends with the stop: its next
        // attempt does not slew the mount again.
        [Test]
        public async Task Execute_EndsACenterUnderWay_SoItSlewsTheMountNoMore() {
            using var center = await RunningCenter.StartAsync();

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterCenter(() => center.Service)).ExecuteAsync();
            await center.ReleaseNextAttemptAsync();

            Assert.Multiple(() => {
                Assert.That(center.Slewed(), Is.False, "the Center slewed after Emergency Stop");
                Assert.That(center.Service.IsCentering, Is.False);
            });
        }

        private static readonly string[] CameraLoopFailed = { "stop_camera_loop" };

        // #2142 — a routine whose stop never returns is named in failed_rungs, and every rung after it still runs.
        [Test]
        public async Task Execute_ARoutineThatDoesNotStop_IsCalledOut_AndTheLadderGoesOn() {
            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().Register("camera loop", RigHolding.MainCamera, () => true, (_, _) => new TaskCompletionSource().Task)) {
                RungTimeout = TimeSpan.FromMilliseconds(100),
            }.ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.FailedRungs, Is.EqualTo(CameraLoopFailed));
                Assert.That(result.GuidingStopped, Is.True, "stop guiding ran after it");
                Assert.That(result.FlatPanelLightOff, Is.True, "the flat panel rung ran after it");
            });
        }

        // #2142 — the mount is stopped again only after a routine that holds it: one that holds only the camera cannot move it.
        [Test]
        public async Task Execute_StopsTheMountAgain_OnlyAfterARoutineThatHoldsIt() {
            var aborts = 0;
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>())).Callback(() => aborts++).Returns(Task.CompletedTask);
            var loopRuns = true;

            await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().Register("camera loop", RigHolding.MainCamera, () => loopRuns,
                    (_, _) => { loopRuns = false; return Task.CompletedTask; })).ExecuteAsync();

            Assert.That(aborts, Is.EqualTo(1));
        }

        // #2066 — the stop is latched in the mount's stop count before the first event and before any rung: a polar
        // alignment between two turns would otherwise start its next turn while a slow rung (the run abort) holds the ladder.
        [Test]
        public async Task Execute_LatchesTheStopInTheMount_BeforeTheEventAndTheRunAbort() {
            var calls = new List<string>();
            telescope.Setup(t => t.FenceMotion(It.IsAny<string>()))
                .Callback<string>(reason => { lock (calls) { calls.Add("fence " + reason); } });
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback(() => { lock (calls) { calls.Add("event"); } })
                .Returns(Task.CompletedTask);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => { lock (calls) { calls.Add("abort_runs"); } }).ReturnsAsync(0);
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Callback(() => { lock (calls) { calls.Add("abort"); } }).Returns(Task.CompletedTask);

            await Build().ExecuteAsync();

            Assert.That(calls.Take(4), Is.EqualTo(LatchFirst));
        }

        private static readonly string[] LatchFirst = { "fence emergency_stop", "event", "abort_runs", "abort" };

        private static readonly string[] AlignerStoppedAgainAfterPolar = { "aligner", "polar", "aligner" };

        // #2066 — polar alignment drives the Polar Aligner, and now leaves the stop to this ladder: the aligner is stopped
        // again after it, like the mount.
        [Test]
        public async Task Execute_StopsThePolarAlignerAgain_AfterARunningPolarAlignmentEnds() {
            var calls = new List<string>();
            var polar = new Mock<IPolarAlignService>();
            var polarRuns = true;
            polar.Setup(p => p.IsActive).Returns(() => polarRuns);
            polar.Setup(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()))
                .Callback(() => { calls.Add("polar"); polarRuns = false; }).ReturnsAsync(Accepted);
            var aligner = new Mock<IPolarAlignerService>();
            aligner.Setup(p => p.IsPresent).Returns(true);
            aligner.Setup(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("aligner")).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object), polarAligner: aligner.Object).ExecuteAsync();

            Assert.That(calls, Is.EqualTo(AlignerStoppedAgainAfterPolar));
            Assert.That(result.FailedRungs, Is.Empty);
        }

        [Test]
        public async Task Execute_ASecondMountStopThatFaults_ReportsTheMountOnce_AndNotStopped() {
            var aborts = 0;
            telescope.Setup(t => t.AbortSlewAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ++aborts == 2 ? Task.FromException(new InvalidOperationException("second abort refused")) : Task.CompletedTask);
            telescope.Setup(t => t.SetTrackingAsync(false, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var polar = new Mock<IPolarAlignService>();
            var polarRuns = true; // until its stop lands: the register confirms the stop by this reading
            polar.Setup(p => p.IsActive).Returns(() => polarRuns);
            polar.Setup(p => p.StopForTakeoverAsync("emergency_stop", It.IsAny<CancellationToken>()))
                .Callback(() => polarRuns = false).ReturnsAsync(Accepted);

            var result = await new EmergencyStopService(
                camera.Object, () => sequencer.Object, guider.Object,
                telescope.Object, flat.Object, notifications.Object, ws.Object,
                occupancy: new RigOccupancy().RegisterPolarAlign(() => polar.Object)).ExecuteAsync();

            Assert.Multiple(() => {
                Assert.That(result.MountStopped, Is.False);
                Assert.That(result.FailedRungs, Is.EqualTo(StopMountFailed));
            });
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
                .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, System.Text.Json.JsonElement, CancellationToken>((_, e, _) => actionTaken = e.GetRawText())
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

        // #1985 — a hung probe or a blocking synchronous prefix must not hold the ladder. The outer
        // bound keeps a regression from hanging the suite: the ladder runs off the test thread.
        private static async Task<EmergencyStopResultDto?> RunWithOuterBoundAsync(EmergencyStopService service) {
            var ladder = Task.Run(() => service.ExecuteAsync());
            var done = await Task.WhenAny(ladder, Task.Delay(TimeSpan.FromSeconds(10)));
            return ReferenceEquals(done, ladder) ? await ladder : null;
        }

        [Test]
        public async Task Execute_AProbeThatNeverCompletes_FailsTheRung_AndTheLadderGoesOn() {
            var never = new TaskCompletionSource<CameraDto?>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).Returns(never.Task!);
            var service = Build();
            service.RungTimeout = TimeSpan.FromMilliseconds(500);

            var result = await RunWithOuterBoundAsync(service);

            never.TrySetResult(null);
            Assert.That(result, Is.Not.Null, "the ladder must finish within the outer bound");
            Assert.Multiple(() => {
                Assert.That(result!.FailedRungs, Does.Contain("abort_exposure"));
                Assert.That(result.MountStopped, Is.True, "the rungs after the hung probe still run");
                Assert.That(result.FlatPanelLightOff, Is.True);
            });
        }

        [Test]
        public async Task Execute_AnActionThatBlocksItsCaller_FailsTheRung_AndTheLadderGoesOn() {
            using var release = new ManualResetEventSlim();
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>()))
                .Returns(() => { release.Wait(TimeSpan.FromSeconds(30)); return Task.CompletedTask; });
            var service = Build();
            service.RungTimeout = TimeSpan.FromMilliseconds(500);

            var result = await RunWithOuterBoundAsync(service);

            release.Set();
            Assert.That(result, Is.Not.Null, "the ladder must finish within the outer bound");
            Assert.Multiple(() => {
                Assert.That(result!.FailedRungs, Does.Contain("abort_exposure"));
                Assert.That(result.MountStopped, Is.True, "the rungs after the blocked action still run");
                Assert.That(result.FlatPanelLightOff, Is.True);
            });
        }

        [Test]
        public async Task Execute_NoRunningSequence_ReportsZeroAborted() {
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

            var result = await Build().ExecuteAsync();

            Assert.That(result.RunsAborted, Is.EqualTo(0));
            Assert.That(result.MountStopped, Is.True, "an idle rig's mount is still stopped — the point is 'nothing moves'");
        }
    }
    /// <summary>
    /// #1985 — a rung whose work completes synchronously must not need a free pool thread to start
    /// inside its RungTimeout window: an Emergency Stop has to work while the pool is starved. Own fixture, run alone, because
    /// it blocks every pool worker for a moment.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class EmergencyStopServicePoolStarvationTest {

        [Test]
        public void Execute_WithEveryPoolThreadBlocked_HealthyRungsAreNotTimedOut() {
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CameraDto("c1", "Cam", EquipmentConnectionState.Connected, null, null!));
            camera.Setup(c => c.AbortExposureAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var service = new EmergencyStopService(camera.Object) { RungTimeout = TimeSpan.FromMilliseconds(200) };

            ThreadPool.GetMinThreads(out var minWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);
            var toBlock = Math.Min(minWorkers, maxWorkers);
            using var release = new ManualResetEventSlim();
            using var started = new CountdownEvent(toBlock);
            // Block as many workers as the pool's minimum. Above the minimum the pool injects threads
            // slowly (about one per second), far slower than the 200 ms window. The mocks complete
            // synchronously, so this pins the dedicated-thread start, not the continuation path.
            for (var i = 0; i < toBlock; i++) {
                ThreadPool.QueueUserWorkItem(_ => { started.Signal(); release.Wait(TimeSpan.FromSeconds(30)); });
            }
            try {
                Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True, "every pool worker is blocked");
                EmergencyStopResultDto? result = null;
                var caller = new Thread(() => result = service.ExecuteAsync().GetAwaiter().GetResult());
                caller.Start();
                Assert.That(caller.Join(TimeSpan.FromSeconds(15)), Is.True, "the ladder finishes");
                Assert.That(result!.FailedRungs, Is.Empty, "a healthy rung is not failed by a starved pool");
                Assert.That(result.ExposureAborted, Is.True);
            } finally {
                release.Set();
            }
        }
    }
}