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
    /// §14e — live integration test for <see cref="TelescopeService"/>. Discovers a Telescope from a
    /// running ASCOM OmniSim, connects, unparks + enables tracking, slews to a coordinate, verifies
    /// the cached runtime reflects the slew, then aborts/disconnects. Runs in the
    /// <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class TelescopeConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live Telescope test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_unparks_tracks_and_slews_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Telescope discovered from the running OmniSim");

            using var svc = new TelescopeService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(connected.Capabilities, Is.Not.Null, "capabilities should be populated once connected");

            // Drive the mount inside a try/finally so the simulator is always disconnected even if an
            // assertion fires mid-test: DisconnectAsync runs the full SafeDisconnectDispose teardown
            // (AbortSlew + Connected=false), unlike `using`/Dispose which takes the fast non-blocking
            // path (DisposeQuietly only). That leaves the sim mount halted + disconnected for retries.
            try {
                // Make sure the mount can move: unpark (idempotent if already unparked) and track.
                await svc.UnparkAsync(null, CancellationToken.None).ConfigureAwait(false);
                await PollUntilAsync(svc, d => !d.Runtime.Parked).ConfigureAwait(false);
                await svc.SetTrackingAsync(true, CancellationToken.None).ConfigureAwait(false);
                var tracking = await PollUntilAsync(svc, d => d.Runtime.Tracking).ConfigureAwait(false);
                Assert.That(tracking!.Runtime.Tracking, Is.True, "tracking should report on after SetTracking(true)");

                // Slew to a coordinate near the current pointing so the OmniSim settles quickly, then
                // confirm the read-back lands near the target.
                var startRa = tracking.Runtime.RightAscensionHours ?? 6.0;
                var targetRa = NormalizeRaHours(startRa + 0.2);
                const double targetDec = 45.0;
                await svc.SlewAsync(new SlewRequestDto(targetRa, targetDec), null, CancellationToken.None).ConfigureAwait(false);
                var slewed = await PollUntilAsync(svc,
                    d => d.Runtime.DeclinationDegrees is double dec && Math.Abs(dec - targetDec) < 1.0
                      && d.Runtime.State != "slewing").ConfigureAwait(false);
                Assert.That(slewed!.Runtime.DeclinationDegrees, Is.Not.Null);
                Assert.That(slewed.Runtime.DeclinationDegrees!.Value, Is.EqualTo(targetDec).Within(1.0),
                    "the mount should report ~45° declination after the slew");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static double NormalizeRaHours(double ra) {
            ra %= 24.0;
            return ra < 0 ? ra + 24.0 : ra;
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.Telescope, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        // Polls up to ~24s (slew + the 2s cache) until the predicate holds; null on timeout.
        private static async Task<TelescopeDto?> PollUntilAsync(TelescopeService svc, Func<TelescopeDto, bool> predicate) {
            for (var i = 0; i < 120; i++) {
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
