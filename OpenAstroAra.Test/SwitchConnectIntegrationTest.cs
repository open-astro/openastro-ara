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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(connected.Ports, Is.Not.Empty, "the simulated Switch should expose ports");

            // Find a writable port and flip it between its min and max, then confirm the read-back.
            var writable = connected.Ports.FirstOrDefault(p => p.CanWrite && p.Max > p.Min);
            if (writable is not null) {
                var target = Math.Abs(writable.Value - writable.Max) < double.Epsilon ? writable.Min : writable.Max;
                await svc.SetValueAsync(new SwitchValueRequestDto(writable.Id, target), CancellationToken.None).ConfigureAwait(false);

                var updated = await PollUntilPortAsync(svc, writable.Id, target).ConfigureAwait(false);
                Assert.That(updated, Is.Not.Null, "port never reflected the written value");
                Assert.That(updated!.Value, Is.EqualTo(target).Within(1e-6),
                    "the cached port value should reflect the SetValue write");
            } else {
                Assert.Warn("no writable port on the simulated Switch — write path not exercised");
            }

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
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

        private static async Task<SwitchDto?> PollUntilAsync(SwitchService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<SwitchPortDto?> PollUntilPortAsync(SwitchService svc, int portId, double target) {
            for (var i = 0; i < 40; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                var port = dto?.Ports.FirstOrDefault(p => p.Id == portId);
                if (port is not null && Math.Abs(port.Value - target) < 1e-6) {
                    return port;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            var final = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            return final?.Ports.FirstOrDefault(p => p.Id == portId);
        }
    }
}
