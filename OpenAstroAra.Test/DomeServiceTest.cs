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
    /// Sim-free unit coverage for <see cref="DomeService"/> — the eighth real device service (four
    /// control actions: slew, park, open/close shutter). Mirrors the prior suites; the live happy
    /// path lives in the <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class DomeServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new DomeService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new DomeService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable Dome", DeviceType.Dome,
                "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new DomeService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Dome, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void SlewAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new DomeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.SlewAsync(new DomeSlewRequestDto(180), null, CancellationToken.None); });
        }

        [Test]
        public void SlewAsync_with_out_of_range_azimuth_throws_ArgumentOutOfRange() {
            using var svc = new DomeService();
            // Range is validated before the connected check, so this fires without a sim.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new DomeSlewRequestDto(360), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new DomeSlewRequestDto(-1), null, CancellationToken.None); });
        }

        [Test]
        public void ParkAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new DomeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.ParkAsync(null, CancellationToken.None); });
        }

        [Test]
        public void OpenShutterAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new DomeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.OpenShutterAsync(null, CancellationToken.None); });
        }

        [Test]
        public void CloseShutterAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new DomeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.CloseShutterAsync(null, CancellationToken.None); });
        }

        [Test]
        public void IsAzimuthOutOfRange_enforces_zero_to_360() {
            Assert.That(DomeService.IsAzimuthOutOfRange(0), Is.False);
            Assert.That(DomeService.IsAzimuthOutOfRange(359.9), Is.False);
            Assert.That(DomeService.IsAzimuthOutOfRange(360), Is.True, "360 wraps to 0; not a valid target");
            Assert.That(DomeService.IsAzimuthOutOfRange(-0.1), Is.True);
            Assert.That(DomeService.IsAzimuthOutOfRange(400), Is.True);
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new DomeService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.Dome, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new DomeService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new DomeService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void SlewAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new DomeService();
            svc.Dispose();
            // 360 is out of range, but the dispose check runs first, so ObjectDisposedException wins
            // over ArgumentOutOfRangeException (ordering aligned with the other services).
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.SlewAsync(new DomeSlewRequestDto(360), null, CancellationToken.None); });
        }

        [Test]
        public void ParkAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new DomeService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ParkAsync(null, CancellationToken.None); });
        }

        private static async Task<DomeDto?> PollUntilNotConnectingAsync(DomeService svc) {
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
