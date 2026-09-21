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
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>#1066 — the first-connect home to slot 0 is daemon policy. Drives a real
    /// <see cref="FilterWheelService"/> against a loopback Alpaca wheel stub that records every
    /// <c>PUT …/position</c>, and pins the three-way rule: a first connect off slot 0 homes, a
    /// reconnect never does, and an unknown (moving) position at seed skips WITHOUT consuming the
    /// once-per-session claim.</summary>
    [TestFixture]
    public class FilterWheelFirstConnectHomeTest {

        /// <summary>A loopback Alpaca FilterWheel: answers <c>position</c>/<c>names</c>/
        /// <c>focusoffsets</c>/<c>connected</c> with real values and records position writes.</summary>
        private sealed class StubWheel : IAsyncDisposable {
            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private int _position;

            public readonly ConcurrentQueue<int> PositionWrites = new();

            private StubWheel(HttpListener listener, int port, int position) {
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = listener;
                _position = position;
                _loop = Task.Run(LoopAsync);
            }

            public Uri BaseUri { get; }

            /// <summary>The slot the stub reports; -1 = "moving" per ASCOM.</summary>
            public int Position { set => Volatile.Write(ref _position, value); }

            public static StubWheel Start(int position) {
                var (listener, port) = OpenAstroAra.TestHarness.Net.LoopbackListener.Bind();
                return new StubWheel(listener, port, position);
            }

            private async Task LoopAsync() {
                while (!_cts.IsCancellationRequested) {
                    HttpListenerContext ctx;
                    try {
                        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    } catch (HttpListenerException) {
                        return;
                    } catch (ObjectDisposedException) {
                        return;
                    }
                    var leaf = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
                    leaf = leaf[(leaf.LastIndexOf('/') + 1)..].ToUpperInvariant();
                    string value = "true";
                    if (ctx.Request.HttpMethod == "PUT") {
                        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        if (leaf == "POSITION") {
                            foreach (var pair in body.Split('&')) {
                                var kv = pair.Split('=');
                                if (kv.Length == 2 && kv[0].Equals("Position", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) {
                                    PositionWrites.Enqueue(p);
                                    Volatile.Write(ref _position, p);
                                }
                            }
                        }
                        value = "null";
                    } else {
                        value = leaf switch {
                            "POSITION" => Volatile.Read(ref _position).ToString(CultureInfo.InvariantCulture),
                            "NAMES" => "[\"L\",\"R\",\"G\",\"B\"]",
                            "FOCUSOFFSETS" => "[0,0,0,0]",
                            _ => "true",
                        };
                    }
                    var bytes = Encoding.UTF8.GetBytes(
                        $"{{\"Value\":{value},\"ClientTransactionID\":0,\"ServerTransactionID\":0,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}}");
                    try {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        ctx.Response.Close();
                    } catch (HttpListenerException) {
                    } catch (ObjectDisposedException) {
                    }
                }
            }

            public async ValueTask DisposeAsync() {
                await _cts.CancelAsync().ConfigureAwait(false);
                try { _listener.Stop(); } catch (ObjectDisposedException) { }
                _listener.Close();
                try {
                    await _loop.ConfigureAwait(false);
                } catch (HttpListenerException) {
                } catch (ObjectDisposedException) {
                }
                _cts.Dispose();
            }
        }

        private static DiscoveredDeviceDto Device(StubWheel stub, string uniqueId = "wheel-under-test") => new(
            UniqueId: uniqueId, Name: "Bench Wheel", Type: DeviceType.FilterWheel,
            HostName: stub.BaseUri.Host, IpAddress: stub.BaseUri.Host, IpPort: stub.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForStateAsync(FilterWheelService svc, EquipmentConnectionState state, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto?.State == state) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail($"filter wheel never reached {state}");
        }

        private static async Task<bool> WaitForWriteAsync(StubWheel stub, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (!stub.PositionWrites.IsEmpty) {
                    return true;
                }
                await Task.Delay(50);
            }
            return false;
        }

        private static async Task ConnectAsync(FilterWheelService svc, StubWheel stub, string uniqueId = "wheel-under-test") {
            await svc.ConnectAsync(new ConnectRequestDto(Device(stub, uniqueId)), idempotencyKey: null, CancellationToken.None);
            await WaitForStateAsync(svc, EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15));
        }

        private static async Task DisconnectAsync(FilterWheelService svc) {
            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None);
            await WaitForStateAsync(svc, EquipmentConnectionState.Disconnected, TimeSpan.FromSeconds(15));
        }

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task First_connect_off_slot_0_homes_and_a_reconnect_never_does() {
            await using var stub = StubWheel.Start(position: 3);
            using var svc = new FilterWheelService();

            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True, "first connect must home the wheel");
            Assert.That(stub.PositionWrites.TryDequeue(out var target) && target == FilterWheelService.DefaultSlot, Is.True,
                "the home goes to the default slot");

            // Park it elsewhere while offline, reconnect: NO home — a reconnect must never yank a
            // running sequence's filter back to L.
            await DisconnectAsync(svc);
            stub.Position = 2;
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "a reconnect never re-homes");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task Already_at_slot_0_counts_as_homed_and_an_unknown_position_does_not_claim() {
            await using var stub = StubWheel.Start(position: FilterWheelService.DefaultSlot);
            using var svc = new FilterWheelService();

            // Already at 0: no move, but the session's home decision is settled…
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "already at 0 — nothing to move");
            await DisconnectAsync(svc);
            stub.Position = 3;
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "…so a later reconnect never re-homes");
            await DisconnectAsync(svc);

            // A DIFFERENT wheel that reports "moving" (-1) at seed: skipped without claiming, so its
            // next connect with a known position still homes.
            stub.Position = -1;
            await ConnectAsync(svc, stub, uniqueId: "second-wheel");
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "unknown position at seed — don't fight the hardware");
            await DisconnectAsync(svc);
            stub.Position = 3;
            await ConnectAsync(svc, stub, uniqueId: "second-wheel");
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True, "the unclaimed wheel homes on its next known-position connect");
            await DisconnectAsync(svc);
        }
    }
}
