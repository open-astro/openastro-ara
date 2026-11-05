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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ↑ara#1585: a Live View loop nobody fetches frames from (client killed, phone left the LAN) ends itself
    /// after a bound instead of holding the camera until a daemon restart. The clock is injected, so the
    /// 30 s bound is crossed by advancing a fake clock, not by waiting.
    /// </summary>
    [TestFixture]
    public class CameraServiceLiveViewIdleStopTest {

        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        // The test client is unreachable, so the connection probe also ends the loop after ~6 s; the idle
        // stop is told apart by its own log line, not by "Active went false".
        private sealed class Recorder : Microsoft.Extensions.Logging.ILogger<CameraService> {
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Msgs = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId e, TState st, Exception? ex, Func<TState, Exception?, string> f) => Msgs.Enqueue(f(st, ex));
        }
        private sealed class ManualClock : TimeProvider {
            private long _ticks;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        }

        private static (CameraService Svc, ManualClock Clock, Recorder Log) NewService() {
            var clock = new ManualClock();
            var log = new Recorder();
            var svc = new CameraService(log);
            svc.WithLiveViewTestClientForTest();
            svc.SetLiveViewClockForTest(clock);
            svc.SetLiveViewFrameSourceForTest(() => (LiveStackEngineLimitsTest.Field(1), LiveStackEngineLimitsTest.W, LiveStackEngineLimitsTest.H));
            return (svc, clock, log);
        }

        private static Task Start(CameraService svc) =>
            svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 2, BinY: 2), CancellationToken.None);

        private static async Task<bool> Until(Func<bool> condition) {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed < Limit) { await Task.Delay(20); }
            return condition();
        }

        [Test]
        public async Task A_loop_nobody_fetches_frames_from_stops_itself_after_the_bound() {
            var (svc, clock, log) = NewService();
            using var disposeSvc = svc;
            await Start(svc);
            try {
                Assert.That(await Until(() => svc.GetLiveViewStatus().FrameSeq > 0), Is.True, "loop never produced a frame");
                clock.Advance(CameraService.LiveViewIdleStop + TimeSpan.FromSeconds(1));
                Assert.That(await Until(() => !svc.GetLiveViewStatus().Active), Is.True, "loop kept running with no client");
                Assert.That(log.Msgs, Has.Some.Contain("no client fetched a frame"));
                Assert.That(svc.GetLiveViewFrame(), Is.Null, "the stop clears the frame like a normal stop");
            } finally {
                await svc.StopLiveViewAsync();
            }
        }

        [Test]
        public async Task A_loop_whose_frames_are_being_fetched_keeps_going() {
            var (svc, clock, log) = NewService();
            using var disposeSvc = svc;
            await Start(svc);
            try {
                Assert.That(await Until(() => svc.GetLiveViewStatus().FrameSeq > 0), Is.True);
                // 6 x 10 s = 60 s of fake time, a fetch between every step: never 30 s idle.
                for (var i = 0; i < 6; i++) {
                    Assert.That(svc.GetLiveViewFrame(), Is.Not.Null);
                    clock.Advance(TimeSpan.FromSeconds(10));
                    await Task.Delay(250); // several loop iterations see the advanced clock
                }
                Assert.That(svc.GetLiveViewStatus().Active, Is.True);
                Assert.That(log.Msgs, Has.None.Contain("no client fetched a frame"));
            } finally {
                await svc.StopLiveViewAsync();
            }
        }
    }
}
