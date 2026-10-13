#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1177 — the optional ImageType on a manual capture: validated at the entry point, written as the
    /// catalog frame type, and DARK/BIAS close the shutter.</summary>
    [TestFixture]
    public class ManualCaptureImageTypeTest {

        [Test]
        public void Image_type_normalizes_the_five_names_and_rejects_the_rest() {
            Assert.That(CameraService.NormalizeManualImageType(null), Is.EqualTo("LIGHT"));
            Assert.That(CameraService.NormalizeManualImageType("  "), Is.EqualTo("LIGHT"));
            Assert.That(CameraService.NormalizeManualImageType("dark"), Is.EqualTo("DARK"));
            Assert.That(CameraService.NormalizeManualImageType(" Bias "), Is.EqualTo("BIAS"));
            Assert.That(CameraService.NormalizeManualImageType("Flat"), Is.EqualTo("FLAT"));
            Assert.That(CameraService.NormalizeManualImageType("DarkFlat"), Is.EqualTo("DARKFLAT"));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraService.NormalizeManualImageType("PRETTY"));
            Assert.Throws<ArgumentOutOfRangeException>(() => CameraService.NormalizeManualImageType("SNAPSHOT"),
                "snapshots are the Live tab's own kind, not a manual-capture type");
        }

        [Test]
        public void Only_darks_and_biases_close_the_shutter() {
            Assert.That(CameraService.IsLightExposure("dark"), Is.False);
            Assert.That(CameraService.IsLightExposure("bias"), Is.False);
            foreach (var open in new[] { "light", "flat", "darkflat", "snapshot", "analysis", "liveview" }) {
                Assert.That(CameraService.IsLightExposure(open), Is.True, open);
            }
        }

        [Test]
        public void A_bad_image_type_is_an_argument_error_before_the_connected_check() {
            using var svc = new CameraService();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null, ImageType: "PRETTY"), null, CancellationToken.None); });
        }

        [Test]
        [Category("IO")]
        public async Task A_dark_manual_capture_closes_the_shutter_and_registers_a_dark_frame() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/imageready", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/imagearray", StringComparison.Ordinal) ? "[[100,200],[300,400]],\"Type\":2,\"Rank\":2"
                : path.EndsWith("/sensortype", StringComparison.Ordinal) ? "0"
                : null);
            var frames = new Mock<IFrameRepository>();
            var dir = Path.Combine(Path.GetTempPath(), "oara-manual-dark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                using var svc = new CameraService(frames: frames.Object, fallbackFramesDir: dir);
                await svc.ConnectAsync(new ConnectRequestDto(new DiscoveredDeviceDto(UniqueId: "scripted-cam", Name: "Scripted camera", Type: DeviceType.Camera, HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port, AlpacaDeviceNumber: 0, UseHttps: false)), null, CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while ((await svc.GetAsync(CancellationToken.None)) is not { State: EquipmentConnectionState.Connected, Capabilities: not null }) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "camera never connected");
                    await Task.Delay(50);
                }

                await svc.StartExposureAsync(new ExposureRequestDto(0.01, Gain: null, ImageType: "dark"), null, CancellationToken.None);

                deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (!frames.Invocations.Any(i => i.Method.Name == nameof(IFrameRepository.InsertAsync))) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the dark frame was never registered");
                    await Task.Delay(50);
                }
                frames.Verify(f => f.InsertAsync(It.Is<FrameDto>(d => d.FrameType == FrameType.Dark), It.IsAny<CancellationToken>()), Times.Once,
                    "a DARK manual capture lands in the catalog as a dark");
                var start = box.Puts.Single(p => p.Path.EndsWith("/startexposure", StringComparison.Ordinal));
                Assert.That(start.Body, Does.Contain("Light=False").IgnoreCase, "a dark is taken with the shutter closed");
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }
}
