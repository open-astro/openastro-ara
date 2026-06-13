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
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Protocol-level coverage for the bench's <see cref="FakeGuider"/> — drives it with
    /// a raw TCP client exactly as the real PHD2 client does (line-framed JSON, event
    /// stream + per-id RPC responses), so the bench-3 scenarios can rely on the wire
    /// contract.
    /// </summary>
    [TestFixture]
    public class FakeGuiderTest {

        [Test]
        public async Task On_connect_it_greets_with_Version_then_AppState() {
            await using var guider = FakeGuider.Start();
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);

            var first = await client.ReadEventAsync().ConfigureAwait(false);
            var second = await client.ReadEventAsync().ConfigureAwait(false);

            Assert.That((string?)first["Event"], Is.EqualTo("Version"));
            Assert.That((string?)second["Event"], Is.EqualTo("AppState"));
            Assert.That((string?)second["State"], Is.EqualTo("Stopped"));
        }

        [Test]
        public async Task An_rpc_request_gets_a_response_with_the_matching_id() {
            await using var guider = FakeGuider.Start();
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);

            await client.SendAsync("""{"method":"get_connected","id":"7"}""").ConfigureAwait(false);
            var response = await client.ReadResponseAsync("7").ConfigureAwait(false);

            Assert.That((string?)response["id"], Is.EqualTo("7"));
            Assert.That((int?)response["result"], Is.EqualTo(0), "an un-overridden method defaults to result:0");
            Assert.That(guider.ReceivedMethods, Does.Contain("get_connected"));
        }

        [Test]
        public async Task OnRpc_override_supplies_the_result_value() {
            await using var guider = FakeGuider.Start();
            guider.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);

            await client.SendAsync("""{"method":"get_pixel_scale","id":"3"}""").ConfigureAwait(false);
            var response = await client.ReadResponseAsync("3").ConfigureAwait(false);

            Assert.That((double?)response["result"], Is.EqualTo(1.5));
        }

        [Test]
        public async Task A_factory_override_can_read_the_request_params() {
            await using var guider = FakeGuider.Start();
            // Echo back whatever connected flag was set, like a real set_connected→get_connected pair.
            guider.OnRpc("set_connected", req => {
                var p = req["params"]?.AsArray();
                return JsonValue.Create(p is { Count: > 0 } ? (bool?)p[0] : false);
            });
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);

            await client.SendAsync("""{"method":"set_connected","params":[true],"id":"9"}""").ConfigureAwait(false);
            var response = await client.ReadResponseAsync("9").ConfigureAwait(false);

            Assert.That((bool?)response["result"], Is.True);
        }

        [Test]
        public async Task BroadcastAsync_pushes_an_event_to_a_connected_client() {
            await using var guider = FakeGuider.Start();
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);
            // Drain the greeting first.
            await client.ReadEventAsync().ConfigureAwait(false);
            await client.ReadEventAsync().ConfigureAwait(false);

            await guider.BroadcastAsync(PhdEvents.StarLost()).ConfigureAwait(false);
            var evt = await client.ReadEventAsync().ConfigureAwait(false);

            Assert.That((string?)evt["Event"], Is.EqualTo("StarLost"));
        }

        [Test]
        public async Task SetOnConnectEvents_replaces_the_greeting() {
            await using var guider = FakeGuider.Start();
            guider.SetOnConnectEvents(PhdEvents.Version(), PhdEvents.AppState("Guiding"));
            using var client = await ConnectAsync(guider.Port).ConfigureAwait(false);

            await client.ReadEventAsync().ConfigureAwait(false); // Version
            var state = await client.ReadEventAsync().ConfigureAwait(false);

            Assert.That((string?)state["State"], Is.EqualTo("Guiding"));
        }

        [Test]
        public async Task Two_clients_each_get_the_greeting_and_a_broadcast() {
            await using var guider = FakeGuider.Start();
            using var a = await ConnectAsync(guider.Port).ConfigureAwait(false);
            using var b = await ConnectAsync(guider.Port).ConfigureAwait(false);
            foreach (var c in new[] { a, b }) {
                await c.ReadEventAsync().ConfigureAwait(false);
                await c.ReadEventAsync().ConfigureAwait(false);
            }

            await guider.BroadcastAsync(PhdEvents.SettleDone()).ConfigureAwait(false);

            Assert.That((string?)(await a.ReadEventAsync().ConfigureAwait(false))["Event"], Is.EqualTo("SettleDone"));
            Assert.That((string?)(await b.ReadEventAsync().ConfigureAwait(false))["Event"], Is.EqualTo("SettleDone"));
        }

        private static async Task<TestClient> ConnectAsync(int port) {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            return new TestClient(tcp);
        }

        /// <summary>A minimal PHD2-style line client: writes requests, reads framed JSON messages.</summary>
        private sealed class TestClient : IDisposable {
            private readonly TcpClient _tcp;
            private readonly StreamReader _reader;
            private readonly NetworkStream _stream;

            public TestClient(TcpClient tcp) {
                _tcp = tcp;
                _stream = tcp.GetStream();
                _reader = new StreamReader(_stream, Encoding.ASCII);
            }

            public async Task SendAsync(string json) {
                var bytes = Encoding.ASCII.GetBytes(json + Environment.NewLine);
                await _stream.WriteAsync(bytes).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }

            /// <summary>Reads the next framed JSON message (event or response).</summary>
            public async Task<JsonObject> ReadEventAsync() {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("connection closed before a message arrived");
                return (JsonObject)JsonNode.Parse(line)!;
            }

            /// <summary>Reads messages until one carries the given RPC id, skipping interleaved events.</summary>
            public async Task<JsonObject> ReadResponseAsync(string id) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                string? line;
                while ((line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) is not null) {
                    var obj = (JsonObject)JsonNode.Parse(line)!;
                    if ((string?)obj["id"] == id) {
                        return obj;
                    }
                }
                throw new InvalidOperationException($"no response with id={id}");
            }

            public void Dispose() {
                _reader.Dispose();
                _stream.Dispose();
                _tcp.Dispose();
            }
        }
    }
}
