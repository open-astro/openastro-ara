#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §68.1 warn-band notifier: publishes <c>equipment.alpaca_bridge_outdated_warn</c> with the
    /// bridge version + the minimum/recommended thresholds, and is best-effort (a throwing broadcaster
    /// never escapes).
    /// </summary>
    [TestFixture]
    public class AlpacaBridgeGateNotifierTest {

        [Test]
        public async Task Publishes_the_warn_event_with_version_and_thresholds() {
            var ws = new CapturingBroadcaster();
            var notifier = new AlpacaBridgeGateNotifier(ws, NullLogger<AlpacaBridgeGateNotifier>.Instance);

            await notifier.NotifyOutdatedWarnAsync("1.3.0", CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(ws.LastEventType, Is.EqualTo("equipment.alpaca_bridge_outdated_warn"));
                Assert.That(ws.LastPayload.GetProperty("version").GetString(), Is.EqualTo("1.3.0"));
                Assert.That(ws.LastPayload.GetProperty("minimum").GetString(), Is.EqualTo("1.2.0"));
                Assert.That(ws.LastPayload.GetProperty("recommended").GetString(), Is.EqualTo("1.5.0"));
            });
        }

        [Test]
        public void A_throwing_broadcaster_is_swallowed_best_effort() {
            var notifier = new AlpacaBridgeGateNotifier(new ThrowingBroadcaster(), NullLogger<AlpacaBridgeGateNotifier>.Instance);

            Assert.That(async () => await notifier.NotifyOutdatedWarnAsync("1.3.0", CancellationToken.None),
                Throws.Nothing, "a failed WS publish must not escape — the warn banner is advisory");
        }

        private sealed class CapturingBroadcaster : IWsBroadcaster {
            public string? LastEventType { get; private set; }
            public JsonElement LastPayload { get; private set; }
            public long CurrentSequence => 0;

            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                LastEventType = eventType;
                LastPayload = payload.Clone();
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingBroadcaster : IWsBroadcaster {
            public long CurrentSequence => 0;

            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) =>
                throw new InvalidOperationException("broadcaster down");
        }
    }
}
