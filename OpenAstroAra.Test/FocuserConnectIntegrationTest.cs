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
    /// §14e — live integration test for <see cref="FocuserService"/>. Discovers a Focuser from a
    /// running ASCOM OmniSim, connects, reads its capabilities, moves it to a target position via
    /// <see cref="FocuserService.MoveAsync"/>, and verifies the cached runtime reflects the new
    /// position, then disconnects. Runs in the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class FocuserConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live Focuser test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_reads_caps_moves_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Focuser device discovered from the running OmniSim");

            using var svc = new FocuserService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(connected.Capabilities, Is.Not.Null, "capabilities should be seeded on connect");
            Assert.That(connected.Capabilities!.MaxPosition, Is.GreaterThan(0), "an absolute focuser exposes a max position");

            // Move to a target away from the current position and confirm the read-back.
            var max = connected.Capabilities.MaxPosition;
            var current = connected.Runtime.Position ?? 0;
            var target = current < max / 2 ? Math.Min(max, max / 2 + 100) : Math.Max(0, max / 2 - 100);

            await svc.MoveAsync(new FocuserMoveRequestDto(target), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var moved = await PollUntilPositionAsync(svc, target).ConfigureAwait(false);
            Assert.That(moved, Is.Not.Null, "focuser never reported the target position");
            Assert.That(moved!.Runtime.Position, Is.EqualTo(target), "the cached position should reflect the move");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.Focuser, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<FocuserDto?> PollUntilAsync(FocuserService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Up to ~16s for the move to complete + the 2s cache to reflect it. Returns null on
        // timeout so the caller's Is.Not.Null assertion fails fast with the right message rather
        // than a confusing position-equality mismatch.
        private static async Task<FocuserDto?> PollUntilPositionAsync(FocuserService svc, int target) {
            for (var i = 0; i < 80; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto?.Runtime.Position == target) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }
    }
}
