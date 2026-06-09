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
    /// §14e — live integration test for <see cref="FlatDeviceService"/>. Discovers a CoverCalibrator
    /// from a running ASCOM OmniSim, connects, opens the cover and turns the calibrator light on via
    /// <see cref="FlatDeviceService.ApplyFlatPanelAsync"/>, verifying the cached runtime reflects
    /// each, then disconnects. Runs in the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class FlatDeviceConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live FlatDevice test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_opens_cover_and_lights_on_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no FlatDevice (CoverCalibrator) discovered from the running OmniSim");

            using var svc = new FlatDeviceService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            // Open the cover and confirm the read-back.
            await svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(OpenCover: true), null, CancellationToken.None).ConfigureAwait(false);
            var opened = await PollUntilAsync(svc, d => d.Runtime.CoverOpen).ConfigureAwait(false);
            Assert.That(opened!.Runtime.CoverOpen, Is.True, "the cover should report open after OpenCover");

            // Turn the calibrator light on (full) and confirm the read-back.
            await svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(LightOn: true), null, CancellationToken.None).ConfigureAwait(false);
            var lit = await PollUntilAsync(svc, d => d.Runtime.LightOn).ConfigureAwait(false);
            Assert.That(lit!.Runtime.LightOn, Is.True, "the calibrator light should report on after LightOn");

            // Turn the light off again (leave the device tidy) — best-effort, not asserted hard.
            await svc.ApplyFlatPanelAsync(new FlatPanelRequestDto(LightOn: false), null, CancellationToken.None).ConfigureAwait(false);

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.FlatDevice, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        // Polls up to ~16s (cover/light moves + the 2s cache) until the predicate holds; null on timeout.
        private static async Task<FlatDeviceDto?> PollUntilAsync(FlatDeviceService svc, Func<FlatDeviceDto, bool> predicate) {
            for (var i = 0; i < 80; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
