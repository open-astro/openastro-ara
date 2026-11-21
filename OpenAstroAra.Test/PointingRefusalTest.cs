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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0022 pilot — the pointing modules (solve a frame, Center, Check) refuse with a
    /// declared cause and its parameters. Each test asserts the cause, never the sentence; the endpoints
    /// build no problem answer of their own, so a Refusal leaves them as a <see cref="RefusalException"/>.</summary>
    [TestFixture]
    public class PointingRefusalTest {

        private static Mock<IProfileService> Profile(double focalLength, double pixelSize) {
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(Mock.Of<IPlateSolveSettings>());
            profileService.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(focalLength);
            profileService.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(pixelSize);
            return profileService;
        }

        // ─── Check (Verify Position) ───

        /// <summary>A Check whose capture-and-solve throws <paramref name="captureFault"/>.</summary>
        private static VerifyPositionService Check(Exception captureFault, double focalLength = 800, double pixelSize = 3.8,
                ICameraService? camera = null) {
            var captureSolver = new Mock<ICaptureSolver>();
            captureSolver.Setup(s => s.Solve(It.IsAny<CaptureSequence>(), It.IsAny<CaptureSolverParameter>(),
                    It.IsAny<IProgress<PlateSolveProgress>>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(captureFault);
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetCaptureSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>(), It.IsAny<IImagingMediator>(), It.IsAny<IFilterWheelMediator>()))
                .Returns(captureSolver.Object);
            return new VerifyPositionService(Profile(focalLength, pixelSize).Object, factory.Object, Mock.Of<IImagingMediator>(),
                Mock.Of<ITelescopeMediator>(), Mock.Of<IFilterWheelMediator>(), camera: camera);
        }

        private static readonly string[] FocalLength = { "focal_length" };

        private static async Task<T> ThrownAsync<T>(Func<Task> act) where T : Exception {
            try {
                await act();
            } catch (T thrown) {
                return thrown;
            }
            Assert.Fail($"expected {typeof(T).Name}");
            return null!;
        }

        private static Task<RefusalException> RefusedAsync(Func<Task> act) => ThrownAsync<RefusalException>(act);

        [Test]
        public async Task Check_while_another_exposure_holds_the_camera_refuses_camera_busy() {
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(false);
            using var svc = Check(new InvalidOperationException("never reached"), camera: camera.Object);
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.CameraBusy));
            Assert.That(refusal.Parameters, Is.Empty);
        }

        [Test]
        public async Task Check_with_no_camera_connected_refuses_no_camera() {
            using var svc = Check(new CameraUnavailableException("The camera isn't connected."));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.NoCamera));
        }

        [Test]
        public async Task Check_whose_capture_gate_wait_expires_refuses_gate_stuck() {
            using var svc = Check(new TimeoutException("gate"));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.GateStuck));
        }

        [TestCase(0.0, 3.8, new[] { "focal_length" })]
        [TestCase(double.NaN, 3.8, new[] { "focal_length" })]
        [TestCase(800.0, 0.0, new[] { "pixel_size" })]
        [TestCase(0.0, double.NaN, new[] { "focal_length", "pixel_size" })]
        public async Task Check_with_optics_not_set_refuses_optics_not_set_naming_what_is_missing(
                double focalLength, double pixelSize, string[] missing) {
            using var svc = Check(new InvalidOperationException("never reached"), focalLength, pixelSize);
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.OpticsNotSet));
            Assert.That(refusal.Parameters["missing"], Is.EqualTo(missing));
        }

        [Test]
        public async Task Check_whose_solver_is_not_set_up_refuses_solver_not_set_even_when_the_sentence_names_the_optics() {
            // The cause comes from the exception's kind, never its words: this sentence says "focal length".
            using var svc = Check(new PlateSolverConfigurationException("ASTAP is missing; focal length and pixel size are fine."));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.SolverNotSet));
        }

        [Test]
        public async Task Check_whose_solver_binary_is_missing_refuses_solver_not_set() {
            using var svc = Check(new System.IO.FileNotFoundException("/opt/solver/bin"));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.SolverNotSet));
            // The framework message can embed the server-side path: detail is a fixed sentence, never ex.Message.
            Assert.That(refusal.Message, Does.Not.Contain("/opt/solver/bin"));
        }

        [Test]
        public async Task Check_with_the_frame_store_missing_refuses_storage_unavailable() {
            using var svc = Check(new StoreUnavailableException("No storage disk: nothing is mounted, so there is nowhere to save frames."));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.StorageUnavailable));
            Assert.That(refusal.Parameters, Is.Empty);
        }

        [Test]
        public async Task Check_with_settings_the_camera_cannot_take_refuses_capture_settings_unsupported() {
            using var svc = Check(new ArgumentOutOfRangeException("sequence", 2.0, "binning 4x4 is not supported"));
            var refusal = await RefusedAsync(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.CaptureSettingsUnsupported));
        }

        [Test]
        public async Task Check_that_fails_for_another_reason_is_not_reported_as_no_camera() {
            // The old catch-all turned every InvalidOperationException into `no-camera`; now it stays a fault.
            using var svc = Check(new InvalidOperationException("frame catalog is not configured; captures cannot be stored"));
            var thrown = await ThrownAsync<InvalidOperationException>(() => svc.VerifyAsync(2, CancellationToken.None));
            Assert.That(thrown, Is.Not.InstanceOf<RefusalException>());
        }

        [Test]
        public async Task The_Check_endpoint_builds_no_answer_for_a_refusal_or_a_fault() {
            var refusal = new RefusalException(RefusalCatalog.CameraBusy, "busy");
            var refusing = new Mock<IVerifyPositionService>();
            refusing.Setup(v => v.VerifyAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
            Assert.That(await ThrownAsync<RefusalException>(
                () => PlateSolveEndpoints.VerifyPositionAsync(null, refusing.Object, CancellationToken.None)), Is.SameAs(refusal));

            var fault = new InvalidOperationException("No camera connected");
            var faulting = new Mock<IVerifyPositionService>();
            faulting.Setup(v => v.VerifyAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>())).ThrowsAsync(fault);
            Assert.That(await ThrownAsync<InvalidOperationException>(
                () => PlateSolveEndpoints.VerifyPositionAsync(null, faulting.Object, CancellationToken.None)), Is.SameAs(fault));
        }

        // ─── Solve a frame ───

        [TestCase(0.0, 3.8, new[] { "focal_length" })]
        [TestCase(800.0, double.NaN, new[] { "pixel_size" })]
        [TestCase(double.NaN, 0.0, new[] { "focal_length", "pixel_size" })]
        public async Task Solving_a_frame_with_optics_not_set_refuses_optics_not_set_naming_what_is_missing(
                double focalLength, double pixelSize, string[] missing) {
            var sut = new PlateSolveService(Profile(focalLength, pixelSize).Object, Mock.Of<IPlateSolverFactory>());
            var refusal = await RefusedAsync(() => sut.SolveImageAndLearnOptics(Mock.Of<IImageData>(), null, null, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.OpticsNotSet));
            Assert.That(refusal.Parameters["missing"], Is.EqualTo(missing));
        }

        [TestCase(typeof(PlateSolverConfigurationException))]
        [TestCase(typeof(System.IO.FileNotFoundException))]
        public async Task Solving_a_frame_whose_solver_is_not_set_up_refuses_solver_not_set(Type solverFault) {
            var imageSolver = new Mock<IImageSolver>();
            imageSolver.Setup(s => s.Solve(It.IsAny<IImageData>(), It.IsAny<PlateSolveParameter>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync((Exception)Activator.CreateInstance(solverFault, "/opt/solver/astap")!);
            var factory = new Mock<IPlateSolverFactory>();
            factory.Setup(f => f.GetPlateSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetBlindSolver(It.IsAny<IPlateSolveSettings>())).Returns(Mock.Of<IPlateSolver>());
            factory.Setup(f => f.GetImageSolver(It.IsAny<IPlateSolver>(), It.IsAny<IPlateSolver>())).Returns(imageSolver.Object);
            var sut = new PlateSolveService(Profile(800, 3.8).Object, factory.Object);
            var refusal = await RefusedAsync(() => sut.SolveImageAndLearnOptics(Mock.Of<IImageData>(), null, null, CancellationToken.None));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.SolverNotSet));
            if (solverFault == typeof(System.IO.FileNotFoundException)) {
                // The framework message can embed the server-side path: detail is a fixed sentence, never ex.Message.
                Assert.That(refusal.Message, Does.Not.Contain("/opt/solver/astap"));
            }
        }

        [Test]
        public async Task The_solve_endpoint_builds_no_answer_for_a_refusal() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.LoadImageDataAsync(It.IsAny<Guid>(), It.IsAny<IProfileService>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IImageData>());
            frames.Setup(f => f.TryReadTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((double, double)?)null);
            var refusal = new RefusalException(RefusalCatalog.OpticsNotSet, "no optics",
                new Dictionary<string, object?> { ["missing"] = FocalLength });
            var solver = new Mock<IPlateSolveService>();
            solver.Setup(s => s.SolveImageAndLearnOptics(It.IsAny<IImageData>(), It.IsAny<Coordinates?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(refusal);
            Assert.That(await ThrownAsync<RefusalException>(() => PlateSolveEndpoints.SolveFrameAsync(Guid.NewGuid(), null,
                frames.Object, solver.Object, Mock.Of<IProfileService>(), CancellationToken.None)), Is.SameAs(refusal));
        }

        // ─── Center ───

        [Test]
        public async Task Center_on_another_target_while_one_runs_refuses_center_in_progress_naming_the_running_job() {
            var running = Guid.NewGuid();
            var jobs = new Mock<IBatchJobService>();
            jobs.Setup(j => j.Enqueue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Func<Action<int>, CancellationToken, Task>>(), It.IsAny<string?>()))
                .Throws(new BatchJobConflictException(running, "center:5.500000h,20.000000d", "center:6.500000h,-10.000000d"));
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.NumberOfAttempts).Returns(1);
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => PlateSolveEndpoints.CenterAsync(new CenterRequestDto(6.5, -10.0),
                Mock.Of<ICenteringService>(), jobs.Object, profileService.Object));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.CenterInProgress));
            Assert.That(refusal.Parameters["job_id"], Is.EqualTo(running.ToString()));
        }
    }
}