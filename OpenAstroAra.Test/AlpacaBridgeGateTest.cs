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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §68.1 AlpacaBridge version-gate: the pure classifier table + the <c>/version</c> probe against
    /// a stubbed bridge (no real HTTP, no hardware).
    /// </summary>
    [TestFixture]
    public class AlpacaBridgeGateTest {

        // ----- §68.1 classifier table -----

        [TestCase("1.5.0")]
        [TestCase("1.5.1")]
        [TestCase("1.6.2")]
        [TestCase("2.0.0")]
        [TestCase("10.0.0")]
        [TestCase("1.5")]        // normalized to 1.5.0 — equals the recommended floor.
        [TestCase("v1.5.0")]     // leading v tolerated.
        public void Classify_accepts_recommended_and_newer(string version) {
            Assert.That(AlpacaBridgeGate.Classify(version), Is.EqualTo(AlpacaBridgeStatus.Ok));
        }

        [TestCase("1.2.0")]      // exactly the minimum.
        [TestCase("1.2.5")]
        [TestCase("1.3.0")]
        [TestCase("1.4.9")]
        [TestCase("1.2")]        // normalized to 1.2.0 — equals the minimum, not below it.
        [TestCase("1.4.9-beta")] // pre-release suffix dropped.
        [TestCase("1.3.0.7")]    // extra component ignored.
        public void Classify_warns_between_minimum_and_recommended(string version) {
            Assert.That(AlpacaBridgeGate.Classify(version), Is.EqualTo(AlpacaBridgeStatus.OutdatedWarn));
        }

        [TestCase("1.1.9")]
        [TestCase("1.1.0")]
        [TestCase("1.0.0")]
        [TestCase("0.9.9")]
        [TestCase("0.1")]
        public void Classify_blocks_below_minimum(string version) {
            Assert.That(AlpacaBridgeGate.Classify(version), Is.EqualTo(AlpacaBridgeStatus.OutdatedBlock));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("garbage")]
        [TestCase("v")]
        [TestCase("-1.2.3")]     // leading sign isn't a digit → no numeric core.
        public void Classify_treats_unparseable_as_missing(string? version) {
            Assert.That(AlpacaBridgeGate.Classify(version), Is.EqualTo(AlpacaBridgeStatus.Missing));
        }

        // ----- §68.1 /version probe -----

        [Test]
        public async Task ProbeAsync_classifies_a_healthy_bridge_and_hits_the_version_path() {
            using var handler = new StubHandler(_ => Json("{\"alpaca_bridge_version\":\"1.3.0\",\"alpaca_api_version\":\"1\"}"));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:11111/"), CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.OutdatedWarn));
                Assert.That(result.Version, Is.EqualTo("1.3.0"));
                Assert.That(handler.LastRequestUri?.AbsolutePath, Is.EqualTo("/version"),
                    "the probe must hit the AlpacaBridge-specific /version endpoint");
            });
        }

        [Test]
        public async Task ProbeAsync_forces_version_path_even_when_base_uri_has_a_path() {
            using var handler = new StubHandler(_ => Json("{\"alpaca_bridge_version\":\"1.6.0\"}"));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://host:11111/api/v1/camera/0"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.Ok));
            Assert.That(handler.LastRequestUri?.AbsolutePath, Is.EqualTo("/version"));
        }

        [TestCase("{\"alpaca_bridge_version\":\"1.1.0\"}", AlpacaBridgeStatus.OutdatedBlock)]
        [TestCase("{\"alpaca_bridge_version\":\"1.5.0\"}", AlpacaBridgeStatus.Ok)]
        public async Task ProbeAsync_maps_reported_version_through_the_gate(string body, AlpacaBridgeStatus expected) {
            using var handler = new StubHandler(_ => Json(body));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://h:1/"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(expected));
        }

        [TestCase("not json at all")]
        [TestCase("{\"some_other_field\":\"1.2.3\"}")] // missing alpaca_bridge_version
        [TestCase("{\"alpaca_bridge_version\":123}")]  // wrong type
        public async Task ProbeAsync_treats_unusable_body_as_missing(string body) {
            using var handler = new StubHandler(_ => Json(body));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://h:1/"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.Missing));
            Assert.That(result.Version, Is.Null);
        }

        [Test]
        public async Task ProbeAsync_treats_non_success_status_as_missing() {
            using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://h:1/"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.Missing));
        }

        [Test]
        public async Task ProbeAsync_treats_an_unreachable_bridge_as_missing() {
            using var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
            var probe = NewProbe(handler);

            var result = await probe.ProbeAsync(new Uri("http://h:1/"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(AlpacaBridgeStatus.Missing));
            Assert.That(result.Version, Is.Null);
        }

        [Test]
        public void ProbeAsync_propagates_a_caller_cancellation() {
            using var handler = new StubHandler(_ => Json("{\"alpaca_bridge_version\":\"1.5.0\"}"));
            var probe = NewProbe(handler);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(async () => await probe.ProbeAsync(new Uri("http://h:1/"), cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }

        private static AlpacaBridgeVersionProbe NewProbe(HttpMessageHandler handler) =>
            new(new StubHttpClientFactory(handler), NullLogger<AlpacaBridgeVersionProbe>.Instance);

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class StubHandler : HttpMessageHandler {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public Uri? LastRequestUri { get; private set; }

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
                cancellationToken.ThrowIfCancellationRequested();
                LastRequestUri = request.RequestUri;
                return Task.FromResult(_responder(request));
            }
        }

        // Hands out clients over the test's stub handler; disposeHandler:false so the probe's
        // per-call `using var client` doesn't dispose the shared handler the test still owns.
        private sealed class StubHttpClientFactory : IHttpClientFactory {
            private readonly HttpMessageHandler _handler;
            public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }
    }
}
