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
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Linq;
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

        private static DiscoveredDeviceDto Dead(string uid, int deviceNumber, string host = "127.0.0.1") =>
            new(uid, $"Unreachable {deviceNumber}", DeviceType.Switch,
                host, "127.0.0.1", 1, deviceNumber, false);

        // §42.3 — the ports-unreadable rule the refresh tick feeds into the weak-signal
        // streak. Pure, so it is testable without an Alpaca device (the tick around it
        // needs a live OmniSim; the companion Integration test covers that path).

        [Test]
        public void PortsUnreadable_when_the_device_will_not_report_a_count() {
            // null must NOT read as 0: a MaxSwitch that throws is "no answer", and
            // treating it as "this device has no ports" would let a dead device fall
            // through to a SUCCESSFUL probe that resets the streak on every tick.
            Assert.That(SwitchService.PortsUnreadable(advertised: null, portsRead: 0), Is.True);
        }

        [Test]
        public void PortsUnreadable_when_ports_are_advertised_but_none_read() {
            Assert.That(SwitchService.PortsUnreadable(advertised: 24, portsRead: 0), Is.True);
        }

        [Test]
        public void PortsUnreadable_is_false_for_a_device_that_genuinely_has_no_ports() {
            Assert.That(SwitchService.PortsUnreadable(advertised: 0, portsRead: 0), Is.False);
        }

        [Test]
        public async Task PortsUnreadable_is_false_when_any_port_was_read() {
            // A partially-readable device is degraded, not lost — the per-port skips
            // already keep the readable ones.
            Assert.That(SwitchService.PortsUnreadable(advertised: 24, portsRead: 1), Is.False);
            Assert.That(SwitchService.PortsUnreadable(advertised: 24, portsRead: 24), Is.False);
        }

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
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-A", 0, host: "host-a")), null, CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-B", 0, host: "host-b")), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid-A");
            await PollUntilNotConnectingAsync(svc, "uid-B");

            var all = await svc.GetAllAsync(CancellationToken.None);
            Assert.That(all, Has.Count.EqualTo(2), "same-numbered switches on different hosts coexist");
            Assert.That(all[0].DeviceId, Is.EqualTo("uid-A"), "stable order: number, then id");
            Assert.That(all[1].DeviceId, Is.EqualTo("uid-B"));
        }

        [Test]
        public async Task ConnectAsync_same_endpoint_with_a_renamed_unique_id_replaces_not_duplicates() {
            using var svc = new SwitchService();
            // A bridge that renamed the device's UniqueId across versions (ZWO_DEW_1 →
            // ZWO_DEW_SN_...): the endpoint (host:port + number) is the same physical device, so
            // the fresh connect must supersede the stale entry, not open a second connection.
            await svc.ConnectAsync(new ConnectRequestDto(Dead("ZWO_DEW_1", 1)), null, CancellationToken.None);
            await svc.ConnectAsync(new ConnectRequestDto(Dead("ZWO_DEW_SN_abc", 1)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "ZWO_DEW_SN_abc");

            var all = await svc.GetAllAsync(CancellationToken.None);
            Assert.That(all, Has.Count.EqualTo(1), "one physical device = one connection entry");
            Assert.That(all[0].DeviceId, Is.EqualTo("ZWO_DEW_SN_abc"), "the latest id wins");
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
        public async Task ReconnectAsync_for_an_unknown_id_returns_null() {
            using var svc = new SwitchService();
            Assert.That(await svc.ReconnectAsync("never-seen", null, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ReconnectAsync_after_a_disconnect_redispatches_the_known_device() {
            using var svc = new SwitchService();
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid", 0)), null, CancellationToken.None);
            await PollUntilNotConnectingAsync(svc, "uid");
            await svc.DisconnectAsync("uid", null, CancellationToken.None);

            var accepted = await svc.ReconnectAsync("uid", null, CancellationToken.None);

            // The card's Connect re-dispatches the registry's own discovery record (no rediscovery,
            // no host/port from the client): the entry leaves Disconnected — Connecting, then Error
            // for this unreachable device — and stays a single known switch.
            Assert.That(accepted, Is.Not.Null);
            var dto = await PollUntilNotConnectingAsync(svc, "uid");
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Error));
            Assert.That(await svc.GetAllAsync(CancellationToken.None), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task SetValueAsync_for_an_unknown_device_number_throws_InvalidOperation() {
            using var svc = new SwitchService();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetValueAsync("no-such-switch", new SwitchValueRequestDto(0, 1.0), CancellationToken.None));
        }

        [Test]
        public async Task SetValueAsync_with_out_of_range_PortId_throws_ArgumentOutOfRange() {
            using var svc = new SwitchService();
            // PortId > short.MaxValue would silently wrap on the (short) cast — must throw instead, and
            // before the connection lookup so the range contract holds regardless of state.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.SetValueAsync("uid", new SwitchValueRequestDto(40000, 1.0), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
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
        public async Task GetAllAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetAllAsync(CancellationToken.None));
        }

        [Test]
        public async Task SetValueAsync_after_Dispose_throws_ObjectDisposedException() {
            var svc = new SwitchService();
            svc.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => svc.SetValueAsync("uid", new SwitchValueRequestDto(0, 1.0), CancellationToken.None));
        }

        // ─── #1127 — RemoveAsync: live gate + Error-client release ───────────────────────────

        [Test]
        public async Task RemoveAsync_refuses_a_connecting_switch_and_an_unknown_id_is_false() {
            using var svc = new SwitchService();
            Assert.That(await svc.RemoveAsync("never-seen", CancellationToken.None), Is.False);
            // A black-hole address keeps the connect in flight long enough to observe Connecting.
            await svc.ConnectAsync(new ConnectRequestDto(Dead("uid-slow", 0, host: "10.255.255.1")), null, CancellationToken.None);
            var dto = await svc.GetAsync("uid-slow", CancellationToken.None);
            Assume.That(dto?.State, Is.EqualTo(EquipmentConnectionState.Connecting), "the connect must still be in flight for this probe");
            await Assert.ThatAsync(() => svc.RemoveAsync("uid-slow", CancellationToken.None), Throws.TypeOf<InvalidOperationException>(),
                "a mid-connect removal would let the adopt land in an untracked entry");
            Assert.That(await svc.GetAsync("uid-slow", CancellationToken.None), Is.Not.Null, "refused means kept");
        }

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task RemoveAsync_releases_the_client_of_a_switch_that_tripped_to_Error() {
            // Connect to a scripted switch, then have it answer Connected=false until the §42.3
            // streak trips the entry to Error — the entry still holds the adopted client. Remove
            // must release it: the teardown's Connected=false PUT reaching the device is the proof
            // (before #1127 the entry was dropped and the client leaked, no PUT ever arrived).
            var connected = "true";
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? Volatile.Read(ref connected)
                : path.EndsWith("/maxswitch", StringComparison.Ordinal) ? "0"
                : null);
            using var svc = new SwitchService();
            var device = new DiscoveredDeviceDto(
                UniqueId: "switch-under-test", Name: "Bench Power Box", Type: DeviceType.Switch,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            await WaitForStateAsync(svc, "switch-under-test", EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15));

            Volatile.Write(ref connected, "false");
            await WaitForStateAsync(svc, "switch-under-test", EquipmentConnectionState.Error, TimeSpan.FromSeconds(30));
            // The connect itself PUT Connected=True; the release is the LATER Connected=False.
            static bool IsDisconnectPut((string Path, string Body) p) =>
                p.Path.EndsWith("/connected", StringComparison.Ordinal) && p.Body.Contains("False", StringComparison.OrdinalIgnoreCase);
            Assume.That(box.Puts.Any(IsDisconnectPut), Is.False, "nothing has released the client yet");

            Assert.That(await svc.RemoveAsync("switch-under-test", CancellationToken.None), Is.True);
            Assert.That(await svc.GetAsync("switch-under-test", CancellationToken.None), Is.Null, "gone from the known list");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !box.Puts.Any(IsDisconnectPut)) {
                await Task.Delay(100);
            }
            Assert.That(box.Puts.Any(IsDisconnectPut), Is.True, "the Error entry's client was never released (no Connected=False reached the device)");
        }

        private static async Task WaitForStateAsync(SwitchService svc, string deviceId, EquipmentConnectionState state, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if ((await svc.GetAsync(deviceId, CancellationToken.None))?.State == state) {
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail($"{deviceId} never reached {state}");
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
