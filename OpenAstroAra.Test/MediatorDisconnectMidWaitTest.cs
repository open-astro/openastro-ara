#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.2/§42.3 — the disconnect-mid-wait regression harness promised in the #800
    /// review arc: for every peripheral mediator with a settle wait, a device that drops (here: a
    /// user disconnect superseding the client) while the op is still confirming must FAIL the
    /// instruction with <see cref="SequenceEntityFailedException"/> — never report the op settled
    /// at an unknown position. Each bench drives a real service against a scripted loopback device
    /// that dispatches the op fine but reports "still moving" forever, then disconnects mid-wait.</summary>
    [TestFixture]
    [Category("bench")] // §42.4 virtual-observatory bench — loopback-only, runs in the default job too
    public class MediatorDisconnectMidWaitTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box, DeviceType type) => new(
            UniqueId: $"{type}-under-test", Name: $"Bench {type}", Type: type,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        // The op has dispatched over loopback (ms) long before this elapses, so the disconnect
        // always lands inside the settle wait (which polls for 60s+ against a never-settling device).
        private static readonly TimeSpan MidWait = TimeSpan.FromSeconds(1);

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

        [Test]
        public async Task A_focuser_disconnect_mid_settle_fails_the_move_instruction() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/ismoving", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/position", StringComparison.Ordinal) ? "5000"
                : path.EndsWith("/absolute", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/maxstep", StringComparison.Ordinal) ? "10000"
                : path.EndsWith("/maxincrement", StringComparison.Ordinal) ? "1000"
                : path.EndsWith("/tempcompavailable", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/tempcomp", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/temperature", StringComparison.Ordinal) ? "10.0"
                : path.EndsWith("/stepsize", StringComparison.Ordinal) ? "1.0"
                : null);
            using var svc = new FocuserService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Focuser)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "focuser never connected");

            var move = ((IFocuserMediator)svc).MoveFocuser(6000, CancellationToken.None);
            await Task.Delay(MidWait);
            Assert.That(move.IsCompleted, Is.False, "the wait is still confirming against the never-settling device");
            await svc.DisconnectAsync(null, CancellationToken.None);

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => move,
                "a focuser at an unknown position must not read as a completed move");
        }

        [Test]
        public async Task A_rotator_disconnect_mid_settle_fails_the_move_instruction() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/ismoving", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/mechanicalposition", StringComparison.Ordinal) ? "10.0"
                : path.EndsWith("/position", StringComparison.Ordinal) ? "10.0"
                : path.EndsWith("/canreverse", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/reverse", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/stepsize", StringComparison.Ordinal) ? "1.0"
                : null);
            using var svc = new RotatorService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Rotator)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "rotator never connected");

            var move = ((IRotatorMediator)svc).MoveMechanical(90f, CancellationToken.None);
            await Task.Delay(MidWait);
            Assert.That(move.IsCompleted, Is.False, "the wait is still confirming against the never-settling device");
            await svc.DisconnectAsync(null, CancellationToken.None);

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => move,
                "a rotator at an unknown angle must not read as a completed move");
        }

        [Test]
        public async Task A_filter_wheel_disconnect_mid_settle_fails_the_change_instruction() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/position", StringComparison.Ordinal) ? "-1" // ASCOM: -1 while rotating
                : path.EndsWith("/names", StringComparison.Ordinal) ? "[\"L\",\"R\"]"
                : path.EndsWith("/focusoffsets", StringComparison.Ordinal) ? "[0,0]"
                : null);
            using var svc = new FilterWheelService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.FilterWheel)), null, CancellationToken.None);
            await WaitForAsync(async () => {
                var dto = await svc.GetAsync(CancellationToken.None);
                return dto?.State == EquipmentConnectionState.Connected && dto.Slots.Count == 2;
            }, TimeSpan.FromSeconds(15), "filter wheel never connected with its slots read");

            var change = ((IFilterWheelMediator)svc).ChangeFilter(new FilterInfo("R", 0, 1), null, CancellationToken.None);
            await Task.Delay(MidWait);
            Assert.That(change.IsCompleted, Is.False, "the wait is still confirming against the never-landing wheel");
            await svc.DisconnectAsync(null, CancellationToken.None);

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => change,
                "a wheel that never confirmed its slot must not hand back the requested filter");
        }

        [Test]
        public async Task A_mount_disconnect_mid_slew_fails_the_slew_instruction() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/slewing", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/tracking", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "5.5"
                : path.EndsWith("/declination", StringComparison.Ordinal) ? "20.0"
                : null);
            using var svc = new TelescopeService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Telescope)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "mount never connected");

            var target = new Coordinates(Angle.ByHours(5.5), Angle.ByDegree(20.0), Epoch.JNOW);
            var slew = ((ITelescopeMediator)svc).SlewToCoordinatesAsync(target, CancellationToken.None);
            await Task.Delay(MidWait);
            Assert.That(slew.IsCompleted, Is.False, "the wait is still confirming against the never-settling mount");
            await svc.DisconnectAsync(null, CancellationToken.None);

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => slew,
                "a mount at an unknown pointing must not read as a completed slew");
        }

        [Test]
        public async Task A_dome_disconnect_mid_slew_fails_the_slew_instruction() {
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/slewing", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/azimuth", StringComparison.Ordinal) ? "100.0"
                : path.EndsWith("/shutterstatus", StringComparison.Ordinal) ? "1"
                : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/canslave", StringComparison.Ordinal) ? "false"
                : path.EndsWith("/slaved", StringComparison.Ordinal) ? "false"
                : null);
            using var svc = new DomeService();
            await svc.ConnectAsync(new ConnectRequestDto(Device(box, DeviceType.Dome)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None))?.State == EquipmentConnectionState.Connected,
                TimeSpan.FromSeconds(15), "dome never connected");

            var slew = ((IDomeMediator)svc).SlewToAzimuth(200.0, CancellationToken.None);
            await Task.Delay(MidWait);
            Assert.That(slew.IsCompleted, Is.False, "the wait is still confirming against the never-settling dome");
            await svc.DisconnectAsync(null, CancellationToken.None);

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => slew,
                "a dome at an unknown azimuth must not read as a completed slew");
        }
    }
}
