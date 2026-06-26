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
    /// Sim-free unit coverage for <see cref="TelescopeService"/> — the ninth real device service
    /// (control actions: slew/sync, park/unpark, set-tracking, abort-slew). Mirrors the prior suites;
    /// the live happy path lives in the <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class TelescopeServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new TelescopeService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new TelescopeService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable Mount", DeviceType.Telescope,
                "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new TelescopeService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Telescope, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void SlewAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(12.0, 45.0), null, CancellationToken.None); });
        }

        [Test]
        public void SlewAsync_with_out_of_range_coordinates_throws_ArgumentOutOfRange() {
            using var svc = new TelescopeService();
            // Range is validated before the connected check, so these fire without a sim.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(24.0, 0.0), null, CancellationToken.None); }, "RA 24h wraps");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(-0.1, 0.0), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(0.0, 90.1), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(0.0, -90.1), null, CancellationToken.None); });
        }

        [Test]
        public void ParkAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None); });
        }

        [Test]
        public void UnparkAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.UnparkAsync(null, CancellationToken.None); });
        }

        [Test]
        public void FindHomeAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.FindHomeAsync(null, CancellationToken.None); });
        }

        [Test]
        public void MoveAxisAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.MoveAxisAsync(0, 1.5, CancellationToken.None));
        }

        [Test]
        public void SetTrackingAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetTrackingAsync(true, CancellationToken.None));
        }

        [Test]
        public void AbortSlewAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new TelescopeService();
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.AbortSlewAsync(CancellationToken.None));
        }

        [Test]
        public void IsCoordinateOutOfRange_enforces_ra_0_to_24_and_dec_minus90_to_90() {
            Assert.That(TelescopeService.IsCoordinateOutOfRange(0, 0), Is.False);
            Assert.That(TelescopeService.IsCoordinateOutOfRange(23.999, 89.9), Is.False);
            Assert.That(TelescopeService.IsCoordinateOutOfRange(0, 90), Is.False, "+90 (north pole) is valid");
            Assert.That(TelescopeService.IsCoordinateOutOfRange(0, -90), Is.False, "-90 (south pole) is valid");
            Assert.That(TelescopeService.IsCoordinateOutOfRange(24, 0), Is.True, "24h wraps to 0");
            Assert.That(TelescopeService.IsCoordinateOutOfRange(-0.01, 0), Is.True);
            Assert.That(TelescopeService.IsCoordinateOutOfRange(0, 90.01), Is.True);
            Assert.That(TelescopeService.IsCoordinateOutOfRange(0, -90.01), Is.True);
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.Telescope, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void SlewAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            // Out-of-range coordinates, but the dispose check runs first, so ObjectDisposedException
            // wins over ArgumentOutOfRangeException (ordering aligned with the other services).
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.SlewAsync(new SlewRequestDto(24.0, 0.0), null, CancellationToken.None); });
        }

        [Test]
        public void ParkAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ParkAsync(new ParkRequestDto(), null, CancellationToken.None); });
        }

        [Test]
        public void UnparkAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.UnparkAsync(null, CancellationToken.None); });
        }

        [Test]
        public void SetTrackingAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.SetTrackingAsync(false, CancellationToken.None));
        }

        [Test]
        public void AbortSlewAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.AbortSlewAsync(CancellationToken.None));
        }

        private static async Task<TelescopeDto?> PollUntilNotConnectingAsync(TelescopeService svc) {
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
