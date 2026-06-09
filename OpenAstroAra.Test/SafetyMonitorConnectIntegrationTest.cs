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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live integration test for the first real Alpaca-backed equipment service,
    /// <see cref="SafetyMonitorService"/>. Discovers a SafetyMonitor from a running ASCOM
    /// OmniSim, connects to it, observes the connection reach <c>Connected</c> with a live
    /// <c>IsSafe</c> read, then disconnects — exercising the §60.5 202-Accepted lifecycle
    /// end-to-end against real Alpaca HTTP (not just discovery).
    ///
    /// Self-gating + category match the discovery integration test: probes <c>:32323</c> in
    /// setup and <see cref="Assert.Ignore(string)"/>s when no simulator answers; the
    /// <c>alpaca-sim-integration</c> CI job runs it with <c>--filter TestCategory=Integration</c>.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class SafetyMonitorConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live SafetyMonitor test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_reaches_connected_then_disconnect_returns_to_disconnected() {
            var device = await DiscoverSafetyMonitorAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no SafetyMonitor was discovered from the running OmniSim");

            using var svc = new SafetyMonitorService();

            // Before any connect, no device is selected -> GET maps to 404 (null DTO).
            Assert.That(await svc.GetAsync(CancellationToken.None).ConfigureAwait(false), Is.Null,
                "GetAsync should be null before a device is selected");

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "the simulated SafetyMonitor should reach Connected");
            Assert.That(connected.DeviceId, Is.Not.Empty, "a connected device must carry its UniqueId");
            // Safe is whatever the sim is configured to report; we only assert the live read
            // produced a DTO without faulting (it did, since State is Connected not Error).

            // §32.4 — the same singleton serves the Sequencer's mediator GetInfo() from the cache
            // the background loop maintains. While Connected it must report Connected with the
            // device identity (IsSafe is whatever the sim reports).
            var info = ((OpenAstroAra.Equipment.Interfaces.Mediator.ISafetyMonitorMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.True, "mediator GetInfo() should report Connected while connected");
            Assert.That(info.DeviceId, Is.EqualTo(connected.DeviceId),
                "mediator GetInfo() and REST GetAsync() must agree on the device identity");

            // Idempotency (§60.5): connecting again to the same device while Connected is a no-op
            // accept — it must not tear the connection down or bounce it through Connecting.
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var stillConnected = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(stillConnected, Is.Not.Null);
            Assert.That(stillConnected!.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "a repeat connect to the same connected device must stay Connected (idempotent no-op)");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected, Is.Not.Null);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected),
                "the device should return to Disconnected after DisconnectAsync");
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverSafetyMonitorAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.SafetyMonitor, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        // Polls GetAsync (~10s budget) until the connection state satisfies the predicate.
        private static async Task<SafetyMonitorDto?> PollUntilAsync(
                SafetyMonitorService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
