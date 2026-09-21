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
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>#1065 — the WIRED halves of the cooling-fan interlock, against loopback scripted
    /// Alpaca devices (bench category: runs in the default job): the switch write path refuses a
    /// fan-off while the camera cools (or cannot be read) and lets it through when the cooler is
    /// off; the camera cooler path drives the fan through the actuator seam.</summary>
    [TestFixture]
    [Category("bench")]
    public class CoolingFanInterlockBenchTest {

        private static ScriptedAlpacaDevice ThermalSwitchBox() => ScriptedAlpacaDevice.Start(path =>
            path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Fan\""
            : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
            : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
            : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
            : null);

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box, DeviceType type, string name) => new(
            UniqueId: $"{type}-under-test", Name: name, Type: type,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static CameraDto Camera(bool coolerOn) => new("cam", "Bench Camera", EquipmentConnectionState.Connected,
            Capabilities: null, Runtime: new CameraStateDto("idle", -5, 60, coolerOn, null));

        private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (await condition()) {
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail(failure);
        }

        private static async Task<SwitchService> ConnectedThermalSwitchAsync(ScriptedAlpacaDevice box, Mock<ICameraService> camera) {
            var svc = new SwitchService(cameraProbe: () => camera.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Switch, "ToupTek Thermal Switch")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync("Switch-under-test", CancellationToken.None)) is { State: EquipmentConnectionState.Connected, Ports.Count: > 0 },
                TimeSpan.FromSeconds(15), "thermal switch never connected with its ports read");
            return svc;
        }

        [Test]
        public async Task A_fan_off_while_the_camera_cools_is_refused() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            using var svc = await ConnectedThermalSwitchAsync(box, camera);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("damage the camera"));
        }

        [Test]
        public async Task A_fan_off_with_an_unreadable_cooler_state_is_refused_fail_closed() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("camera status hung"));
            using var svc = await ConnectedThermalSwitchAsync(box, camera);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("cooler state is unknown"));
        }

        [Test]
        public async Task A_fan_off_with_the_cooler_off_goes_through_and_a_fan_on_never_asks() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: false));
            using var svc = await ConnectedThermalSwitchAsync(box, camera);

            Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            camera.Verify(c => c.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 1), CancellationToken.None));
            camera.Verify(c => c.GetAsync(It.IsAny<CancellationToken>()), Times.Once, "a fan ON never consults the camera");
        }

        [Test]
        public async Task The_camera_cooler_write_drives_the_fan_through_the_actuator() {
            // A scripted camera that answers every read with a default ("true"/unparseable → the
            // per-field fallbacks) and accepts every PUT: enough to connect and take a cooler write.
            await using var box = ScriptedAlpacaDevice.Start(_ => null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            using var svc = new CameraService(fan: () => actuator.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            await svc.SetCoolerAsync(enabled: true, targetTemperatureC: -10, CancellationToken.None);

            actuator.Verify(a => a.SetFanValueAsync("sw-5", It.Is<SwitchValueRequestDto>(r => r.PortId == 1 && r.Value == 1), It.IsAny<CancellationToken>()),
                Times.Once, "cooler-on drives the fan to the port's max");
        }
    }
}
