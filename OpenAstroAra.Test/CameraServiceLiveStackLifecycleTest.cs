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
using OpenAstroAra.Stretch;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ↑ara#1169: the stack follows the Live View session. The real Live View loop runs against an unreachable client
    /// (every frame fails fast, the loop self-stops after 10), so no simulator is needed; except the frame-feed tests,
    /// which swap in a synthetic frame source for the loop.
    /// </summary>
    [TestFixture]
    public class CameraServiceLiveStackLifecycleTest {

        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        private static CameraService NewService() {
            var svc = new CameraService();
            svc.WithLiveViewTestClientForTest();
            return svc;
        }

        private static Task StartLiveView(CameraService svc) =>
            svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 2, BinY: 2), CancellationToken.None);

        private static async Task<bool> Completes(Task t) => await Task.WhenAny(t, Task.Delay(Limit)).ConfigureAwait(false) == t;

        private static (ushort[] Pixels, int Width, int Height) SyntheticFrame() =>
            (LiveStackEngineLimitsTest.Field(1), LiveStackEngineLimitsTest.W, LiveStackEngineLimitsTest.H);

        // Falsified by: deleting the FeedLiveStack call in the Live View loop leaves FramesStacked at 0.
        [Test]
        public async Task The_live_view_loop_feeds_frames_to_the_enabled_stack() {
            using var svc = NewService();
            svc.SetLiveViewFrameSourceForTest(() => SyntheticFrame());
            await StartLiveView(svc);
            try {
                ((ILiveStackControl)svc).StartLiveStack();
                var sw = Stopwatch.StartNew();
                while (svc.LiveStack.Snapshot.FramesStacked < 2 && sw.Elapsed < Limit) { await Task.Delay(20); }
                Assert.That(svc.LiveStack.Snapshot.FramesStacked, Is.GreaterThanOrEqualTo(2));
                Assert.That(((ILiveStackControl)svc).GetLiveStackStatus().StackSeq, Is.GreaterThan(0));
            } finally {
                await svc.StopLiveViewAsync();
            }
        }

        // Falsified by: dropping the OSC guard in FeedLiveStack would add the raw mosaic to the stack.
        [Test]
        public void FeedLiveStack_skips_osc_frames() {
            using var svc = new CameraService();
            svc.LiveStack.Enable(3);
            var f = SyntheticFrame();
            svc.FeedLiveStackForTest(f.Pixels, f.Width, f.Height, 3, BayerPattern.RGGB);
            Assert.That(svc.LiveStack.Snapshot.FramesStacked, Is.EqualTo(0));
            svc.FeedLiveStackForTest(f.Pixels, f.Width, f.Height, 3, null);
            Assert.That(svc.LiveStack.Snapshot.FramesStacked, Is.EqualTo(1));
        }

        // Falsified by: removing the try/catch lets a stack failure (null pixels) escape into the loop's self-stop count.
        [Test]
        public void FeedLiveStack_swallows_a_stack_exception() {
            using var svc = new CameraService();
            svc.LiveStack.Enable(3);
            Assert.DoesNotThrow(() => svc.FeedLiveStackForTest(null!, 256, 256, 3, null));
        }

        // Falsified by: no opt-in (Live View start enabling the stack) shows Enabled=true with no stack/start.
        [Test]
        public async Task Live_view_start_leaves_stacking_off_until_stack_start() {
            using var svc = NewService();
            svc.LiveStack.Enable(5); // a stale enabled engine must not survive a Live View start
            await StartLiveView(svc);
            try {
                var s = ((ILiveStackControl)svc).GetLiveStackStatus();
                Assert.That(svc.LiveStack.Snapshot.Enabled, Is.False, "Live View start disables the engine");
                Assert.That(s.Enabled, Is.False);
                Assert.That(s.FramesStacked, Is.EqualTo(0));
            } finally {
                await svc.StopLiveViewAsync();
            }
        }

        // Falsified by: no Disable after the loop in StopLiveViewAsync leaves the stack enabled and bound to the dead session.
        [Test]
        public async Task Stop_live_view_clears_and_disables_the_stack_and_does_not_deadlock() {
            using var svc = NewService();
            await StartLiveView(svc);
            ((ILiveStackControl)svc).StartLiveStack();
            Assert.That(((ILiveStackControl)svc).GetLiveStackStatus().Enabled, Is.True);
            Assert.That(await Completes(svc.StopLiveViewAsync()), Is.True, "StopLiveViewAsync hung");
            Assert.That(svc.LiveStack.Snapshot.Enabled, Is.False);
            Assert.That(((ILiveStackControl)svc).GetLiveStackStatus().Enabled, Is.False);
        }

        // Falsified by: the loop's finally taking _liveViewMutex hangs Stop for good (Stop holds it while it awaits the
        // loop); the loop self-stopping (10 failed frames) while a Stop waits must let both finish.
        [Test]
        public async Task A_loop_that_self_stops_while_a_stop_waits_lets_both_finish() {
            using var svc = NewService();
            await StartLiveView(svc);
            ((ILiveStackControl)svc).StartLiveStack();
            await Task.Delay(700);
            var stop = svc.StopLiveViewAsync();
            Assert.That(await Completes(stop), Is.True, "StopLiveViewAsync hung");
            Assert.That(svc.LiveStack.Snapshot.Enabled, Is.False);
        }

        // Falsified by: no Disable in the loop's finally leaves a self-stopped session's stack enabled.
        [Test]
        public async Task A_loop_self_stop_disables_the_stack_without_a_stop_call() {
            using var svc = NewService();
            await StartLiveView(svc);
            ((ILiveStackControl)svc).StartLiveStack();
            var sw = Stopwatch.StartNew();
            while (svc.GetLiveViewStatus().Active && sw.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(50); }
            Assert.That(svc.GetLiveViewStatus().Active, Is.False, "the loop did not self-stop");
            // Active is cleared before the finally runs the Disable: wait for the engine, bounded.
            sw.Restart();
            while (svc.LiveStack.Snapshot.Enabled && sw.Elapsed < Limit) { await Task.Delay(20); }
            Assert.That(svc.LiveStack.Snapshot.Enabled, Is.False);
            await svc.StopLiveViewAsync();
        }

        // Falsified by: no Disable in CancelLiveViewForDispose leaves an enabled engine behind on teardown.
        [Test]
        public async Task Dispose_disables_the_stack() {
            var svc = NewService();
            await StartLiveView(svc);
            ((ILiveStackControl)svc).StartLiveStack();
            var engine = svc.LiveStack;
            svc.Dispose();
            Assert.That(engine.Snapshot.Enabled, Is.False);
        }

        // Falsified by: a seam that does not clear the engine (the session-end hook every exit path calls).
        [Test]
        public void OnLiveViewSessionEnded_disables_the_engine() {
            using var svc = new CameraService();
            svc.LiveStack.Enable(1);
            svc.OnLiveViewSessionEnded();
            Assert.That(svc.LiveStack.Snapshot.Enabled, Is.False);
        }
    }
}
