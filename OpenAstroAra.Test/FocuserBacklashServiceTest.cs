#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageAnalysis;
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

    /// <summary>#1541 — the daemon-side backlash wiring, driven through a real <see cref="FocuserService"/> against a
    /// loopback Alpaca focuser stub that records every <c>PUT …/move</c>. Each test pins a line the pure
    /// <see cref="FocuserBacklash"/> tests cannot see: offset accumulation, the logical-position subtraction, the
    /// reset on reconnect and the sweep's suppression.</summary>
    [TestFixture]
    [Category("IO")]
    public class FocuserBacklashServiceTest {

        private static readonly int[] MovesAfterReversal = [1200, 970];
        private static readonly int[] MovesAfterSecondReversal = [1200, 970, 1210];
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(150);

        private sealed class StubFocuser : IAsyncDisposable {
            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private int _position;
            private int _movingLeft;
            private int _isMovingReads;

            /// <summary>After a move the stub answers this many <c>ismoving</c> reads with true, then false.</summary>
            public int MovingReadsAfterMove { get; set; }

            /// <summary>Every <c>ismoving</c> read so far: the service reads it once per refresh tick.</summary>
            public int IsMovingReads => Volatile.Read(ref _isMovingReads);

            public readonly ConcurrentQueue<int> Moves = new();

            private StubFocuser(HttpListener listener, int port, int position) {
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = listener;
                _position = position;
                _loop = Task.Run(LoopAsync);
            }

            public Uri BaseUri { get; }

            /// <summary>The hardware position the stub reports (a move lands there at once).</summary>
            public int Position { get => Volatile.Read(ref _position); set => Volatile.Write(ref _position, value); }

            public static StubFocuser Start(int position) {
                var (listener, port) = OpenAstroAra.TestHarness.Net.LoopbackListener.Bind();
                return new StubFocuser(listener, port, position);
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
                    string value;
                    if (ctx.Request.HttpMethod == "PUT") {
                        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        if (leaf == "MOVE") {
                            foreach (var pair in body.Split('&')) {
                                var kv = pair.Split('=');
                                if (kv.Length == 2 && kv[0].Equals("Position", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) {
                                    Moves.Enqueue(p);
                                    Position = p;
                                    Volatile.Write(ref _movingLeft, MovingReadsAfterMove);
                                }
                            }
                        }
                        value = "null";
                    } else {
                        value = leaf switch {
                            "POSITION" => Position.ToString(CultureInfo.InvariantCulture),
                            "MAXSTEP" => "50000",
                            "STEPSIZE" => "1",
                            "TEMPERATURE" => "10",
                            "ISMOVING" => (Interlocked.Increment(ref _isMovingReads) > 0 && Interlocked.Decrement(ref _movingLeft) >= 0) ? "true" : "false",
                            "TEMPCOMP" or "TEMPCOMPAVAILABLE" => "false",
                            _ => "true", // connected, absolute
                        };
                    }
                    var bytes = Encoding.UTF8.GetBytes($"{{\"Value\":{value},\"ClientTransactionID\":0,\"ServerTransactionID\":0,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}}");
                    try {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        ctx.Response.Close();
                    } catch (HttpListenerException) {
                    } catch (ObjectDisposedException) {
                    } catch (InvalidOperationException) when (_cts.IsCancellationRequested) {
                    }
                }
            }

            public async ValueTask DisposeAsync() {
                await _cts.CancelAsync().ConfigureAwait(false);
                try { _listener.Stop(); } catch (ObjectDisposedException) { }
                OpenAstroAra.TestHarness.Net.LoopbackListener.Close(_listener);
                try {
                    await _loop.ConfigureAwait(false);
                } catch (HttpListenerException) {
                } catch (ObjectDisposedException) {
                }
                _cts.Dispose();
            }
        }

        private static InMemoryProfileStore Store(int backlashIn, int backlashOut) {
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(new AutofocusSettingsDto("hfr_v_curve", 4, 100, 2, 1, "L", false, 1.0, 10, 0, false, true, StepSizeAuto: false,
                BacklashInSteps: backlashIn, BacklashOutSteps: backlashOut));
            return store;
        }

        private static async Task<FocuserDto?> PollAsync(FocuserService svc, Func<FocuserDto, bool> done) {
            for (var i = 0; i < 200; i++) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto is not null && done(dto)) {
                    return dto;
                }
                await Task.Delay(50);
            }
            return await svc.GetAsync(CancellationToken.None);
        }

        private static async Task ConnectAsync(FocuserService svc, StubFocuser stub) {
            var device = new DiscoveredDeviceDto("focuser-under-test", "Bench Focuser", DeviceType.Focuser,
                stub.BaseUri.Host, stub.BaseUri.Host, stub.BaseUri.Port, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None);
            var dto = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected && d.Capabilities is not null);
            Assert.That(dto?.Capabilities, Is.Not.Null, "connected with capabilities");
        }

        [Test]
        public async Task A_reversal_sends_target_plus_backlash_and_every_reported_position_is_the_logical_target() {
            await using var stub = StubFocuser.Start(1000);
            using var svc = new FocuserService(profiles: Store(backlashIn: 30, backlashOut: 40)) { RefreshPeriod = Tick };
            await ConnectAsync(svc, stub);

            Assert.That(await svc.MoveFocuser(1200, CancellationToken.None), Is.EqualTo(1200), "the first move has no direction to reverse");
            var settled = await svc.MoveFocuser(1000, CancellationToken.None);

            Assert.That(stub.Moves, Is.EqualTo(MovesAfterReversal), "the reversal went 30 steps further in");
            Assert.That(settled, Is.EqualTo(1000), "the returned position is the user's scale (Logical subtraction)");
            Assert.That(svc.GetInfo().Position, Is.EqualTo(1000), "and so is the cached runtime");
        }

        [Test]
        public async Task A_rest_move_reversal_reports_the_logical_target_in_the_runtime() {
            await using var stub = StubFocuser.Start(1000);
            using var svc = new FocuserService(profiles: Store(backlashIn: 30, backlashOut: 40)) { RefreshPeriod = Tick };
            await ConnectAsync(svc, stub);

            await svc.MoveAsync(new FocuserMoveRequestDto(1200), idempotencyKey: null, CancellationToken.None);
            Assert.That((await PollAsync(svc, d => d.Runtime.Position == 1200))?.Runtime.Position, Is.EqualTo(1200));
            await svc.MoveAsync(new FocuserMoveRequestDto(1000), idempotencyKey: null, CancellationToken.None);

            var dto = await PollAsync(svc, d => d.Runtime.Position == 1000);
            Assert.That(stub.Moves, Is.EqualTo(MovesAfterReversal));
            Assert.That(dto?.Runtime.Position, Is.EqualTo(1000), "the refresh subtracts the standing offset");
        }

        [Test]
        public async Task A_compensated_move_reports_backlash_compensating_until_it_settles() {
            await using var stub = StubFocuser.Start(1000);
            using var svc = new FocuserService(profiles: Store(backlashIn: 30, backlashOut: 40)) { RefreshPeriod = Tick };
            await ConnectAsync(svc, stub);
            await svc.MoveFocuser(1200, CancellationToken.None);

            // The in reversal is compensated; the focuser reports motion for the next 8 reads, i.e. several refresh ticks.
            stub.MovingReadsAfterMove = 8;
            await svc.MoveAsync(new FocuserMoveRequestDto(1000), idempotencyKey: null, CancellationToken.None);
            var readsAtDispatch = stub.IsMovingReads;
            while (stub.IsMovingReads < readsAtDispatch + 3) {
                await Task.Delay(20);
            }

            Assert.That((await svc.GetAsync(CancellationToken.None))!.Runtime.State, Is.EqualTo("backlash_compensating"),
                "still compensating two refresh ticks after the dispatch");
            Assert.That(svc.GetInfo().IsMoving, Is.True);
            var settled = await PollAsync(svc, d => d.Runtime.State == "idle");
            Assert.That(settled?.Runtime.State, Is.EqualTo("idle"), "cleared once the focuser stops moving");
            Assert.That(settled!.Runtime.Position, Is.EqualTo(1000));

            // A plain move (no reversal) is just "moving".
            await svc.MoveAsync(new FocuserMoveRequestDto(800), idempotencyKey: null, CancellationToken.None);
            Assert.That((await PollAsync(svc, d => d.Runtime.State != "idle"))?.Runtime.State, Is.EqualTo("moving"));
        }

        [Test]
        public async Task A_relative_move_starts_from_the_logical_position_and_the_offset_accumulates() {
            await using var stub = StubFocuser.Start(1000);
            using var svc = new FocuserService(profiles: Store(backlashIn: 30, backlashOut: 40)) { RefreshPeriod = Tick };
            await ConnectAsync(svc, stub);
            await svc.MoveFocuser(1200, CancellationToken.None);
            await svc.MoveFocuser(1000, CancellationToken.None); // hardware 970, offset -30

            var settled = await svc.MoveFocuserRelative(200, CancellationToken.None); // logical 1000 -> 1200, out reversal

            Assert.That(stub.Moves, Is.EqualTo(MovesAfterSecondReversal), "1200 logical - 30 standing + 40 out backlash");
            Assert.That(settled, Is.EqualTo(1200));
        }

        [Test]
        public async Task A_reconnect_resets_the_offset_and_the_last_direction() {
            await using var stub = StubFocuser.Start(1000);
            using var svc = new FocuserService(profiles: Store(backlashIn: 30, backlashOut: 40)) { RefreshPeriod = Tick };
            await ConnectAsync(svc, stub);
            await svc.MoveFocuser(1200, CancellationToken.None);
            await svc.MoveFocuser(1000, CancellationToken.None); // hardware 970, offset -30, last direction in
            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None);
            await PollAsync(svc, d => d.State == EquipmentConnectionState.Disconnected);

            await ConnectAsync(svc, stub);

            Assert.That(svc.GetInfo().Position, Is.EqualTo(970), "a fresh session reads the hardware as it is: no standing offset");
            Assert.That(await svc.MoveFocuser(1500, CancellationToken.None), Is.EqualTo(1500));
            Assert.That(stub.Moves.ToArray()[^1], Is.EqualTo(1500), "no last direction on a fresh session: an out move is not a reversal");
        }

        [Test]
        public async Task The_autofocus_sweep_adds_no_backlash_steps() {
            await using var stub = StubFocuser.Start(10_000);
            // In only: the sweep's out and in reversals would otherwise add +40 and -30 and could cancel to a zero offset.
            var store = Store(backlashIn: 30, backlashOut: 0);
            using var focuser = new FocuserService(profiles: store) { RefreshPeriod = Tick };
            await ConnectAsync(focuser, stub);
            await focuser.MoveFocuser(10_100, CancellationToken.None); // a direction for the sweep's first reversal to reverse
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(new AnalysisFrame(new ushort[64 * 64], 64, 64, DateTimeOffset.UnixEpoch)));
            using var sweep = new AutofocusSweepService(store, focuser, frames.Object,
                travelRange: _ => Task.FromResult<(int, int)?>((0, 50_000)),
                metric: (_, _) => {
                    var delta = (stub.Position - 9_850) / 100.0;
                    var hfr = 1.5 + 0.2 * delta * delta;
                    return new StarDetectionResult { AverageHFR = hfr, DetectedStars = 42, StarList = Stars(42, hfr) };
                });

            Assert.That(await sweep.RunAutofocusAsync(new Progress<ApplicationStatus>(), CancellationToken.None), Is.True);

            Assert.That(focuser.GetInfo().Position, Is.EqualTo(stub.Position),
                "no standing offset after the sweep: the hardware moved exactly where the sweep asked");
        }

        private static System.Collections.Generic.List<DetectedStar> Stars(int count, double hfr) {
            var stars = new System.Collections.Generic.List<DetectedStar>(count);
            for (var i = 0; i < count; i++) {
                stars.Add(new DetectedStar { HFR = hfr, FWHM = hfr * 2.0, Roundness = 0.9, PeakToBackground = 8.0, DonutOuterDiameter = hfr * 2.0 });
            }
            return stars;
        }
    }
}
