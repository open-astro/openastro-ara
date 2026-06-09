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
    /// §14e — live integration test for <see cref="ObservingConditionsService"/>. Discovers an
    /// ObservingConditions device from a running ASCOM OmniSim, connects, and asserts the §32.4
    /// cache serves live sensor readings (the OmniSim reports temperature), then disconnects.
    /// Self-gating + category match the other integration tests; runs in the
    /// <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class ObservingConditionsConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live ObservingConditions test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_reads_sensors_then_disconnect_returns_to_disconnected() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no ObservingConditions device discovered from the running OmniSim");

            using var svc = new ObservingConditionsService();

            Assert.That(await svc.GetAsync(CancellationToken.None).ConfigureAwait(false), Is.Null,
                "GetAsync should be null before a device is selected");

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "the simulated ObservingConditions device should reach Connected");
            Assert.That(connected.DeviceId, Is.Not.Empty);
            // The OmniSim ObservingConditions simulator implements the temperature sensor, so the
            // §32.4 cache (seeded on connect) should carry a value — proving the read path works.
            Assert.That(connected.TemperatureC, Is.Not.Null, "the cache should carry the sim's temperature reading");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected, Is.Not.Null);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.ObservingConditions, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<ObservingConditionsDto?> PollUntilAsync(
                ObservingConditionsService svc, Func<EquipmentConnectionState, bool> predicate) {
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
