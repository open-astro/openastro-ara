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
        public async Task An_earlier_failed_capture_doesnt_use_up_the_next_ones_retry() {
            var starts = 0;
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                    : null,
                // The 1st and 2nd exposures fail (one whole capture lost), the 3rd fails, the 4th works.
                putResponder: path => path.EndsWith("/startexposure", StringComparison.Ordinal) && ++starts != 4
                    ? ScriptedAlpacaDevice.Error("ASISetControlValue: ASI_ERROR_GENERAL_ERROR")
                    : null);
            var dir = Directory.CreateTempSubdirectory("ara-camlost-").FullName;
            try {
                var frames = new Mock<IFrameRepository>();
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir);
                await ConnectAsync(svc, box);
                await Assert.ThrowsAsync<CameraUnavailableException>(() =>
                    svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" }, CancellationToken.None, null, "M42"));

                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" }, CancellationToken.None, null, "M42");

                Assert.That(starts, Is.EqualTo(4), "the second capture's one glitch was retried, not treated as lost");
                frames.Verify(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()), Times.Once);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task An_analysis_capture_with_no_camera_connected_says_connect_it_and_try_again() {
            using var camera = new CameraService(frames: new Mock<IFrameRepository>().Object);
            var ex = await Assert.ThrowsAsync<CameraUnavailableException>(() => camera.CaptureForAnalysisAsync(1, 1, CancellationToken.None));
            Assert.That(ex!.Message, Is.EqualTo("The camera isn't connected. Connect it, then try again."));
            Assert.That(ex.Cause, Is.EqualTo("The camera isn't connected."), "a run's prompt builds on the bare cause");
        }

        [Test]
        public async Task An_analysis_capture_outside_a_run_says_try_again_not_press_resume() {
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/imageready", StringComparison.Ordinal) ? "true" : null,
                putResponder: path => path.EndsWith("/startexposure", StringComparison.Ordinal)
                    ? ScriptedAlpacaDevice.Error("ZWO image acquisition failed: camera exposure failed after SDK retries")
                    : null);
            using var svc = new CameraService(frames: new Mock<IFrameRepository>().Object);
            await ConnectAsync(svc, box);

            // #1436: Smart Focus, a plate solve or the rotation readout has nothing to Resume.
            var ex = await Assert.ThrowsAsync<CameraUnavailableException>(() => svc.CaptureForAnalysisAsync(0.01, 1, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("stopped responding").And.Contain("USB cable and power").And.EndWith("then try again."));
            Assert.That(ex.Message, Does.Not.Contain("Resume"));

            // The same failure inside a run (a flat probe) prompts once, for Resume.
            var prompt = UserFixablePause.Prompt(ex);
            Assert.That(prompt, Does.EndWith("then press Resume").And.Not.Contain("try again"));
            Assert.That(prompt.Split("USB cable").Length - 1, Is.EqualTo(1), "the advice isn't doubled");
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

                // Still failing after Resume: that capture gets its own retry, then pauses again.
                await Assert.ThrowsAsync<CameraUnavailableException>(() =>
                    svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "BIAS" }, CancellationToken.None, null, "Flats"));
                Assert.That(starts, Is.EqualTo(4));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
