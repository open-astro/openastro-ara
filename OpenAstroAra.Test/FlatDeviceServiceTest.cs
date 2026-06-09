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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for <see cref="FlatDeviceService"/> — the seventh real device service
    /// (cover + calibrator-light control). Mirrors the prior suites; the live happy path lives in
    /// the <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class FlatDeviceServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new FlatDeviceService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new FlatDeviceService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable Flat", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new FlatDeviceService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void ApplyFlatPanelAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new FlatDeviceService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(OpenCover: true), null, CancellationToken.None); });
        }

        [Test]
        public void ApplyFlatPanelAsync_with_negative_brightness_throws_ArgumentOutOfRange() {
            using var svc = new FlatDeviceService();
            // Negative brightness is out of range regardless of the (unknown) max, so this is
            // reachable without a sim and fires before the connected check.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(Brightness: -1), null, CancellationToken.None); });
        }

        [Test]
        public void IsBrightnessOutOfRange_enforces_zero_to_max_when_known() {
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 0), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 100), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, 101), Is.True, "above max");
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(100, -1), Is.True);
            // Unknown (null) or zero max -> only negatives rejected; the device validates the upper.
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(null, 9999), Is.False);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(null, -1), Is.True);
            Assert.That(FlatDeviceService.IsBrightnessOutOfRange(0, 9999), Is.False);
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.FlatDevice, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void ApplyFlatPanelAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FlatDeviceService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(OpenCover: true), null, CancellationToken.None); });
        }

        private static async Task<FlatDeviceDto?> PollUntilNotConnectingAsync(FlatDeviceService svc) {
            for (var i = 0; i < 150; i++) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto is not null && dto.State != EquipmentConnectionState.Connecting) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            return await svc.GetAsync(CancellationToken.None);
        }
    }
}
