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
    /// Sim-free unit coverage for <see cref="SwitchService"/> — the multi-instance Switch service
    /// (switches addressed by their Alpaca UniqueId). Mirrors the SafetyMonitor/ObservingConditions
    /// suites; the live happy path (read ports + write a port) lives in the
    /// <c>[Category("Integration")]</c> companion test.
    /// </summary>
    [TestFixture]
    public class SwitchServiceTest {

        private static DiscoveredDeviceDto Dead(string uid, int deviceNumber) =>
            new(uid, $"Unreachable {deviceNumber}", DeviceType.Switch,
                "127.0.0.1", "127.0.0.1", 1, deviceNumber, false);

        [Test]
        public async Task GetAll_is_empty_and_GetAsync_is_null_before_any_device_is_connected() {
            using var svc = new SwitchService();
            Assert.That(await svc.GetAllAsync(CancellationToken.None), Is.Empty);
            Assert.That(await svc.GetAsync("uid-0", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_to_an_unreachable_device_ends_in_Error() {
            using var svc = new SwitchService();

            await svc.ConnectAsync(new ConnectRequestDto(Dead("unit-test-uid", 0)), null, CancellationToken.None);

            var dto = await PollUntilNotConnectingAsync(svc, "unit-test-uid");
            Assert.That(dto, Is.Not.Null, "connect never left the Connecting state");
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
            Assert.That(dto.AlpacaDeviceNumber, Is.EqualTo(0));
            Assert.That(dto.Ports, Is.Empty, "no ports while not Connected");
        }

        [Test]
        public async Task ConnectAsync_keeps_multiple_switches_addressed_by_unique_id() {
            using var svc = new SwitchService();
            // Two distinct switches (device numbers 0 and 1) — the multi-switch rig. The second connect
            // must NOT evict the first (the single-instance bug this service fixes).
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-0", 0)), null, CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-1", 1)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid-0");
            await PollUntilNotConnectingAsync(svc, "uid-1");

            var all = await svc.GetAllAsync(CancellationToken.None);
            // Both devices are unreachable (→ Error), but the point is both REMAIN in the map — the
            // second connect didn't evict the first (the single-instance bug this fixes).
            Assert.That(all, Has.Count.EqualTo(2), "both switches remain in the map");
            Assert.That(all[0].AlpacaDeviceNumber, Is.EqualTo(0), "list is ordered by device number");
            Assert.That(all[1].AlpacaDeviceNumber, Is.EqualTo(1));

            // Disconnecting one leaves the other untouched; both entries remain (0 now Disconnected,
            // 1 still Error) — disconnected switches stay listed until reconnect/restart.
            await svc.DisconnectAsync("uid-0", null, CancellationToken.None);
            var afterDisconnect = await svc.GetAllAsync(CancellationToken.None);
            Assert.That(afterDisconnect, Has.Count.EqualTo(2), "both switches stay in the list");
            Assert.That((await svc.GetAsync("uid-0", CancellationToken.None))!.State,
                Is.EqualTo(EquipmentConnectionState.Disconnected));
            Assert.That((await svc.GetAsync("uid-1", CancellationToken.None))!.State,
                Is.EqualTo(EquipmentConnectionState.Error), "the other switch is unaffected");
        }

        [Test]
        public async Task ConnectAsync_same_device_number_on_two_hosts_keeps_both() {
            using var svc = new SwitchService();
            // The common two-host rig: a power box and a relay board on separate Alpaca servers are
            // BOTH device number 0. UniqueId addressing must keep both — the old device-number keying
            // made the second connect evict the first (the toggles-vanish bug).
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-A", 0)), null, CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-B", 0)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid-A");
            await PollUntilNotConnectingAsync(svc, "uid-B");

            var all = await svc.GetAllAsync(CancellationToken.None);
            Assert.That(all, Has.Count.EqualTo(2), "same-numbered switches on different hosts coexist");
            Assert.That(all[0].DeviceId, Is.EqualTo("uid-A"), "stable order: number, then id");
            Assert.That(all[1].DeviceId, Is.EqualTo("uid-B"));
        }

        [Test]
        public async Task Reconnecting_a_downed_device_publishes_its_teardown_first() {
            // §60.9 — a WS subscriber tracking state deltas must see the stale
            // connection go away (equipment.disconnected) before the fresh
            // Connecting, not a silent replacement.
            var events = new System.Collections.Generic.List<(string Type, string? DeviceId)>();
            var broadcaster = new Moq.Mock<IWsBroadcaster>();
            broadcaster
                .Setup(b => b.PublishAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<System.Text.Json.JsonElement>(), Moq.It.IsAny<CancellationToken>()))
                .Returns<string, System.Text.Json.JsonElement, CancellationToken>((type, payload, _) => {
                    lock (events) {
                        events.Add((type, payload.GetProperty("device_id").GetString()));
                    }
                    return Task.CompletedTask;
                });
            using var svc = new SwitchService(events: new EquipmentEventPublisher(broadcaster.Object));

            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-A", 0)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid-A"); // unreachable → Error
            var countBeforeReconnect = 0;
            lock (events) { countBeforeReconnect = events.Count; }
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-A", 0)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid-A");

            (string, string?)[] snapshot;
            lock (events) { snapshot = events.ToArray(); }
            var tail = snapshot[countBeforeReconnect..];
            Assert.That(tail, Does.Contain(("equipment.disconnected", "uid-A")),
                "the stale connection's teardown must be visible on the stream");
            // Every transition publishes equipment.state_changed (the teardown's own included), so
            // "teardown precedes the fresh Connecting" = a state_changed exists AFTER the
            // equipment.disconnected marker.
            var aGone = System.Array.IndexOf(tail, ("equipment.disconnected", "uid-A"));
            var reconnecting = System.Array.FindIndex(tail, aGone + 1,
                e => e is ("equipment.state_changed", "uid-A"));
            Assert.That(reconnecting, Is.GreaterThan(aGone), "teardown precedes the fresh Connecting");
        }

        [Test]
        public async Task DisconnectAsync_after_a_failed_connect_returns_to_Disconnected() {
            using var svc = new SwitchService();
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid", 0)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid");

            await svc.DisconnectAsync("uid", null, CancellationToken.None);
            var dto = await svc.GetAsync("uid", CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public void SetValueAsync_for_an_unknown_device_number_throws_InvalidOperation() {
            using var svc = new SwitchService();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("no-such-switch", new SwitchValueRequestDto(0, 1.0), CancellationToken.None));
        }

        [Test]
        public void SetValueAsync_with_out_of_range_PortId_throws_ArgumentOutOfRange() {
            using var svc = new SwitchService();
            // PortId > short.MaxValue would silently wrap on the (short) cast — must throw instead, and
            // before the connection lookup so the range contract holds regardless of state.
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.SetValueAsync("uid", new SwitchValueRequestDto(40000, 1.0), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.SetValueAsync("uid", new SwitchValueRequestDto(-1, 1.0), CancellationToken.None));
        }

        [Test]
        public void ConnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.ConnectAsync(new ConnectRequestDto(Dead("uid", 0)), null, CancellationToken.None); });
        }

        [Test]
        public void DisconnectAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.DisconnectAsync("uid", null, CancellationToken.None); });
        }

        [Test]
        public void GetAllAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAllAsync(CancellationToken.None));
        }

        [Test]
        public void SetValueAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(
                () => svc.SetValueAsync("uid", new SwitchValueRequestDto(0, 1.0), CancellationToken.None));
        }

        private static async Task<SwitchDto?> PollUntilNotConnectingAsync(SwitchService svc, string deviceId) {
            for (var i = 0; i < 150; i++) {
                var dto = await svc.GetAsync(deviceId, CancellationToken.None);
                if (dto is not null && dto.State != EquipmentConnectionState.Connecting) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            return await svc.GetAsync(deviceId, CancellationToken.None);
        }
    }
}
