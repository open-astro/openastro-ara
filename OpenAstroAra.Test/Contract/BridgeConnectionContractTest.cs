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
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.Contract {

    /// <summary>#1548 — Ara against a real AlpacaBridge, asserted only through Ara's own HTTP API. The bridge is
    /// killed (its sockets drop) and started again; Ara must show the loss and then the recovery on
    /// <c>GET /api/v1/equipment/alpacabridge/catalog</c>, the route the setup wizard reads the bridge's drivers
    /// from: a bridge that does not answer is an empty <c>device_types</c> list (the wizard then offers no Add,
    /// design/PORT_PLAYBOOK.md §68.2).
    /// Skipped unless <see cref="BridgeProcess.BinVar"/> and <see cref="BridgeProcess.PortVar"/> name a bridge, so
    /// a local or <c>analyzer-gate</c> run stays green without one.</summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable] // BuildApp sets process-wide state (profile dir, Serilog, Notifier)
    public class BridgeConnectionContractTest {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

        private BridgeProcess? bridge;
        private AraHost? ara;
        private HttpClient http = null!;

        [OneTimeSetUp]
        public async Task StartAraAndBridge() {
            bridge = BridgeProcess.FromEnvironment();
            if (bridge is null) {
                Assert.Ignore($"no AlpacaBridge configured ({BridgeProcess.BinVar}, {BridgeProcess.PortVar}); the CI contract job sets them");
            }
            await bridge.StartAsync();
            ara = new AraHost();
            http = ara.CreateClient();
        }

        [OneTimeTearDown]
        public void StopAraAndBridge() {
            // Each on its own: a failing Ara stop must not leave the bridge running.
            try {
                http?.Dispose();
                ara?.Dispose();
            } finally {
                bridge?.Dispose();
            }
        }

        // The driver-type tokens Ara reports for the bridge; empty when the bridge does not answer.
        private async Task<int> DriverTypesAsync() {
            var url = $"/api/v1/equipment/alpacabridge/catalog?host={BridgeProcess.Host}&port={bridge!.Port.ToString(CultureInfo.InvariantCulture)}";
            using var response = await http.GetAsync(new Uri(url, UriKind.Relative));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Ara answers the wizard even when the bridge does not");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("device_types").GetArrayLength();
        }

        private async Task<int> UntilAsync(Func<int, bool> reached, string what) {
            var deadline = DateTime.UtcNow + Settle;
            while (true) {
                var n = await DriverTypesAsync();
                if (reached(n) || DateTime.UtcNow >= deadline) {
                    Assert.That(reached(n), Is.True, $"{what} within {Settle.TotalSeconds:0} s (last count {n})");
                    return n;
                }
                await Task.Delay(500);
            }
        }

        [Test]
        public async Task Ara_reports_a_killed_bridge_as_lost_and_a_restarted_one_as_back() {
            var before = await UntilAsync(n => n > 0, "Ara lists the running bridge's driver types");

            await bridge!.KillAsync();
            await UntilAsync(n => n == 0, "Ara reports the killed bridge as having no drivers");

            await bridge.StartAsync();
            var after = await UntilAsync(n => n > 0, "Ara lists the driver types again once the bridge is back");
            Assert.That(after, Is.EqualTo(before), "the restarted bridge offers the same driver types");
        }
    }
}