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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live integration test for <see cref="SwitchService"/>, the first device with a
    /// control action. Discovers a Switch from a running ASCOM OmniSim, connects, reads its ports,
    /// writes a writable port via <see cref="SwitchService.SetValueAsync"/>, and verifies the
    /// cached value reflects the write. Runs in the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class SwitchConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live Switch test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_reads_ports_writes_one_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Switch device discovered from the running OmniSim");

            using var svc = new SwitchService();

            var n = device!.UniqueId;
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, n, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(connected.Ports, Is.Not.Empty, "the simulated Switch should expose ports");

            // Idempotency (§60.5): a repeat connect to the same connected device is a no-op accept.
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var stillConnected = await svc.GetAsync(n, CancellationToken.None).ConfigureAwait(false);
            Assert.That(stillConnected!.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "a repeat connect to the same connected device must stay Connected");

            // Find a writable port and flip it between its min and max, then confirm the read-back.
            var writable = connected.Ports.FirstOrDefault(p => p.CanWrite && p.Max > p.Min);
            if (writable is not null) {
                var target = Math.Abs(writable.Value - writable.Max) < double.Epsilon ? writable.Min : writable.Max;
                await svc.SetValueAsync(n, new SwitchValueRequestDto(writable.Id, target), CancellationToken.None).ConfigureAwait(false);

                var updated = await PollUntilPortAsync(svc, n, writable.Id, target).ConfigureAwait(false);
                Assert.That(updated, Is.Not.Null, "port never reflected the written value");
                Assert.That(updated!.Value, Is.EqualTo(target).Within(1e-6),
                    "the cached port value should reflect the SetValue write");
            } else {
                Assert.Warn("no writable port on the simulated Switch — write path not exercised");
            }

            await svc.DisconnectAsync(n, null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, n, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        /// <summary>
        /// §14e — the same <see cref="SwitchService"/> also serves <see cref="ISwitchMediator"/>: the
        /// <c>SetSwitchValue</c> instruction reads <c>GetInfo()</c> (Validate → writable switches with
        /// real min/max/step) and calls <c>SetSwitchValue(collectionIndex, value)</c> (Execute). This
        /// exercises the live mediator surface end-to-end, including the collection-index→port-id
        /// mapping.
        /// </summary>
        [Test]
        public async Task Mediator_GetInfo_and_SetSwitchValue_drive_the_live_device() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Switch device discovered from the running OmniSim");

            using var svc = new SwitchService();
            var n = device!.UniqueId;
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, n, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(connected.Ports, Is.Not.Empty);

            try {
                var info = ((ISwitchMediator)svc).GetInfo();
                Assert.That(info.Connected, Is.True);
                Assert.That(info.WritableSwitches, Is.Not.Null);
                Assert.That(info.WritableSwitches, Is.Not.Empty,
                    "the simulated Switch should expose writable ports for SetSwitchValue.Validate");

                // Pick a writable switch with a real range, like the instruction's Validate would.
                var index = -1;
                for (var i = 0; i < info.WritableSwitches!.Count; i++) {
                    if (info.WritableSwitches[i].Maximum > info.WritableSwitches[i].Minimum) {
                        index = i;
                        break;
                    }
                }
                if (index < 0) {
                    Assert.Warn("no writable port with a usable range on the simulated Switch — mediator write not exercised");
                    return;
                }

                var target = info.WritableSwitches[index];
                var newValue = Math.Abs(target.Value - target.Maximum) < double.Epsilon ? target.Minimum : target.Maximum;
                await ((ISwitchMediator)svc).SetSwitchValue((short)index, newValue, progress: null!, CancellationToken.None).ConfigureAwait(false);

                // The wrapper's Value reads the live §32.4 cache, so the same stored reference must
                // converge on the written value as the refresh ticks.
                var reflected = false;
                for (var i = 0; i < 40 && !reflected; i++) {
                    reflected = Math.Abs(target.Value - newValue) < 1e-6;
                    if (!reflected) {
                        await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                    }
                }
                Assert.That(reflected, Is.True, "the mediator write should be reflected by the live wrapper Value");
            } finally {
                await svc.DisconnectAsync(n, null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, n, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.Switch, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<SwitchDto?> PollUntilAsync(SwitchService svc, string deviceId, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<SwitchPortDto?> PollUntilPortAsync(SwitchService svc, string deviceId, int portId, double target) {
            for (var i = 0; i < 40; i++) {
                var dto = await svc.GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                var port = dto?.Ports.FirstOrDefault(p => p.Id == portId);
                if (port is not null && Math.Abs(port.Value - target) < 1e-6) {
                    return port;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            var final = await svc.GetAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            return final?.Ports.FirstOrDefault(p => p.Id == portId);
        }
    }
}
