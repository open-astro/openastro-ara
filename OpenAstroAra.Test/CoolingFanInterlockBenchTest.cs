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
using System.Linq;
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
            // #1076 — connect itself probes the camera once (the late-connect fan catch-up); count from here.
            await Task.Delay(300);
            var probesAfterConnect = camera.Invocations.Count(i => i.Method.Name == nameof(ICameraService.GetAsync));

            Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            camera.Verify(c => c.GetAsync(It.IsAny<CancellationToken>()), Times.Exactly(probesAfterConnect + 1));
            Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 1), CancellationToken.None));
            camera.Verify(c => c.GetAsync(It.IsAny<CancellationToken>()), Times.Exactly(probesAfterConnect + 1), "a fan ON never consults the camera");
        }

        [Test]
        public async Task A_failed_fan_write_never_fails_the_cooler_call_and_publishes_an_op_error_fault() {
            await using var box = ScriptedAlpacaDevice.Start(_ => null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 1, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            var faults = new List<EquipmentFaultEvent>();
            var sink = new Mock<IEquipmentFaultSink>();
            sink.Setup(f => f.Publish(It.IsAny<EquipmentFaultEvent>())).Callback<EquipmentFaultEvent>(f => { lock (faults) { faults.Add(f); } });
            using var svc = new CameraService(faults: sink.Object, fan: () => actuator.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            // Cooler OFF (the §58 warm ramp's final step): the fan-off write fails — the call must still
            // return normally so the ramp completes, and the failure is an op_error fault on the switch.
            Assert.DoesNotThrowAsync(() => svc.SetCoolerAsync(enabled: false, targetTemperatureC: null, CancellationToken.None));
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1));
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.OpError));
                Assert.That(faults[0].DeviceType, Is.EqualTo(DeviceType.Switch));
                Assert.That(faults[0].DeviceId, Is.EqualTo("sw-5"));
                Assert.That(faults[0].Details, Does.Contain("cooling fan could not be stopped"));
            }
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

        [Test]
        public async Task A_sequence_SetSwitchValue_that_would_stop_the_fan_while_cooling_fails_the_instruction() {
            // #1076 — the sequencer's write goes through the same interlock as the REST write.
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            using var svc = await ConnectedThermalSwitchAsync(box, camera);

            var ex = Assert.ThrowsAsync<OpenAstroAra.Core.Model.SequenceEntityFailedException>(
                () => ((OpenAstroAra.Equipment.Interfaces.Mediator.ISwitchMediator)svc).SetSwitchValue(0, 0, new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("damage the camera"));
        }

        [Test]
        public async Task Cooler_on_refuses_when_the_fan_cannot_be_started_but_cooler_off_still_completes() {
            // #1076 — fan FIRST when enabling: a rig whose fan cannot be started must not cool.
            await using var box = ScriptedAlpacaDevice.Start(_ => null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            using var svc = new CameraService(fan: () => actuator.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetCoolerAsync(enabled: true, targetTemperatureC: -10, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("cooling fan could not be started"));
            Assert.DoesNotThrowAsync(() => svc.SetCoolerAsync(enabled: false, targetTemperatureC: null, CancellationToken.None),
                "cooler-off must still complete (the warm ramp's final step) even if the fan-off write fails");
        }
    }
}
