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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for <see cref="FilterWheelService"/> — the sixth real device service
    /// (control action: change slot). Mirrors the prior suites; the live happy path lives in the
    /// <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class FilterWheelServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new FilterWheelService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new FilterWheelService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable FW", DeviceType.FilterWheel, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
            Assert.That(dto.Slots, Is.Empty);
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new FilterWheelService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.FilterWheel, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc);
            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void ChangeFilterAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new FilterWheelService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.ChangeFilterAsync(new FilterChangeRequestDto(0), null, CancellationToken.None); });
        }

        [Test]
        public void ChangeFilterAsync_with_position_beyond_short_throws_ArgumentOutOfRange() {
            using var svc = new FilterWheelService();
            // > short.MaxValue would silently wrap on the (short) cast — must throw (before the
            // connected check, so it's reachable without a sim).
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.ChangeFilterAsync(new FilterChangeRequestDto(40000), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.ChangeFilterAsync(new FilterChangeRequestDto(-1), null, CancellationToken.None); });
        }

        [Test]
        public void IsPositionOutOfRange_enforces_slot_bounds() {
            var slots = new List<FilterSlotDto> {
                new(0, "L", 0), new(1, "R", 0), new(2, "G", 0), new(3, "B", 0), new(4, "Ha", 0),
            };
            Assert.That(FilterWheelService.IsPositionOutOfRange(slots, 0), Is.False);
            Assert.That(FilterWheelService.IsPositionOutOfRange(slots, 4), Is.False);
            Assert.That(FilterWheelService.IsPositionOutOfRange(slots, 5), Is.True, "above last slot");
            Assert.That(FilterWheelService.IsPositionOutOfRange(slots, -1), Is.True);
            // Unknown (null) or empty slot list -> device validates (not rejected locally).
            Assert.That(FilterWheelService.IsPositionOutOfRange(null, 99), Is.False);
            Assert.That(FilterWheelService.IsPositionOutOfRange(new List<FilterSlotDto>(), 99), Is.False);
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FilterWheelService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto("uid", "D", DeviceType.FilterWheel, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FilterWheelService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FilterWheelService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAsync(CancellationToken.None));
        }

        [Test]
        public void ChangeFilterAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new FilterWheelService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ChangeFilterAsync(new FilterChangeRequestDto(0), null, CancellationToken.None); });
        }

        private static async Task<FilterWheelDto?> PollUntilNotConnectingAsync(FilterWheelService svc) {
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
