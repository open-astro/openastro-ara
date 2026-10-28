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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CaptureSequence = OpenAstroAra.Equipment.Model.CaptureSequence;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>A capture that names no filter (every sequence TakeExposure: the filter is set by a
    /// separate SwitchFilter step) takes the wheel's installed filter, so FILTER, the catalog row
    /// and the {filter} filename token record it.</summary>
    [TestFixture]
    public class CameraServiceInstalledFilterTest {

        private static CameraService WithWheel(FilterWheelInfo? info) {
            var wheel = new Mock<IFilterWheelMediator>();
            wheel.Setup(w => w.GetInfo()).Returns(info!);
            return new CameraService(filterWheel: () => wheel.Object);
        }

        private static FilterWheelInfo Wheel(bool connected, string? selected, bool moving = false) => new() {
            Connected = connected,
            IsMoving = moving,
            SelectedFilter = selected is null ? null! : new FilterInfo(selected, 0, 2),
        };

        [Test]
        public void An_unnamed_capture_takes_the_installed_filter() {
            using var svc = WithWheel(Wheel(connected: true, "Ha"));
            var request = svc.WithInstalledFilter(new ExposureRequestDto(300, Gain: 100));
            Assert.That(request.FilterName, Is.EqualTo("Ha"));
            Assert.That(request.ExposureSec, Is.EqualTo(300));
            Assert.That(request.Gain, Is.EqualTo(100));
        }

        [Test]
        public void A_named_filter_is_kept() {
            using var svc = WithWheel(Wheel(connected: true, "Ha"));
            var request = svc.WithInstalledFilter(new ExposureRequestDto(300, Gain: null, FilterName: "OIII"));
            Assert.That(request.FilterName, Is.EqualTo("OIII"));
        }

        [TestCase(false, "Ha")]
        [TestCase(true, null)]
        [TestCase(true, " ")]
        public void No_wheel_filter_leaves_the_frame_unfiltered(bool connected, string? selected) {
            using var svc = WithWheel(Wheel(connected, selected));
            Assert.That(svc.WithInstalledFilter(new ExposureRequestDto(300, Gain: null)).FilterName, Is.Null);
        }

        [Test]
        public void A_moving_wheel_or_pending_manual_swap_leaves_the_frame_unfiltered() {
            using var svc = WithWheel(Wheel(connected: true, "Ha", moving: true));
            Assert.That(svc.WithInstalledFilter(new ExposureRequestDto(300, Gain: null)).FilterName, Is.Null);
        }

        [Test]
        public void No_wheel_registered_leaves_the_frame_unfiltered() {
            using var none = WithWheel(null);
            Assert.That(none.WithInstalledFilter(new ExposureRequestDto(300, Gain: null)).FilterName, Is.Null);
            using var unwired = new CameraService();
            Assert.That(unwired.WithInstalledFilter(new ExposureRequestDto(300, Gain: null)).FilterName, Is.Null);
        }
    }

    /// <summary>The sequencer capture end to end against a scripted loopback camera: a
    /// TakeExposure-shaped <see cref="CaptureSequence"/> (no FilterType) on a rig whose wheel
    /// holds Ha lands with FILTER = 'Ha' in the FITS and Ha on the catalog row.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk + loopback HTTP
    public class CameraServiceInstalledFilterCaptureTest {

        [Test]
        public async Task A_bias_frame_runs_at_the_cameras_shortest_exposure() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/exposuremin", StringComparison.Ordinal) ? "0.000032"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : null);
            var dir = Directory.CreateTempSubdirectory("ara-bias-").FullName;
            try {
                FrameDto? row = null;
                var frames = new Mock<IFrameRepository>();
                frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                    .Callback<FrameDto, CancellationToken>((f, _) => row = f)
                    .Returns(Task.CompletedTask);
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir);
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                    UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                    HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                    AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                    await Task.Delay(50);
                }

                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.001, ImageType = "BIAS" },
                    CancellationToken.None, null, "Flats");
                await svc.FinishPendingSavesAsync(); // the sequencer's frames are saved in the background

                Assert.That(row, Is.Not.Null, "the frame was catalogued");
                Assert.That(row!.FrameType, Is.EqualTo(FrameType.Bias));
                Assert.That(row.ExposureSeconds, Is.EqualTo(0.000032), "the step's 1 ms placeholder gives way to ExposureMin");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task A_driver_without_ExposureMin_is_asked_once_not_on_every_bias_frame() {
            var minReads = 0;
            await using var box = ScriptedAlpacaDevice.Start(path => {
                if (path.EndsWith("/exposuremin", StringComparison.Ordinal)) {
                    Interlocked.Increment(ref minReads);
                    return ScriptedAlpacaDevice.Error("Property ExposureMin is not implemented");
                }
                return path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                    : null;
            });
            var dir = Directory.CreateTempSubdirectory("ara-bias-").FullName;
            try {
                using var svc = new CameraService(frames: new Mock<IFrameRepository>().Object, fallbackFramesDir: dir);
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                    UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                    HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                    AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                    await Task.Delay(50);
                }
                var atConnect = Volatile.Read(ref minReads);

                for (var n = 0; n < 3; n++) {
                    await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.001, ImageType = "BIAS" },
                        CancellationToken.None, null, "M42");
                }

                Assert.That(Volatile.Read(ref minReads) - atConnect, Is.LessThanOrEqualTo(1),
                    "one read per connection, not a bridge round-trip per bias frame");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task A_saved_frame_takes_the_cooler_state_from_the_cache_not_the_bridge() {
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var box = ScriptedAlpacaDevice.Start(path => {
                log.Enqueue(path[(path.LastIndexOf('/') + 1)..]);
                return path.EndsWith("/cooleron", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/setccdtemperature", StringComparison.Ordinal) ? "-10"
                    : path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                    : null;
            });
            var dir = Directory.CreateTempSubdirectory("ara-cooler-").FullName;
            try {
                FrameDto? row = null;
                var frames = new Mock<IFrameRepository>();
                frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                    .Callback<FrameDto, CancellationToken>((f, _) => row = f)
                    .Returns(Task.CompletedTask);
                // The background refresh stays out of the way; the connect's own read fills the cache.
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir) { RefreshPeriod = TimeSpan.FromHours(1) };
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                    UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                    HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                    AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null, Runtime.CoolerOn: true }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected with its cooler state read");
                    await Task.Delay(50);
                }
                log.Clear();

                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" },
                    CancellationToken.None, null, "M42");
                await svc.FinishPendingSavesAsync(); // the sequencer's frames are saved in the background

                Assert.That(row!.CoolerSetpointC, Is.EqualTo(-10), "the frame still records the set-point it ran at");
                // The frame's own metadata reads (after the binning read-back) are the sensor
                // temperature and set-point only; the status refresh reads CoolerOn on its own cadence.
                Assert.That(string.Join(" ", log), Does.Contain("biny ccdtemperature setccdtemperature")
                    .And.Not.Contain("biny ccdtemperature setccdtemperature cooleron"),
                    "no CoolerOn round-trip to the bridge for each saved frame");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task A_frame_that_could_not_be_saved_stops_the_next_exposure_until_it_lands() {
            // #1374 at the capture site: the sequencer's frame is handed off and its save fails; the
            // next capture retries it and, still failing, pauses the run before the shutter opens.
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : null);
            var dir = Directory.CreateTempSubdirectory("ara-held-").FullName;
            try {
                var inserted = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
                var catalogDown = true;
                var frames = new Mock<IFrameRepository>();
                frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                    .Returns((FrameDto f, CancellationToken _) => {
                        if (Volatile.Read(ref catalogDown)) {
                            return Task.FromException(new IOException("catalog is read-only"));
                        }
                        inserted.Enqueue(f.Id);
                        return Task.CompletedTask;
                    });
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir);
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                    UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                    HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                    AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                    await Task.Delay(50);
                }
                int Exposures() => box.Puts.Count(p => p.Path.EndsWith("/startexposure", StringComparison.Ordinal));
                var dark = new CaptureSequence { ExposureTime = 0.01, ImageType = "DARK" };

                // Handed off: the capture returns although its save will fail.
                await svc.CaptureImage(dark, CancellationToken.None, null, "M42");
                while (!svc.SaveHeld) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline + TimeSpan.FromSeconds(15)), "the save never failed");
                    await Task.Delay(20);
                }
                var before = Exposures();

                await Assert.ThrowsAsync<OpenAstroAra.Core.Model.StoreUnavailableException>(
                    () => svc.CaptureImage(dark, CancellationToken.None, null, "M42"));
                Assert.That(Exposures(), Is.EqualTo(before), "no shutter opened while a frame is held");

                Volatile.Write(ref catalogDown, false); // the user fixed it; Resume captures again
                await svc.CaptureImage(dark, CancellationToken.None, null, "M42");
                await svc.FinishPendingSavesAsync();
                Assert.That(inserted, Has.Count.EqualTo(2), "the held frame landed, then the new one");
                Assert.That(Exposures(), Is.EqualTo(before + 1));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task A_sequence_frame_records_the_installed_filter() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : null);
            var dir = Directory.CreateTempSubdirectory("ara-filter-").FullName;
            try {
                FrameDto? row = null;
                var frames = new Mock<IFrameRepository>();
                frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                    .Callback<FrameDto, CancellationToken>((f, _) => row = f)
                    .Returns(Task.CompletedTask);
                var wheel = new Mock<IFilterWheelMediator>();
                wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                    Connected = true, SelectedFilter = new FilterInfo("Ha", 0, 4),
                });
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir,
                    filterWheel: () => wheel.Object);
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(
                    UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
                    HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                    AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                    await Task.Delay(50);
                }

                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "LIGHT" },
                    CancellationToken.None, null, "M42");
                await svc.FinishPendingSavesAsync(); // the sequencer's frames are saved in the background

                Assert.That(row, Is.Not.Null, "the frame was catalogued");
                Assert.That(row!.FilterName, Is.EqualTo("Ha"));
                var header = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(row.FilePath));
                Assert.That(header, Does.Match(@"FILTER  = 'Ha\s*'"));

                // Darks and bias don't depend on the filter: the wheel's position isn't recorded.
                await svc.CaptureImage(new CaptureSequence { ExposureTime = 0.01, ImageType = "DARK" },
                    CancellationToken.None, null, "M42");
                await svc.FinishPendingSavesAsync();
                Assert.That(row!.FrameType, Is.EqualTo(FrameType.Dark));
                Assert.That(row.FilterName, Is.Null);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
