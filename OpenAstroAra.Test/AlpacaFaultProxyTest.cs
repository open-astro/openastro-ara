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
using OpenAstroAra.TestHarness.Alpaca;
using OpenAstroAra.TestHarness.Net;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Unit coverage for the virtual-observatory bench's <see cref="AlpacaFaultProxy"/>.
    /// Drives the proxy in front of a tiny in-process stub Alpaca device (no OmniSim,
    /// no network beyond loopback), so it runs in the default CI job and pins the
    /// fault-injection contract the §42.2 scenarios depend on.
    /// </summary>
    [TestFixture]
    public class AlpacaFaultProxyTest {

        private const string TelescopeConnected = "api/v1/telescope/0/connected";

        [Test]
        public async Task PassThrough_returns_the_upstream_response_body() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            using var client = new HttpClient();

            var body = await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);

            Assert.That(ValueOf(body), Is.EqualTo("false"));
            Assert.That(upstream.RequestCount, Is.EqualTo(1), "a pass-through request must reach the upstream device");
        }

        [Test]
        public async Task PassThrough_forwards_inbound_request_headers_to_the_upstream() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(proxy.BaseUri, TelescopeConnected));
            request.Headers.Add("X-Ara-Test", "abc123");

            using var resp = await client.SendAsync(request).ConfigureAwait(false);

            Assert.That(upstream.LastRequestHeaders, Is.Not.Null);
            Assert.That(upstream.LastRequestHeaders!["X-Ara-Test"], Is.EqualTo("abc123"),
                "a custom inbound header must reach the upstream device, not be dropped by the proxy");
        }

        [Test]
        public async Task AlpacaError_returns_a_nonzero_errornumber_and_skips_the_upstream() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                DeviceType = "telescope",
                Fault = AlpacaFault.AlpacaError(0x500, "mount refused the slew"),
            });
            using var client = new HttpClient();

            var body = await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);
            var node = JsonNode.Parse(body)!.AsObject();

            Assert.That((int)node["ErrorNumber"]!, Is.EqualTo(0x500));
            Assert.That((string?)node["ErrorMessage"], Is.EqualTo("mount refused the slew"));
            Assert.That(upstream.RequestCount, Is.Zero, "an injected Alpaca error must not touch the real device");
        }

        [Test]
        public async Task HttpStatus_returns_the_raw_status_code() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule { Fault = AlpacaFault.HttpStatus(503) });
            using var client = new HttpClient();

            using var resp = await client.GetAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);

            Assert.That((int)resp.StatusCode, Is.EqualTo(503));
        }

        [Test]
        public async Task Drop_aborts_the_connection() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule { Fault = AlpacaFault.Drop() });
            using var client = new HttpClient();

            // NUnit's ThrowsAsync runs the delegate synchronously and RETURNS the
            // exception (it is not a Task) — capture + assert non-null so the check is
            // unmistakably exercised.
            var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
                await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false));
            Assert.That(ex, Is.Not.Null);
            Assert.That(proxy.LastHandlerFault, Is.Null, "a Drop is intentional, not a handler crash");
        }

        [Test]
        public async Task AlpacaError_on_a_PUT_echoes_the_client_transaction_id_from_the_form_body() {
            // A PUT method (e.g. slewtocoordinatesasync) carries ClientTransactionID in
            // the form body, not the query — the synthesized error envelope must echo it
            // so the real ASCOM.Alpaca client can correlate the response.
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                DeviceType = "telescope",
                Method = "slewtocoordinatesasync",
                Fault = AlpacaFault.AlpacaError(0x500, "refused"),
            });
            using var client = new HttpClient();

            using var content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["RightAscension"] = "6.0",
                ["Declination"] = "45.0",
                ["ClientTransactionID"] = "777",
            });
            using var resp = await client.PutAsync(
                new Uri(proxy.BaseUri, "api/v1/telescope/0/slewtocoordinatesasync"), content).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var node = JsonNode.Parse(body)!.AsObject();

            Assert.That((int)node["ErrorNumber"]!, Is.EqualTo(0x500));
            Assert.That((long)node["ClientTransactionID"]!, Is.EqualTo(777),
                "the error envelope must echo the PUT body's ClientTransactionID");
        }

        [Test]
        public async Task RewriteValue_overwrites_the_envelope_value_after_forwarding() {
            // The device reports Slewing=false; the "stuck" fault pins it true so a
            // commanded slew never settles in the recovery scenarios.
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                DeviceType = "telescope",
                Method = "slewing",
                Fault = AlpacaFault.RewriteValue("true"),
            });
            using var client = new HttpClient();

            var body = await client.GetStringAsync(new Uri(proxy.BaseUri, "api/v1/telescope/0/slewing")).ConfigureAwait(false);

            Assert.That(ValueOf(body), Is.EqualTo("true"));
            Assert.That(upstream.RequestCount, Is.EqualTo(1), "a rewrite still forwards to the device first");
        }

        [Test]
        public async Task MaxTriggers_expires_the_rule_after_the_given_count() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                DeviceType = "telescope",
                Fault = AlpacaFault.HttpStatus(500),
                MaxTriggers = 1,
            });
            using var client = new HttpClient();

            using var first = await client.GetAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);
            using var second = await client.GetAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);

            Assert.That((int)first.StatusCode, Is.EqualTo(500), "the first call should hit the one-shot fault");
            Assert.That((int)second.StatusCode, Is.EqualTo(200), "the fault should have expired by the second call");
            Assert.That(upstream.RequestCount, Is.EqualTo(1), "only the post-recovery call reaches the device");
        }

        [Test]
        public async Task Delay_with_no_follow_on_still_serves_the_upstream_response() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                Fault = AlpacaFault.Delay(TimeSpan.FromMilliseconds(200)),
            });
            using var client = new HttpClient();

            var sw = Stopwatch.StartNew();
            var body = await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);
            sw.Stop();

            Assert.That(ValueOf(body), Is.EqualTo("false"), "a bare Delay is a slow-but-healthy device");
            // Lower bound well under the 200 ms delay: robust to timer-resolution undershoot
            // and slow CI (a late timer only increases elapsed). Proves the delay happened.
            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(150), "the response must not arrive before the delay");
        }

        [Test]
        public async Task Delay_then_Drop_drops_the_connection_after_the_delay() {
            // The chained "hung then dropped" fault: wait, then tear the connection.
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                Fault = AlpacaFault.Delay(TimeSpan.FromMilliseconds(150), AlpacaFault.Drop()),
            });
            using var client = new HttpClient();

            var sw = Stopwatch.StartNew();
            var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
                await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false));
            sw.Stop();

            Assert.That(ex, Is.Not.Null, "the chained Drop must still abort the connection");
            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(90), "the drop must not happen before the delay");
        }

        [Test]
        public async Task Nested_Delay_applies_every_delay_before_the_terminal_fault() {
            // Delay(a, Delay(b, Drop())) must wait a+b, then drop — not discard the inner.
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                Fault = AlpacaFault.Delay(
                    TimeSpan.FromMilliseconds(80),
                    AlpacaFault.Delay(TimeSpan.FromMilliseconds(80), AlpacaFault.Drop())),
            });
            using var client = new HttpClient();

            var sw = Stopwatch.StartNew();
            var ex = Assert.ThrowsAsync<HttpRequestException>(async () =>
                await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false));
            sw.Stop();

            Assert.That(ex, Is.Not.Null);
            Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(120),
                "both nested delays (80+80 ms) must elapse before the drop");
        }

        [Test]
        public async Task A_method_selector_only_faults_the_matching_method() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule {
                DeviceType = "telescope",
                Method = "slewtocoordinatesasync",
                Fault = AlpacaFault.HttpStatus(500),
            });
            using var client = new HttpClient();

            // A different method (connected) must sail through untouched.
            using var resp = await client.GetAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);

            Assert.That((int)resp.StatusCode, Is.EqualTo(200));
            Assert.That(upstream.RequestCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ClearFaults_restores_clean_pass_through() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            proxy.InjectFault(new AlpacaFaultRule { Fault = AlpacaFault.Drop() });
            proxy.ClearFaults();
            using var client = new HttpClient();

            var body = await client.GetStringAsync(new Uri(proxy.BaseUri, TelescopeConnected)).ConfigureAwait(false);

            Assert.That(ValueOf(body), Is.EqualTo("false"));
        }

        [Test]
        public void RewriteValue_rejects_a_malformed_json_literal_at_construction() {
            // An unquoted string isn't a valid JSON value — must throw here, not silently
            // become a pass-through at request time.
            Assert.Throws<ArgumentException>(() => AlpacaFault.RewriteValue("not json"));
        }

        [Test]
        public void RewriteValue_rejects_a_null_literal_that_would_erase_the_field() {
            // "null" parses but would delete the envelope's Value rather than rewrite it.
            Assert.Throws<ArgumentException>(() => AlpacaFault.RewriteValue("null"));
        }

        [Test]
        public async Task InjectFault_rejects_a_nonpositive_MaxTriggers() {
            await using var upstream = StubAlpaca.Start(valueLiteral: "false");
            await using var proxy = AlpacaFaultProxy.Start(upstream.BaseUri);
            // MaxTriggers = 0 would fire zero times (silently dormant) — reject it.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                proxy.InjectFault(new AlpacaFaultRule { Fault = AlpacaFault.Drop(), MaxTriggers = 0 }));
        }

        private static string? ValueOf(string envelope) => JsonNode.Parse(envelope)?["Value"]?.ToJsonString();

        /// <summary>
        /// A minimal in-process Alpaca device: answers any request with a well-formed
        /// envelope carrying a fixed <c>Value</c> and counts the requests it receives,
        /// so a test can prove whether the proxy forwarded or short-circuited.
        /// Requests are handled one at a time (sequential accept loop) — sufficient for
        /// the current sequential-request tests; a future concurrent scenario that needs
        /// real overlap would serialize here and should fan the loop out instead.
        /// </summary>
        private sealed class StubAlpaca : IAsyncDisposable {
            private readonly HttpListener _listener;
            private readonly string _valueLiteral;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private int _requestCount;
            private volatile NameValueCollection? _lastRequestHeaders;

            private StubAlpaca(HttpListener listener, int port, string valueLiteral) {
                _valueLiteral = valueLiteral;
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = listener; // already bound + started (with retry) by LoopbackListener
                _loop = Task.Run(LoopAsync);
            }

            public Uri BaseUri { get; }

            public int RequestCount => Volatile.Read(ref _requestCount);

            public NameValueCollection? LastRequestHeaders => _lastRequestHeaders;

            public static StubAlpaca Start(string valueLiteral) {
                var (listener, port) = LoopbackListener.Bind();
                return new StubAlpaca(listener, port, valueLiteral);
            }

            private async Task LoopAsync() {
                // GetContextAsync blocks until DisposeAsync stops the listener, which
                // throws HttpListener/ObjectDisposed below — that exception, not this
                // condition, is the loop's real exit. The condition just avoids a
                // needless extra accept if cancellation already fired.
                while (!_cts.IsCancellationRequested) {
                    HttpListenerContext context;
                    try {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    } catch (HttpListenerException) {
                        return;
                    } catch (ObjectDisposedException) {
                        return;
                    }
                    Interlocked.Increment(ref _requestCount);
                    _lastRequestHeaders = context.Request.Headers;
                    var body = Encoding.UTF8.GetBytes(
                        $"{{\"Value\":{_valueLiteral},\"ErrorNumber\":0,\"ErrorMessage\":\"\",\"ClientTransactionID\":0,\"ServerTransactionID\":1}}");
                    var resp = context.Response;
                    resp.StatusCode = 200;
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = body.Length;
                    await resp.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    resp.OutputStream.Close();
                }
            }

            public async ValueTask DisposeAsync() {
                await _cts.CancelAsync().ConfigureAwait(false);
                try {
                    _listener.Stop();
                } catch (ObjectDisposedException) {
                    // already torn down
                }
                try {
                    await _loop.ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // expected on shutdown
                }
                _listener.Close();
                _cts.Dispose();
            }
        }
    }
}
