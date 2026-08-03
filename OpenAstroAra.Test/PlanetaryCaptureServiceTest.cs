#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Video;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§77.2/§77.4 P2 — planetary-mode arbitration + capture surface.</summary>
    [TestFixture]
    public class PlanetaryCaptureServiceTest {

        private sealed class SyntheticCapture : IVideoCapture {
            private int frameSize;
            private long produced;
            private volatile bool started;

            public ulong Produced => (ulong)Interlocked.Read(ref produced);
            public ulong DroppedFrames => 0;
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }

            public void Start(in VideoRequest request) {
                StartCalls++;
                frameSize = (int)VideoFormats.FrameBytes(request);
                started = true;
            }

            public bool GetFrame(Span<byte> buffer, int timeoutMs) {
                if (!started) {
                    return false;
                }
                buffer[..frameSize].Fill(0x42);
                Interlocked.Increment(ref produced);
                return true;
            }

            public void StopCapture() {
                StopCalls++;
                started = false;
            }

            public void Dispose() => StopCapture();
        }

        private static readonly DiscoveredDeviceDto SampleDevice = new(
            UniqueId: "zwo-sim-1", Name: "ZWO ASI662MC", Type: DeviceType.Camera,
            HostName: "localhost", IpAddress: "127.0.0.1", IpPort: 6800,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static PlanetaryCaptureService NewService(
            Mock<ICameraService> camera,
            ActiveRunSessionRegistry registry,
            SyntheticCapture capture,
            DiscoveredDeviceDto? lastDevice = null) =>
            new(NullLogger<PlanetaryCaptureService>.Instance,
                NullLogger<VideoRecorder>.Instance,
                camera.Object,
                registry,
                ws: null,
                profileStore: null,
                new UsbfsTuner(NullLogger<UsbfsTuner>.Instance),
                captureFactory: _ => capture,
                lastDeviceProvider: () => lastDevice);

        // The service takes ownership via captureFactory and disposes it — but the
        // analyzer can't see through the factory lambda, so tests hold a using ref.
        private static SyntheticCapture NewCapture() => new();

        private static Mock<ICameraService> NewCamera() {
            var camera = new Mock<ICameraService>(MockBehavior.Loose);
            camera.Setup(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "camera.disconnect", DateTimeOffset.UtcNow, null));
            camera.Setup(c => c.ConnectAsync(It.IsAny<ConnectRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "camera.connect", DateTimeOffset.UtcNow, null));
            return camera;
        }

        [Test]
        public async Task Enter_DetachesCameraAndReportsPlanetaryMode() {
            var camera = NewCamera();
            using var capture = NewCapture();
            using var service = NewService(camera, new ActiveRunSessionRegistry(), capture);

            var accepted = await service.EnterAsync(new PlanetaryEnterRequestDto(3, null), "k1", CancellationToken.None);
            accepted.OperationType.Should().Be("planetary.enter");
            camera.Verify(c => c.DisconnectAsync(null, It.IsAny<CancellationToken>()), Times.Once);

            var status = service.Status();
            status.Mode.Should().Be("planetary");
            status.CameraId.Should().Be(3);
        }

        [Test]
        public async Task Enter_RefusedWhileASequenceRunIsActive() {
            var registry = new ActiveRunSessionRegistry();
            registry.Enter(Guid.NewGuid());
            var camera = NewCamera();
            using var capture = NewCapture();
            using var service = NewService(camera, registry, capture);

            var act = () => service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*sequence run is active*");
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Enter_IdempotentForSameCamera_RefusedForAnother() {
            var camera = NewCamera();
            using var capture = NewCapture();
            using var service = NewService(camera, new ActiveRunSessionRegistry(), capture);
            await service.EnterAsync(new PlanetaryEnterRequestDto(1, null), null, CancellationToken.None);
            await service.EnterAsync(new PlanetaryEnterRequestDto(1, null), null, CancellationToken.None);
            camera.Verify(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

            var other = () => service.EnterAsync(new PlanetaryEnterRequestDto(2, null), null, CancellationToken.None);
            await other.Should().ThrowAsync<InvalidOperationException>().WithMessage("*leave first*");
        }

        [Test]
        public async Task RecordStart_RequiresPlanetaryMode() {
            using var capture = NewCapture();
            using var service = NewService(NewCamera(), new ActiveRunSessionRegistry(), capture);
            var act = () => service.StartRecordingAsync(
                new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 5, null), null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in planetary mode*");
        }

        [Test]
        public async Task RecordStart_RejectsUnknownFormat() {
            using var capture = NewCapture();
            using var service = NewService(NewCamera(), new ActiveRunSessionRegistry(), capture);
            await service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
            var act = () => service.StartRecordingAsync(
                new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "png", 100, 5, null), null, CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*unknown pixel format*");
        }

        [Test]
        public async Task RecordLifecycle_WritesSerAndReportsHonestCounters() {
            using var capture = new SyntheticCapture();
            var camera = NewCamera();
            using var service = NewService(camera, new ActiveRunSessionRegistry(), capture, SampleDevice);
            var name = $"planetary_svc_{Guid.NewGuid():N}.ser";
            string? path = null;
            try {
                await service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
                await service.StartRecordingAsync(
                    new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 1, name), null, CancellationToken.None);
                path = service.Status().OutputPath;
                path.Should().NotBeNull();
                Path.GetFileName(path).Should().Be(name);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (capture.Produced < 20 && DateTime.UtcNow < deadline) {
                    await Task.Delay(5);
                }
                var live = service.Status();
                live.Recording.Should().NotBeNull();
                live.Recording!.Recording.Should().BeTrue();

                await service.StopRecordingAsync(null, CancellationToken.None);
                var stats = service.Status().Recording!;
                stats.Recording.Should().BeFalse();
                stats.Error.Should().BeEmpty();
                (stats.FramesWritten + stats.RingDroppedFrames + stats.AbandonedFrames)
                    .Should().Be(stats.FramesCaptured);
                File.Exists(path).Should().BeTrue();

                // Leave: SDK stopped, Alpaca reconnect attempted with the last device.
                await service.LeaveAsync(null, CancellationToken.None);
                capture.StopCalls.Should().BeGreaterThan(0);
                camera.Verify(c => c.ConnectAsync(
                    It.Is<ConnectRequestDto>(r => r.Device.UniqueId == SampleDevice.UniqueId),
                    null, It.IsAny<CancellationToken>()), Times.Once);
                var after = service.Status();
                after.Mode.Should().Be("idle");
                // r2: no stale session data after leave.
                after.Recording.Should().BeNull();
                after.OutputPath.Should().BeNull();
            } finally {
                if (path is not null) {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public async Task Leave_IsIdempotentWhenIdle() {
            var camera = NewCamera();
            using var capture = NewCapture();
            using var service = NewService(camera, new ActiveRunSessionRegistry(), capture);
            var accepted = await service.LeaveAsync(null, CancellationToken.None);
            accepted.OperationType.Should().Be("planetary.leave");
            camera.Verify(c => c.ConnectAsync(It.IsAny<ConnectRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task LeaveThenEnterDifferentCamera_GetsAFreshCapture() {
            // Review #911 r1: the capture instance is constructor-bound to a camera id;
            // reusing it across a Leave -> Enter(other camera) would record the wrong
            // physical camera. The factory must be re-invoked after leave.
            var factoryCalls = 0;
            var camera = NewCamera();
            using var captureA = NewCapture();
            using var captureB = NewCapture();
            using var service = new PlanetaryCaptureService(
                NullLogger<PlanetaryCaptureService>.Instance,
                NullLogger<VideoRecorder>.Instance,
                camera.Object,
                new ActiveRunSessionRegistry(),
                ws: null,
                profileStore: null,
                new UsbfsTuner(NullLogger<UsbfsTuner>.Instance),
                captureFactory: _ => ++factoryCalls == 1 ? captureA : captureB,
                lastDeviceProvider: () => null);
            var pathA = $"planetary_a_{Guid.NewGuid():N}.ser";
            var pathB = $"planetary_b_{Guid.NewGuid():N}.ser";
            string? fullA = null;
            string? fullB = null;
            try {
                await service.EnterAsync(new PlanetaryEnterRequestDto(1, null), null, CancellationToken.None);
                await service.StartRecordingAsync(
                    new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 1, pathA), null, CancellationToken.None);
                fullA = service.Status().OutputPath;
                await service.StopRecordingAsync(null, CancellationToken.None);
                await service.LeaveAsync(null, CancellationToken.None);
                captureA.StopCalls.Should().BeGreaterThan(0);

                await service.EnterAsync(new PlanetaryEnterRequestDto(2, null), null, CancellationToken.None);
                await service.StartRecordingAsync(
                    new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 1, pathB), null, CancellationToken.None);
                fullB = service.Status().OutputPath;
                await service.StopRecordingAsync(null, CancellationToken.None);
                factoryCalls.Should().Be(2);
                captureB.StartCalls.Should().Be(1);
            } finally {
                if (fullA is not null) { File.Delete(fullA); }
                if (fullB is not null) { File.Delete(fullB); }
            }
        }

        [Test]
        public async Task Status_ReportsRealDirectIoState() {
            // Review #911 r1: uses_direct_io must reflect the writer's actual mode —
            // on this dev host (non-Linux or O_DIRECT-refusing fs) that is honest, not
            // a hardcoded true.
            using var capture = NewCapture();
            using var service = NewService(NewCamera(), new ActiveRunSessionRegistry(), capture);
            var name = $"planetary_dio_{Guid.NewGuid():N}.ser";
            string? path = null;
            try {
                await service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
                await service.StartRecordingAsync(
                    new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 1, name), null, CancellationToken.None);
                path = service.Status().OutputPath;
                var expected = OperatingSystem.IsLinux();   // GitHub runners: ext4 accepts O_DIRECT
                if (!expected) {
                    service.Status().UsesDirectIo.Should().BeFalse();
                }
                await service.StopRecordingAsync(null, CancellationToken.None);
            } finally {
                if (path is not null) {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public async Task RecordStart_RejectsPathsOutsideThePlanetaryDirectory() {
            // r2: output_path is a bare filename confined to the planetary output
            // directory — separators, traversal, and absolute paths are refused.
            using var capture = NewCapture();
            using var service = NewService(NewCamera(), new ActiveRunSessionRegistry(), capture);
            await service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
            foreach (var bad in new[] { "/tmp/evil.ser", "../evil.ser", "sub/dir.ser", "a..b/evil.ser" }) {
                var act = () => service.StartRecordingAsync(
                    new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 1, bad), null, CancellationToken.None);
                await act.Should().ThrowAsync<ArgumentException>($"'{bad}' must be refused");
            }
        }

        [Test]
        public async Task RecordStart_RejectsDegenerateExposureAndGain() {
            // r3: exposure_ms/gain are validated at the boundary — a zero/negative
            // exposure would otherwise produce a degenerate GetFrame timeout.
            using var capture = NewCapture();
            using var service = NewService(NewCamera(), new ActiveRunSessionRegistry(), capture);
            await service.EnterAsync(new PlanetaryEnterRequestDto(0, null), null, CancellationToken.None);
            var zeroExposure = () => service.StartRecordingAsync(
                new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", 100, 0, null), null, CancellationToken.None);
            await zeroExposure.Should().ThrowAsync<ArgumentException>().WithMessage("*exposure_ms*");
            var negativeGain = () => service.StartRecordingAsync(
                new PlanetaryRecordRequestDto(0, 0, 32, 16, 1, "mono8", -1, 5, null), null, CancellationToken.None);
            await negativeGain.Should().ThrowAsync<ArgumentException>().WithMessage("*gain*");
        }

        [Test]
        public void UsbfsTargetMb_ScalesWithRamAndClamps() {
            const long GiB = 1024L * 1024 * 1024;
            UsbfsTuner.TargetMb(2 * GiB).Should().Be(256);     // iMate class
            UsbfsTuner.TargetMb(8 * GiB).Should().Be(1000);    // ceiling
            UsbfsTuner.TargetMb(256 * 1024 * 1024).Should().Be(64);   // floor
        }

        [Test]
        public void ActiveRunRegistry_HasAny_TracksMultipleRuns() {
            var registry = new ActiveRunSessionRegistry();
            registry.HasAny.Should().BeFalse();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            registry.Enter(a);
            registry.Enter(b);
            registry.HasAny.Should().BeTrue();
            registry.Current.Should().BeNull();   // ≥2 runs: Current deliberately null
            registry.Exit(a);
            registry.Exit(b);
            registry.HasAny.Should().BeFalse();
        }
    }
}
