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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A camera that drops mid-run (2026-10-21: off USB during the flats, then every bias and dark
    /// failed) parks the run awaiting the user. The camera gets one automatic retry first, so a
    /// one-off glitch doesn't stop a run; Resume tries again.
    /// </summary>
    [TestFixture]
    public class CameraLostPauseTest {

        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        private static Mock<ICameraMediator> ConnectedCamera() {
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyCamera.CameraInfo { Connected = true });
            return camera;
        }

        private static PauseGate Attach(OpenAstroAra.Sequencer.SequenceItem.ISequenceItem item) {
            var gate = new PauseGate();
            item.AttachNewParent(new SequenceRootContainer { PauseGate = gate });
            return gate;
        }

        [Test]
        public async Task A_capture_with_no_camera_connected_is_the_typed_refusal() {
            using var camera = new CameraService(frames: new Mock<IFrameRepository>().Object);
            var ex = await Assert.ThrowsAsync<CameraUnavailableException>(() =>
                camera.CaptureImage(new CaptureSequence { ExposureTime = 1 }, CancellationToken.None, null, "M31"));
            Assert.That(ex!.Message, Does.Contain("isn't connected"));
        }

        [Test]
        public async Task A_lost_camera_parks_the_exposure_and_resume_takes_the_frame() {
            var imaging = new Mock<IImagingMediator>();
            var calls = 0;
            imaging.Setup(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<string>()))
                .Returns(() => ++calls == 1
                    ? throw new CameraUnavailableException("The camera stopped responding — it may have dropped off USB or lost power.")
                    : Task.FromResult(new Mock<IExposureData>().Object));
            var item = new TakeExposure(ConnectedCamera().Object, imaging.Object) { ExposureTime = 0.001, ImageType = "BIAS" };
            var gate = Attach(item);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            await Task.Delay(50);
            Assert.That(execute.IsCompleted, Is.False, "parked inside the pause, not a failed frame");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(status, Does.Contain("stopped responding").And.Contain("USB cable and power").And.Contain("press Resume"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Is.EqualTo(2), "Resume took the frame again");
        }

        [Test]
        public async Task A_lost_camera_in_a_flat_set_pauses_and_resume_runs_the_set_again() {
            var executor = new Mock<IFlatCaptureExecutor>();
            var sets = 0;
            executor.Setup(e => e.CaptureFlatSetAsync(It.IsAny<FlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => ++sets == 1 ? throw new CameraUnavailableException("The camera isn't connected.") : Task.FromResult(true));
            var item = new FlatPanelFlats(executor.Object);
            var gate = Attach(item);

            var execute = item.Execute(new SyncProgress(_ => { }), CancellationToken.None);
            await Task.Delay(50);
            Assert.That(execute.IsCompleted, Is.False, "the set waits for the user instead of failing");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(sets, Is.EqualTo(2));
        }

        private static readonly int[] FullThenRemainder = [30, 10];

        [Test]
        public async Task Resume_shoots_only_the_flats_the_lost_camera_left_missing() {
            var executor = new Mock<IFlatCaptureExecutor>();
            var asked = new System.Collections.Generic.List<int>();
            executor.Setup(e => e.CaptureFlatSetAsync(It.IsAny<FlatSetRequest>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns<FlatSetRequest, IProgress<ApplicationStatus>, CancellationToken>((request, _, _) => {
                    asked.Add(request.FrameCount);
                    if (asked.Count == 1) {
                        for (var n = 0; n < 20; n++) {
                            request.Tally!.Add(); // 20 of 30 saved, then the camera drops
                        }
                        throw new CameraUnavailableException("The camera stopped responding.");
                    }
                    return Task.FromResult(true);
                });
            var item = new FlatPanelFlats(executor.Object) { FrameCount = 30 };
            var gate = Attach(item);

            var execute = item.Execute(new SyncProgress(_ => { }), CancellationToken.None);
            await Task.Delay(50);
            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(asked, Is.EqualTo(FullThenRemainder), "the filter ends with 30 flats, not 50");
        }
    }

    /// <summary>The device half against a scripted loopback camera: one failed exposure is retried,
    /// two in a row are a lost camera.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk + loopback HTTP
    public class CameraLostCaptureTest {

        private static async Task ConnectAsync(CameraService svc, ScriptedAlpacaDevice box) {
            await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                await Task.Delay(50);
            }
        }

        [Test]
        public async Task One_failed_exposure_is_retried_and_the_frame_lands() {
            var starts = 0;
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                    : null,
                putResponder: path => path.EndsWith("/startexposure", StringComparison.Ordinal) && ++starts == 1
                    ? ScriptedAlpacaDevice.Error("ASISetControlValue: ASI_ERROR_GENERAL_ERROR")
                    : null);
            var dir = Directory.CreateTempSubdirectory("ara-camlost-").FullName;
            try {
                var frames = new Mock<IFrameRepository>();
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir);
                await ConnectAsync(svc, box);

                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" }, CancellationToken.None, null, "M42");

                Assert.That(starts, Is.EqualTo(2), "the glitch cost one retry");
                frames.Verify(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()), Times.Once);
                Assert.That(svc.DeviceFailureCount, Is.Zero, "a downloaded frame clears the failure count");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task Two_failed_exposures_in_a_row_are_a_lost_camera() {
            var starts = 0;
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/imageready", StringComparison.Ordinal) ? "true" : null,
                putResponder: path => {
                    if (!path.EndsWith("/startexposure", StringComparison.Ordinal)) {
                        return null;
                    }
                    starts++;
                    return ScriptedAlpacaDevice.Error("ZWO image acquisition failed: camera exposure failed after SDK retries");
                });
            var dir = Directory.CreateTempSubdirectory("ara-camlost-").FullName;
            try {
                using var svc = new CameraService(frames: new Mock<IFrameRepository>().Object, fallbackFramesDir: dir);
                await ConnectAsync(svc, box);

                var ex = await Assert.ThrowsAsync<CameraUnavailableException>(() =>
                    svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "BIAS" }, CancellationToken.None, null, "Flats"));
                Assert.That(ex!.Message, Does.Contain("stopped responding"));
                Assert.That(starts, Is.EqualTo(2));

                // Still failing after Resume: lost again at once, no second free retry.
                await Assert.ThrowsAsync<CameraUnavailableException>(() =>
                    svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "BIAS" }, CancellationToken.None, null, "Flats"));
                Assert.That(starts, Is.EqualTo(3));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
