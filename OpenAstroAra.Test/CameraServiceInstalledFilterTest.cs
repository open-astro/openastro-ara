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

        private static FilterWheelInfo Wheel(bool connected, string? selected) => new() {
            Connected = connected,
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

                Assert.That(row, Is.Not.Null, "the frame was catalogued");
                Assert.That(row!.FilterName, Is.EqualTo("Ha"));
                var header = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(row.FilePath));
                Assert.That(header, Does.Match(@"FILTER  = 'Ha\s*'"));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
