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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The camera, focuser and rotator halves of the §42.3 second channel against scripted Alpaca
    /// devices: the essential read failing while the probe answers raises <c>degraded</c> and its
    /// return recovers it; a driver that does not implement the read, or a relative focuser, is never
    /// flagged. (The filter wheel has its own fixture.)
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca devices
    public class DeviceDegradedTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box, DeviceType type) => new(
            UniqueId: $"{type}-under-test", Name: $"Bench {type}", Type: type,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private sealed class Rig {
            public readonly EquipmentFaultHub Hub;
            public readonly List<EquipmentFaultEvent> Faults = new();
            public readonly List<JsonElement> Actions = new();
            public Rig() {
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                    .Callback<string, JsonElement, CancellationToken>((t, e, _) => {
                        if (t == WsEventCatalog.EquipmentFaultActionTaken) { lock (Actions) { Actions.Add(e); } }
                    })
                    .Returns(Task.CompletedTask);
                Hub = new EquipmentFaultHub(ws.Object);
                Hub.Subscribe(f => { lock (Faults) { Faults.Add(f); } });
            }
            public int FaultCount() { lock (Faults) { return Faults.Count; } }
            public int RecoveredCount() { lock (Actions) { return Actions.FindAll(a => a.GetProperty("action").GetString() == "recovered").Count; } }
        }

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
        }

        private static void AssertDegraded(Rig rig, DeviceType type, string reason) {
            lock (rig.Faults) {
                Assert.That(rig.Faults, Has.Count.EqualTo(1), "one fault per episode");
                Assert.That(rig.Faults[0].Kind, Is.EqualTo(EquipmentFaultKind.Degraded));
                Assert.That(rig.Faults[0].DeviceType, Is.EqualTo(type));
                Assert.That(rig.Faults[0].Details, Does.Contain(reason));
            }
        }

        [Test]
        public async Task A_camera_whose_state_read_fails_while_connected_is_degraded_until_it_answers_again() {
            var state = "0"; // Idle
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/camerastate", StringComparison.Ordinal) ? Volatile.Read(ref state)
                : path.EndsWith("/sensortype", StringComparison.Ordinal) ? "0"
                : null);
            var rig = new Rig();
            using var svc = new CameraService(faults: rig.Hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Camera)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
            await Task.Delay(Tick * 5);
            Assert.That(rig.FaultCount(), Is.Zero, "a healthy camera raises nothing");

            Volatile.Write(ref state, ScriptedAlpacaDevice.Error("ASI_ERROR_GENERAL_ERROR"));
            await WaitForAsync(() => rig.FaultCount() >= 1, "no degraded fault after the state read failed three ticks");
            AssertDegraded(rig, DeviceType.Camera, "ASI_ERROR_GENERAL_ERROR");
            Assert.That((await svc.GetAsync(CancellationToken.None))!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            Volatile.Write(ref state, "0");
            await WaitForAsync(() => rig.RecoveredCount() >= 1, "no recovered action after the state read returned");
        }

        [Test]
        public async Task A_rotator_whose_position_read_fails_is_degraded_and_recovers() {
            var position = "12.5";
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/position", StringComparison.Ordinal) ? Volatile.Read(ref position)
                : path.EndsWith("/mechanicalposition", StringComparison.Ordinal) ? "12.5"
                : null);
            var rig = new Rig();
            using var svc = new RotatorService(faults: rig.Hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Rotator)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");

            Volatile.Write(ref position, ScriptedAlpacaDevice.Error("CAA not responding"));
            await WaitForAsync(() => rig.FaultCount() >= 1, "no degraded fault after the position read failed");
            AssertDegraded(rig, DeviceType.Rotator, "CAA not responding");

            Volatile.Write(ref position, "12.5");
            await WaitForAsync(() => rig.RecoveredCount() >= 1, "no recovered action after the position read returned");
        }

        [Test]
        public async Task An_absolute_focuser_whose_position_read_fails_is_degraded() {
            var position = "5000";
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/position", StringComparison.Ordinal) ? Volatile.Read(ref position)
                : path.EndsWith("/absolute", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/maxstep", StringComparison.Ordinal) ? "10000"
                : path.EndsWith("/maxincrement", StringComparison.Ordinal) ? "10000"
                : path.EndsWith("/stepsize", StringComparison.Ordinal) ? "1.0"
                : path.EndsWith("/temperature", StringComparison.Ordinal) ? "10.0"
                : null);
            var rig = new Rig();
            using var svc = new FocuserService(faults: rig.Hub) { RefreshPeriod = Tick };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Focuser)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");

            Volatile.Write(ref position, ScriptedAlpacaDevice.Error("EAF not responding"));
            await WaitForAsync(() => rig.FaultCount() >= 1, "no degraded fault after the position read failed");
            AssertDegraded(rig, DeviceType.Focuser, "EAF not responding");
        }

        [Test]
        public async Task A_focuser_that_does_not_implement_position_or_is_relative_is_never_flagged() {
            foreach (var (absolute, position) in new[] { ("true", ScriptedAlpacaDevice.NotImplemented("no position")), ("false", ScriptedAlpacaDevice.Error("relative: no position")) }) {
                await using var box = ScriptedAlpacaDevice.Start(path =>
                    path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/position", StringComparison.Ordinal) ? position
                    : path.EndsWith("/absolute", StringComparison.Ordinal) ? absolute
                    : path.EndsWith("/maxstep", StringComparison.Ordinal) ? "10000"
                    : path.EndsWith("/maxincrement", StringComparison.Ordinal) ? "10000"
                    : path.EndsWith("/stepsize", StringComparison.Ordinal) ? "1.0"
                    : path.EndsWith("/temperature", StringComparison.Ordinal) ? "10.0"
                    : null);
                var rig = new Rig();
                using var svc = new FocuserService(faults: rig.Hub) { RefreshPeriod = Tick };
                await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Focuser)), null, CancellationToken.None);
                await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
                await Task.Delay(Tick * 8);
                Assert.That(rig.FaultCount(), Is.Zero, $"absolute={absolute}: a missing Position is not a failing device");
            }
        }
    }
}
