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
    /// Sim-free unit coverage for <see cref="SafetyMonitorService"/> — runs in the normal
    /// test suite (no Alpaca simulator). Verifies the §60.5 connection state machine and
    /// that a real ASCOM Alpaca client is constructed and its failure is contained: a
    /// connect to a dead endpoint must end in <see cref="EquipmentConnectionState.Error"/>,
    /// never crash the background task. (The happy-path Connected case needs a live device
    /// and lives in the <c>[Category("Integration")]</c> companion test.)
    /// </summary>
    [TestFixture]
    public class SafetyMonitorServiceTest {

        [Test]
        public async Task GetAsync_is_null_before_any_device_is_selected() {
            using var svc = new SafetyMonitorService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new SafetyMonitorService();

            // 127.0.0.1:1 has nothing listening -> the Alpaca HTTP connect is refused fast.
            var dead = new DiscoveredDeviceDto(
                UniqueId: "unit-test-uid",
                Name: "Unreachable SafetyMonitor",
                Type: DeviceType.SafetyMonitor,
                HostName: "127.0.0.1",
                IpAddress: "127.0.0.1",
                IpPort: 1,
                AlpacaDeviceNumber: 0,
                UseHttps: false);

            await svc.ConnectAsync(new ConnectRequestDto(dead), idempotencyKey: null, CancellationToken.None);

            var dto = await PollUntilNotConnectingAsync(svc);
            Assert.That(dto, Is.Not.Null, "connect never left the Connecting state");
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error),
                "an unreachable device must demote the connection to Error");
            Assert.That(dto.DeviceId, Is.EqualTo("unit-test-uid"));
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new SafetyMonitorService();
            var dead = new DiscoveredDeviceDto(
                "unit-test-uid", "Unreachable", DeviceType.SafetyMonitor,
                "127.0.0.1", "127.0.0.1", 1, 0, false);

            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc); // settle to Error

            await svc.DisconnectAsync(null, CancellationToken.None);
            var dto = await svc.GetAsync(CancellationToken.None);
            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SafetyMonitorService();
            svc.Dispose();
            var dead = new DiscoveredDeviceDto(
                "uid", "Device", DeviceType.SafetyMonitor, "127.0.0.1", "127.0.0.1", 1, 0, false);
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SafetyMonitorService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync(null, CancellationToken.None); });
        }

        [Test]
        public void GetAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SafetyMonitorService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(
                () => svc.GetAsync(CancellationToken.None));
        }

        // Polls up to ~15s for the connection to leave Connecting (HTTP connect-refused on a
        // dead port is fast, but allow generous slack for a loaded CI runner).
        private static async Task<SafetyMonitorDto?> PollUntilNotConnectingAsync(SafetyMonitorService svc) {
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
