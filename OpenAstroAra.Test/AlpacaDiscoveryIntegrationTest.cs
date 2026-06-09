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
    /// §14e (PR2) — live integration test for <see cref="AlpacaEquipmentDiscoveryService"/>.
    /// Exercises the real UDP-broadcast discovery path (port 32227) against a running
    /// ASCOM OmniSim listening on the Alpaca HTTP port 32323, asserting the daemon's
    /// discovery service finds the simulated devices end-to-end. This is the piece the
    /// <c>alpaca-sim-smoke</c> job does NOT cover — that job only proves the HTTP
    /// management API answers; discovery is a separate UDP mechanism.
    ///
    /// Self-gating: the fixture probes <c>:32323</c> in <see cref="OneTimeSetUp"/> and
    /// <see cref="Assert.Ignore(string)"/>s if no simulator answers, so it skips cleanly
    /// on a dev box or in the unit-test job. The <c>Integration</c> category lets the
    /// solution-wide test run exclude it (<c>--filter TestCategory!=Integration</c>); the
    /// dedicated <c>alpaca-sim-integration</c> CI job starts the simulator and runs it
    /// with <c>--filter TestCategory=Integration</c>.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class AlpacaDiscoveryIntegrationTest {

        // Where the OmniSim is expected to answer. The CI job (and a local dev) starts
        // it on the Alpaca default HTTP port; discovery itself broadcasts on UDP 32227.
        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");

        // Discovery can miss on a single 2-second poll (UDP is lossy and the responder
        // may not have answered within the window), so we retry the whole discovery a
        // few times before failing. Each DiscoverAsync call re-broadcasts (no caching).
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live discovery test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task DiscoverAsync_finds_the_OmniSim_camera_over_udp() {
            var camera = await DiscoverFirstAsync(DeviceType.Camera).ConfigureAwait(false);

            Assert.That(camera, Is.Not.Null, "discovery returned no Camera device from the running OmniSim");
            Assert.That(camera!.Type, Is.EqualTo(DeviceType.Camera));
            Assert.That(camera.IpPort, Is.EqualTo(32323), "the simulated camera should advertise the Alpaca HTTP port");
            Assert.That(camera.UniqueId, Is.Not.Empty, "a discovered device must carry a UniqueId");
            Assert.That(camera.Name, Is.Not.Empty, "a discovered device must carry a name");
        }

        [Test]
        public async Task DiscoverAsync_finds_the_OmniSim_telescope_over_udp() {
            // A second device type proves the DeviceType -> ASCOM mapping, not just the
            // transport. OmniSim exposes a telescope simulator alongside the camera.
            var scope = await DiscoverFirstAsync(DeviceType.Telescope).ConfigureAwait(false);

            Assert.That(scope, Is.Not.Null, "discovery returned no Telescope device from the running OmniSim");
            Assert.That(scope!.Type, Is.EqualTo(DeviceType.Telescope));
            Assert.That(scope.IpPort, Is.EqualTo(32323));
            Assert.That(scope.UniqueId, Is.Not.Empty);
        }

        // Runs discovery for a type, retrying to absorb UDP timing flakiness, and returns
        // the first matching device (or null if none appeared across all attempts).
        private static async Task<DiscoveredDeviceDto?> DiscoverFirstAsync(DeviceType type) {
            var service = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var devices = await service.DiscoverAsync(type, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (devices.Count > 0) {
                    return devices[0];
                }
                await TestContext.Progress.WriteLineAsync(
                    $"discovery attempt {attempt}/{MaxDiscoveryAttempts} for {type} found nothing; retrying").ConfigureAwait(false);
            }
            return null;
        }
    }
}
