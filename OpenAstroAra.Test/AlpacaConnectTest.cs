#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>AB #1452 — AlpacaBridge can search for its hardware inside <c>Connected = true</c>.
    /// The connect gets <see cref="AlpacaConnect.Timeout"/> and one PUT, and the client's own
    /// settings come back afterwards so the §42.3 liveness probe stays short.</summary>
    [TestFixture]
    public class AlpacaConnectTest {

        [Test]
        [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
        public async Task A_connect_outlasts_the_client_default_with_a_single_put() {
            // 6 s: past the client's 5 s default, which timed out, retried once and sent a second PUT.
            await using var bridge = ScriptedAlpacaDevice.Start(putResponder: path => {
                if (path.EndsWith("/connected", StringComparison.Ordinal)) {
                    Thread.Sleep(TimeSpan.FromSeconds(6));
                }
                return null;
            });
            using var client = new AlpacaFocuser(ServiceType.Http, bridge.BaseUri.Host, bridge.BaseUri.Port, 0,
                strictCasing: false, logger: null);
            var probeTimeout = client.ClientConfiguration.EstablishConnectionTimeout;
            var retries = client.ClientConfiguration.NumberOfRetries;

            await Task.Run(() => AlpacaConnect.Connect(client));

            Assert.That(bridge.Puts.Count(p => p.Path.EndsWith("/connected", StringComparison.Ordinal)), Is.EqualTo(1),
                "a retried PUT restarts the bridge's search");
            Assert.That(client.ClientConfiguration.EstablishConnectionTimeout, Is.EqualTo(probeTimeout),
                "the Connected GET probe keeps its short timeout");
            Assert.That(client.ClientConfiguration.NumberOfRetries, Is.EqualTo(retries));
        }

        [Test]
        public void The_client_settings_come_back_when_the_connect_throws() {
            using var client = new AlpacaFocuser(ServiceType.Http, "127.0.0.1", 1, 0, strictCasing: false, logger: null);
            var config = client.ClientConfiguration;
            var probeTimeout = config.EstablishConnectionTimeout;
            var retries = config.NumberOfRetries;
            (int Timeout, int Retries) during = default;

            Assert.Throws<InvalidOperationException>(() => AlpacaConnect.WithConnectBudget(config, () => {
                during = (config.EstablishConnectionTimeout, config.NumberOfRetries);
                throw new InvalidOperationException("no device");
            }));

            Assert.That(during, Is.EqualTo(((int) AlpacaConnect.Timeout.TotalSeconds, 0)));
            Assert.That(config.EstablishConnectionTimeout, Is.EqualTo(probeTimeout));
            Assert.That(config.NumberOfRetries, Is.EqualTo(retries));
        }
    }
}
