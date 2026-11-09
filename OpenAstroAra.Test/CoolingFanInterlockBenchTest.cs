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
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
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

        private static int Probes(Mock<ICameraService> camera) {
            lock (camera.Invocations) { // Moq's list is not safe to enumerate under a concurrent add
                return camera.Invocations.Count(i => i.Method.Name == nameof(ICameraService.GetAsync));
            }
        }

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

        private static InMemoryProfileStore Designating(string deviceId, int portId) {
            var store = new InMemoryProfileStore();
            store.PutCoolingFan(new CoolingFanDto(deviceId, portId));
            return store;
        }

        // Named like no fan on purpose: the interlock follows the profile's designation, not the device.
        private static async Task<SwitchService> ConnectedFanSwitchAsync(ScriptedAlpacaDevice box, Mock<ICameraService> camera,
                IProfileStore? store = null, string name = "Pegasus UPBv2") {
            var svc = new SwitchService(profileStore: store ?? Designating("Switch-under-test", 0), cameraProbe: () => camera.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Switch, name)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync("Switch-under-test", CancellationToken.None)) is { State: EquipmentConnectionState.Connected, Ports.Count: > 0 },
                TimeSpan.FromSeconds(15), "switch never connected with its ports read");
            return svc;
        }

        [Test]
        public async Task A_fan_off_while_the_camera_cools_is_refused() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            using var svc = await ConnectedFanSwitchAsync(box, camera);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("damage the camera"));
        }

        [Test]
        public async Task A_fan_off_with_an_unreadable_cooler_state_is_refused_fail_closed() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("camera status hung"));
            using var svc = await ConnectedFanSwitchAsync(box, camera);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("cooler state is unknown"));
        }

        [Test]
        public async Task A_fan_off_with_the_cooler_off_goes_through_and_a_fan_on_never_asks() {
            await using var box = ThermalSwitchBox();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: false));
            using var svc = await ConnectedFanSwitchAsync(box, camera);
            // #1076 — connect itself probes the camera exactly once (the late-connect fan catch-up:
            // cooler off → nothing to catch up, no further probe). Wait for that probe rather than
            // sleeping, so the counts below are deterministic.
            await WaitForAsync(() => Task.FromResult(Probes(camera) == 1), TimeSpan.FromSeconds(5), "the connect-time catch-up never probed the camera");
            const int probesAfterConnect = 1;

            await Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None));
            camera.Verify(c => c.GetAsync(It.IsAny<CancellationToken>()), Times.Exactly(probesAfterConnect + 1));
            await Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 1), CancellationToken.None));
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
            using var svc = new CameraService(faults: sink.Object, fan: () => actuator.Object, profileStore: Designating("sw-5", 1));
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            // Cooler OFF (the §58 warm ramp's final step): the fan-off write fails — the call must still
            // return normally so the ramp completes, and the failure is an op_error fault on the switch.
            await Assert.DoesNotThrowAsync(() => svc.SetCoolerAsync(enabled: false, targetTemperatureC: null, CancellationToken.None));
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
            using var svc = new CameraService(fan: () => actuator.Object, profileStore: Designating("sw-5", 1));
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
            using var svc = await ConnectedFanSwitchAsync(box, camera);

            var ex = await Assert.ThrowsAsync<OpenAstroAra.Core.Model.SequenceEntityFailedException>(
                () => ((OpenAstroAra.Equipment.Interfaces.Mediator.ISwitchMediator)svc).SetSwitchValue(0, 0, new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("damage the camera"));
        }

        [Test]
        public async Task Cooler_on_refuses_when_the_fan_cannot_be_started_but_cooler_off_still_completes() {
            // #1076 — fan FIRST when enabling: a rig whose fan cannot be started must not START
            // cooling. The scripted camera reports CoolerOn=false, so this is the off→on transition.
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/cooleron", StringComparison.Ordinal) ? "false" : null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 1, Min: 0, Max: 1, CanWrite: true)]); // Value 1: the fan-OFF below is a real (failing) write too
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            using var svc = new CameraService(fan: () => actuator.Object, profileStore: Designating("sw-5", 1));
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetCoolerAsync(enabled: true, targetTemperatureC: -10, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("cooling fan could not be started"));
            // Fan FIRST: the refusal happens before the cooler write, so nothing reached the camera.
            Assert.That(box.Puts.Select(p => p.Path), Has.None.EndsWith("/cooleron").And.None.EndsWith("/setccdtemperature"),
                "a refused cooler-on commits nothing to the camera");
            await Assert.DoesNotThrowAsync(() => svc.SetCoolerAsync(enabled: false, targetTemperatureC: null, CancellationToken.None),
                "cooler-off must still complete (the warm ramp's final step) even if the fan-off write fails");
        }

        [Test]
        public async Task A_failed_fan_write_with_an_unreadable_cooler_state_still_refuses_to_start_cooling() {
            // Review of #1084: CoolerOn unreadable this pass (CoolerStateKnown=false, CoolerOn=false
            // by fallback) must NOT count as "already cooling" — the cooler may well be off, and
            // starting the TEC with the fan down is the hazard. Fail closed: refuse.
            // CoolerOn answers "false" through connect (the capability probe must see a cooler), then
            // the responder is swapped so the runtime read throws — CoolerStateKnown goes false.
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/cooleron", StringComparison.Ordinal) ? "false" : null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            using var svc = new CameraService(fan: () => actuator.Object, profileStore: Designating("sw-5", 1));
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");
            box.Respond(path => path.EndsWith("/cooleron", StringComparison.Ordinal) ? "\"not-a-bool\"" : null);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.Runtime is { CoolerStateKnown: false },
                TimeSpan.FromSeconds(15), "the cooler state never became unreadable on a refresh");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetCoolerAsync(enabled: true, targetTemperatureC: -10, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("cooling fan could not be started"));
            Assert.That(box.Puts.Select(p => p.Path), Has.None.EndsWith("/cooleron"), "nothing committed to the camera");
        }

        [Test]
        public async Task A_failed_fan_write_with_the_cooler_already_on_is_a_fault_not_a_refusal_so_the_warm_ramp_completes() {
            // Review of #1084: the §58 unattended warm ramp calls SetCoolerAsync(true, setpoint) once
            // a minute while the cooler is ON. A fan write failing mid-ramp must not throw — that
            // would abort the ramp and leave the TEC cooling at a mid-ramp set-point overnight.
            // The scripted camera answers "true" for CoolerOn: the cooler is already on.
            await using var box = ScriptedAlpacaDevice.Start(_ => null);
            var actuator = new Mock<ICoolingFanActuator>();
            var thermal = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { thermal });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            var faults = new List<EquipmentFaultEvent>();
            var sink = new Mock<IEquipmentFaultSink>();
            sink.Setup(f => f.Publish(It.IsAny<EquipmentFaultEvent>())).Callback<EquipmentFaultEvent>(f => { lock (faults) { faults.Add(f); } });
            using var svc = new CameraService(faults: sink.Object, fan: () => actuator.Object, profileStore: Designating("sw-5", 1));
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.Runtime.CoolerOn == true,
                TimeSpan.FromSeconds(15), "camera never connected with its cooler reported on");

            await Assert.DoesNotThrowAsync(() => svc.SetCoolerAsync(enabled: true, targetTemperatureC: -5, CancellationToken.None),
                "a ramp step (cooler already on) survives a failed fan write");
            actuator.Verify(a => a.SetFanValueAsync("sw-5", It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()), Times.Once,
                "the fan-on is still attempted on every cooler-on (a stale cached value never skips it)");
            lock (faults) {
                Assert.That(faults, Has.Count.EqualTo(1));
                Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.OpError));
                Assert.That(faults[0].Details, Does.Contain("cooling fan could not be started"));
            }
        }

        private static ScriptedAlpacaDevice FanReadingZero() => ScriptedAlpacaDevice.Start(path =>
            path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Fan\""
            : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
            : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "0"
            : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
            : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
            : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
            : null);

        private static bool FanWritten(ScriptedAlpacaDevice box) =>
            box.Puts.Any(p => p.Path.EndsWith("/setswitchvalue", StringComparison.Ordinal));

        [Test]
        public async Task A_designated_switch_connecting_while_the_camera_cools_has_its_fan_started() {
            // #1076 — the fan sync only fires on a cooler write, so the fan's switch connecting
            // AFTER the cooler was turned on is caught up at connect: the fan port (reading 0 here)
            // gets a setswitchvalue PUT to the port's max. Delete the catch-up Task.Run in
            // ConnectInBackground and no PUT ever reaches the box.
            await using var box = FanReadingZero();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            using var svc = await ConnectedFanSwitchAsync(box, camera);

            await WaitForAsync(() => Task.FromResult(FanWritten(box)),
                TimeSpan.FromSeconds(10), "the fan was never started for the already-cooling camera");
            var put = box.Puts.First(p => p.Path.EndsWith("/setswitchvalue", StringComparison.Ordinal)).Body;
            Assert.That(put, Does.Contain("Id=0").And.Contain("Value=1"), "the Fan port (0) is driven to its max (1)");
        }

        [Test]
        public async Task An_undesignated_Thermal_Switch_is_neither_actuated_nor_blocked_while_the_camera_cools() {
            await using var box = FanReadingZero();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            using var svc = await ConnectedFanSwitchAsync(box, camera, store: new InMemoryProfileStore(), name: "ToupTek Thermal Switch");

            await Assert.DoesNotThrowAsync(() => svc.SetValueAsync("Switch-under-test", new SwitchValueRequestDto(0, 0), CancellationToken.None), "never blocked");
            await Task.Delay(TimeSpan.FromSeconds(3)); // past the catch-up's window for a read snapshot
            Assert.That(box.Puts.Count(p => p.Path.EndsWith("/setswitchvalue", StringComparison.Ordinal)), Is.EqualTo(1), "only the user's own write: the catch-up never ran");
        }

        [Test]
        public async Task A_fan_designated_while_the_camera_cools_starts_at_once() {
            await using var box = FanReadingZero();
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            var store = new InMemoryProfileStore();
            using var svc = await ConnectedFanSwitchAsync(box, camera, store: store);
            Assert.That(FanWritten(box), Is.False, "nothing is designated yet");

            store.PutCoolingFan(new CoolingFanDto("Switch-under-test", 0));

            await WaitForAsync(() => Task.FromResult(FanWritten(box)), TimeSpan.FromSeconds(10), "the newly designated fan was not started");
        }

        [Test]
        public async Task A_fan_designated_on_a_switch_that_is_not_connected_raises_no_fault() {
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            var faults = new List<EquipmentFaultEvent>();
            var sink = new Mock<IEquipmentFaultSink>();
            sink.Setup(f => f.Publish(It.IsAny<EquipmentFaultEvent>())).Callback<EquipmentFaultEvent>(f => { lock (faults) { faults.Add(f); } });
            var store = new InMemoryProfileStore();
            using var svc = new SwitchService(faults: sink.Object, profileStore: store, cameraProbe: () => camera.Object);

            // What a profile switch does: the new designation lands before its switch connects.
            store.PutCoolingFan(new CoolingFanDto("not-connected-yet", 0));
            await Task.Delay(TimeSpan.FromSeconds(1));

            lock (faults) {
                Assert.That(faults, Is.Empty, "the switch's own connect starts the fan");
            }
        }

        [Test]
        public async Task The_catch_up_waits_for_the_fan_port_not_just_for_some_ports() {
            // Two ports; the fan (id 1) fails its reads at first, so the snapshot holds only port 0.
            var fanReadable = false;
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "2"
                : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Output\""
                : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
                : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
                : null);
            box.RespondWithQuery((path, query) =>
                !fanReadable && path.EndsWith("/getswitchvalue", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(query, "(^|[?&])id=1(&|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? ScriptedAlpacaDevice.Error("port busy") : null);
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            var svc = new SwitchService(profileStore: Designating("Switch-under-test", 1), cameraProbe: () => camera.Object) { RefreshPeriod = TimeSpan.FromMilliseconds(200) };
            using var _ = svc;
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Switch, "Pegasus UPBv2")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync("Switch-under-test", CancellationToken.None)) is { Ports.Count: 1 },
                TimeSpan.FromSeconds(15), "the partial snapshot never landed");
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.That(FanWritten(box), Is.False, "the fan port hasn't been read yet");

            fanReadable = true;

            await WaitForAsync(() => Task.FromResult(FanWritten(box)), TimeSpan.FromSeconds(10), "the catch-up gave up on the partial snapshot");
            Assert.That(box.Puts.First(p => p.Path.EndsWith("/setswitchvalue", StringComparison.Ordinal)).Body, Does.Contain("Id=1"));
        }

        [Test]
        public async Task A_cooling_camera_whose_designated_port_never_reads_raises_a_fault() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/getswitchname", StringComparison.Ordinal) ? "\"Output\""
                : path.EndsWith("/getswitchdescription", StringComparison.Ordinal) ? "\"\""
                : path.EndsWith("/getswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/minswitchvalue", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/maxswitchvalue", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/switchstep", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/canwrite", StringComparison.Ordinal) ? "true"
                : null);
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Camera(coolerOn: true));
            var faults = new List<EquipmentFaultEvent>();
            var sink = new Mock<IEquipmentFaultSink>();
            sink.Setup(f => f.Publish(It.IsAny<EquipmentFaultEvent>())).Callback<EquipmentFaultEvent>(f => { lock (faults) { faults.Add(f); } });
            // Port 7 doesn't exist on this one-port switch, so the wait runs out.
            using var svc = new SwitchService(faults: sink.Object, profileStore: Designating("Switch-under-test", 7), cameraProbe: () => camera.Object);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Switch, "Pegasus UPBv2")), null, CancellationToken.None);

            await WaitForAsync(() => { lock (faults) { return Task.FromResult(faults.Count > 0); } }, TimeSpan.FromSeconds(20), "no fault for the fan that could not be started");
            lock (faults) {
                Assert.That(faults[0].Details, Does.Contain("cooling fan"));
            }
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<CameraService> {
            public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (Lines) { Lines.Add((logLevel, formatter(state, exception))); }
            }
            public int NoFanWarnings() { lock (Lines) { return Lines.Count(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Text.Contains("no cooling fan designated", StringComparison.Ordinal)); } }
        }

        [TestCase(false, 1, TestName = "Cooling_start_with_no_designation_logs_a_warning_and_drives_no_fan")]
        [TestCase(true, 0, TestName = "Cooling_start_with_a_designation_does_not_log_the_warning")]
        public async Task Cooling_start_logs_a_missing_designation(bool designated, int warnings) {
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/cooleron", StringComparison.Ordinal) ? "false" : null);
            var actuator = new Mock<ICoolingFanActuator>();
            var named = new SwitchDto("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]);
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> { named });
            var log = new CapturingLogger();
            using var svc = new CameraService(logger: log, fan: () => actuator.Object,
                profileStore: designated ? Designating("sw-5", 1) : new InMemoryProfileStore());
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera, "Bench Camera")), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "camera never connected");

            await svc.SetCoolerAsync(enabled: true, targetTemperatureC: -10, CancellationToken.None);

            Assert.That(log.NoFanWarnings(), Is.EqualTo(warnings));
            actuator.Verify(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()),
                designated ? Times.Once() : Times.Never(), "an undesignated Thermal Switch is never actuated");
        }

        [Test]
        public async Task The_cooling_fan_persists_per_profile_and_an_older_file_has_none() {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ara-cooling-fan-" + System.IO.Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                Assert.That(store.GetCoolingFan(), Is.EqualTo(CoolingFanDto.None), "no designation by default");
                store.PutCoolingFan(new CoolingFanDto("sw-5", 1));
                Assert.That(new FileProfileStore(dir).GetCoolingFan(), Is.EqualTo(new CoolingFanDto("sw-5", 1)), "persisted");

                var captured = ProfileStoreSnapshot.Capture(store);
                ProfileStoreSnapshot.Apply(store, captured with { CoolingFan = null });
                Assert.That(store.GetCoolingFan(), Is.EqualTo(CoolingFanDto.None), "selecting a profile with none doesn't inherit the previous rig's fan");
                ProfileStoreSnapshot.Apply(store, captured);
                Assert.That(store.GetCoolingFan().PortId, Is.EqualTo(1), "and back");

                var path = System.IO.Path.Combine(dir, "profile.json");
                var node = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!.AsObject();
                Assert.That(node.Remove("cooling_fan"), Is.True, "the modern file carries the section");
                await System.IO.File.WriteAllTextAsync(path, node.ToJsonString());
                Assert.That(new FileProfileStore(dir).GetCoolingFan(), Is.EqualTo(CoolingFanDto.None), "a pre-#1700 file loads with no designation");
            } finally {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
