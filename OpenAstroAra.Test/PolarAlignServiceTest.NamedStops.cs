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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2107, #2066, #2065 (ARA-10) — on <see cref="FakeGemMount"/>: a stop from outside is named by the reason it was counted
    /// with (the error event's <c>stopped_by</c>), not always "Stop Mount"; a run whose stop was not confirmed, or whose mount
    /// reconnected while it ran, leaves the pose unverified (<c>stop_unconfirmed</c>), and the next Start moves nothing; and
    /// a <c>mount_did_not_stop</c> the run's exit finds after the stop path read its finding is reported exactly once.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A mount that ignores every stop: AbortSlew and MoveAxis 0 both (the RA axis runs on).</summary>
        private static FakeGemMount IgnoresEveryStop() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.AbortIgnored = int.MaxValue;
            mount.MoveAxisStopIgnored = int.MaxValue;
            return mount;
        }

        /// <summary>A run that ends <c>mount_did_not_stop</c> on <paramref name="mount"/> (which ignores every stop), then
        /// the mount really stops: the user cut its power, or it obeyed at last.</summary>
        private static async Task<PolarAlignService> RunEndedNotStoppedAsync(FakeGemMount mount, WsRecorder ws) {
            var svc = await InFirstGemTurnAsync(mount, ws, s => s.StopVerifyWindow = TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
            mount.AbortIgnored = 0;
            mount.MoveAxisStopIgnored = 0;
            await mount.AbortSlewAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(mount.IsMoving, Is.False, mount.Describe());
            return svc;
        }

        private static async Task<RefusalException> StartRefusedAsync(PolarAlignService svc) {
            try {
                await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            } catch (RefusalException refusal) {
                return refusal;
            }
            Assert.Fail("the Start was not refused");
            return null!;
        }

        // ── a stop from outside is named ─────────────────────────────────────────────────────

        // The reactions' refused-park stops used to count as Stop Mount, so a run they ended said "Stop Mount" although the
        // user pressed nothing. The error event now names the stop (stopped_by), and the message says which reaction it was.
        [TestCase(MountStopReasons.FaultReaction, "equipment-fault reaction")]
        [TestCase(MountStopReasons.PoseUnverified, "safety reaction")]
        [TestCase(MountStopReasons.PadStop, "direction pad")]
        public async Task A_stop_from_outside_is_named_in_the_error_by_the_reason_it_was_counted_with(string by, string named) {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws).ConfigureAwait(false);

            await mount.AbortSlewAsync(by, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("stopped_by").GetString(), Is.EqualTo(by), "the app words the end from it");
            Assert.That(error.GetProperty("message").GetString(), Does.Contain(named).And.Not.Contain("Stop Mount"));
            Assert.That(error.TryGetProperty("stop_unconfirmed", out _), Is.False, "the exit confirmed its stop");
        }

        // The app words a named stop in its own language instead of the daemon's message. When the run's exit then could not
        // confirm its stop, that message also carried "cut the mount's power", and no mount_did_not_stop follows (the error
        // reports it itself): the event says so on its own (stop_unconfirmed), so the app keeps the warning.
        [Test]
        public async Task A_named_stop_whose_exit_could_not_confirm_the_stop_says_so_in_the_error() {
            var mount = IgnoresEveryStop();
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws, s => s.StopVerifyWindow = TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);

            await mount.AbortSlewAsync(MountStopReasons.PadStop, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("stopped_by").GetString(), Is.EqualTo(MountStopReasons.PadStop));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain(PolarAlignService.CutPowerSentence));
            Assert.That(error.TryGetProperty("stop_unconfirmed", out var flag) && flag.GetBoolean(), Is.True,
                "the app keeps the cut-power warning when it words the stop itself");
            await Task.Delay(300).ConfigureAwait(false);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.EqualTo(1), "no separate mount_did_not_stop");
        }

        // ── an unconfirmed stop leaves the pose unverified (ARA-10) ──────────────────────────

        [Test]
        public async Task Pose_unverified_after_mount_did_not_stop_blocks_motion() {
            var mount = IgnoresEveryStop();
            var ws = new WsRecorder();
            using var svc = await RunEndedNotStoppedAsync(mount, ws).ConfigureAwait(false);
            Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed), "the run flagged the pose");
            var turns = RaTurns(mount).Length;
            var at = mount.Now;

            var refusal = await StartRefusedAsync(svc).ConfigureAwait(false);

            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(refusal.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.StopUnconfirmed));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(turns), "no turn after the refusal. " + mount.Describe());
            mount.AssertNoMotionSince(at);
        }

        // M28 — the power cycle the user makes after "cut the mount's power": the mount wakes up reporting Home wherever it
        // points (power_cycle_reports_home). The flag from the unconfirmed stop stands: the false Home is not trusted.
        [Test]
        public async Task M28_power_cycle_reports_home_after_mount_did_not_stop_refuses_the_next_Start_with_zero_turns() {
            var mount = IgnoresEveryStop();
            var ws = new WsRecorder();
            using var svc = await RunEndedNotStoppedAsync(mount, ws).ConfigureAwait(false);
            mount.PowerCycle(reportsHome: true);
            var turns = RaTurns(mount).Length;
            var at = mount.Now;

            var refusal = await StartRefusedAsync(svc).ConfigureAwait(false);

            Assert.That(refusal.Parameters["position_unverified_reason"], Is.EqualTo(MountPoseTrust.StopUnconfirmed));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(turns), mount.Describe());
            mount.AssertNoMotionSince(at);
        }

        // M28 — the reconnect case: the mount's session changes while the run is open (a Wi-Fi drop and back, or a power
        // cycle Ara sees as one). The run's stop is confirmed, but the mount may have moved while Ara could not see it.
        [Test]
        public async Task M28_a_reconnect_while_the_run_is_open_leaves_the_pose_unverified_and_the_next_Start_moves_nothing() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);

            mount.Reconnect();
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1), "the stop itself was confirmed");
            Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
            var turns = RaTurns(mount).Length;
            var refusal = await StartRefusedAsync(svc).ConfigureAwait(false);
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(turns), mount.Describe());
        }

        // A run stuck in a call that ignores cancellation never reaches its own exit, and the Stop that gives up on it fences
        // that exit: the stop path's own check is the only one. When the mount cannot be read (a Wi-Fi drop that keeps the
        // session), that check cannot confirm the stop, and the stop path flags the pose itself.
        [Test]
        public async Task A_stuck_run_whose_stop_path_cannot_confirm_the_stop_leaves_the_pose_unverified() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            var solver = new StuckSolver();
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, TenDegreeStore());
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(300);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(1.5);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var session = mount.SessionEpoch;

            try {
                mount.FreshReadsFail = int.MaxValue;
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

                var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
                Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
                Assert.That(mount.SessionEpoch, Is.EqualTo(session), "the same session: only the unconfirmed stop flags it");
                Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed),
                    "flagged while the run is still stuck. " + mount.Describe());
            } finally {
                mount.FreshReadsFail = 0;
                solver.Release();
            }
        }

        // ── the late mount_did_not_stop is told once ─────────────────────────────────────────

        /// <summary>A clock whose timers fire only once the test ends it (a timer made after that fires at once). The stop
        /// path's unwind grace runs on it in the late-report tests: the stop path stops waiting for the run when the test says,
        /// never by the wall clock of a loaded runner.</summary>
        private sealed class HeldClock : TimeProvider {
            private readonly object _lock = new();
            private readonly System.Collections.Generic.List<HeldTimer> _timers = new();
            private bool _ended;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
                var timer = new HeldTimer(callback, state);
                bool ended;
                lock (_lock) {
                    ended = _ended;
                    if (!ended) {
                        _timers.Add(timer);
                    }
                }
                if (ended) {
                    timer.Fire();
                }
                return timer;
            }

            public void End() {
                HeldTimer[] due;
                lock (_lock) {
                    _ended = true;
                    due = _timers.ToArray();
                    _timers.Clear();
                }
                foreach (var timer in due) {
                    timer.Fire();
                }
            }

            private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer {
                private int _done;

                public void Fire() {
                    if (Interlocked.Exchange(ref _done, 1) == 0) {
                        ThreadPool.QueueUserWorkItem(_ => callback(state));
                    }
                }

                public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _done) == 0;

                public void Dispose() => Interlocked.Exchange(ref _done, 1);

                public ValueTask DisposeAsync() {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }

        /// <summary>Stop timings for the late-report tests. The run's exit gives up on its stop after a 1 s window (the mount
        /// ignores every stop until then). The stop path's unwind grace runs on <paramref name="grace"/>: it ends only once the
        /// exit has given up (<see cref="ExitGaveUp"/>), so the stop path's own check always begins after that, on a mount
        /// that obeys, whatever the runner's load.</summary>
        private static Action<PolarAlignService> LateReportTimings(HeldClock grace) => s => {
            s.StopVerifyWindow = TimeSpan.FromSeconds(1.0);
            s.StopResendAfter = TimeSpan.FromSeconds(0.4);
            s.StopVerifyNotBefore = TimeSpan.FromSeconds(0.2);
            s.StopVerifyStillFor = TimeSpan.FromSeconds(0.2);
            s.StopVerifyPoll = TimeSpan.FromMilliseconds(50);
            s.RunUnwindGrace = TimeSpan.FromSeconds(3.0); // on the held clock: never waited out by the wall clock
            s.RunUnwindClock = grace;
        };

        /// <summary>In the exit's pose flag, once it gave up on its stop: the mount obeys from now on (the user's Stop Mount),
        /// the stop path's own check gets a window no load can run out (it is read on every pass), and the stop path stops
        /// waiting for the run.</summary>
        private static void ExitGaveUp(PolarAlignService svc, FakeGemMount mount, HeldClock grace) {
            mount.AbortIgnored = 0;
            mount.MoveAxisStopIgnored = 0;
            mount.AbortSlewAsync(CancellationToken.None).Wait(SignalTimeout);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(30);
            grace.End();
        }

        /// <summary>A Stop of a late-report test: bounded, so a regression that never ends the held grace fails, not hangs.</summary>
        private static Task<OperationAcceptedDto> LateReportStopAsync(PolarAlignService svc) =>
            svc.StopAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        private static async Task<string?[]> ErrorsAfterAsync(WsRecorder ws) {
            var watch = Stopwatch.StartNew();
            while (ws.Count(WsEventCatalog.PolarAlignError) == 0 && watch.Elapsed < TimeSpan.FromSeconds(10)) {
                await Task.Delay(20).ConfigureAwait(false);
            }
            await Task.Delay(300).ConfigureAwait(false);
            return ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetProperty("reason").GetString()).ToArray();
        }

        // The run's exit cannot confirm its stop, but is held (here: at its pose flag) until the stop path has given up waiting
        // for it, confirmed the stop itself, read the finding (none yet), told the client "stopped" and marked the run
        // reported. The exit's "not confirmed" then lands after all that: the exit tells it, once.
        [Test]
        public async Task A_late_exit_that_cannot_confirm_its_stop_after_the_stop_path_read_it_is_reported_once() {
            var mount = IgnoresEveryStop();
            var ws = new WsRecorder();
            var grace = new HeldClock();
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var svc = await InFirstGemTurnAsync(mount, ws, LateReportTimings(grace)).ConfigureAwait(false);
            mount.OnMarkPositionUnverified = _ => {
                // The run's exit gave up on its stop. The mount stops now, so the stop path's own confirmation succeeds; the
                // exit waits here until the stop path has read the finding and reported the end.
                if (held.TrySetResult()) {
                    ExitGaveUp(svc, mount, grace);
                    release.Task.Wait(SignalTimeout);
                }
            };

            await LateReportStopAsync(svc).ConfigureAwait(false);
            Assert.That(held.Task.IsCompleted, Is.True, "the exit gave up on its stop before the stop path answered");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1), "the stop path confirmed the stop itself");
            release.SetResult();

            var errors = await ErrorsAfterAsync(ws).ConfigureAwait(false);
            Assert.That(errors, Is.EqualTo(new[] { PolarAlignService.MountDidNotStop }), "told once, after the end");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"),
                "never left 'stopped' for a stop that was not confirmed");
            Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
        }

        // The plan's race (7c): the stop path has read the finding (none yet) and is telling the client "stopped" when the
        // exit records its "not confirmed". The run is not marked reported yet, so the exit leaves the telling to the stop
        // path, which must tell it when it marks the run reported: told once, by the stop path, never by neither.
        [Test]
        public async Task An_exit_finding_that_lands_while_the_stop_path_reports_the_end_is_told_once_by_the_stop_path() {
            var mount = IgnoresEveryStop();
            var ws = new WsRecorder();
            var grace = new HeldClock();
            var endReporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exitRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exitHeld = 0;
            using var svc = await InFirstGemTurnAsync(mount, ws, LateReportTimings(grace)).ConfigureAwait(false);
            mount.OnMarkPositionUnverified = _ => {
                // The run's exit gave up on its stop; the mount obeys from now on. The exit is held until the stop path is in
                // the middle of reporting the end (its "stopped" event), and records its finding then.
                if (Interlocked.Exchange(ref exitHeld, 1) == 0) {
                    ExitGaveUp(svc, mount, grace);
                    endReporting.Task.Wait(SignalTimeout);
                }
            };
            // The exit's next driver read after its finding is recorded (where it notes where it left the mount): a fresh
            // ReadNowAsync through the seam since #2066's fresh reads, so its kind is "now".
            mount.OnFreshRead = kind => {
                if (kind == "now" && endReporting.Task.IsCompleted) {
                    exitRecorded.TrySetResult();
                }
            };
            ws.OnPublish = type => {
                if (type == WsEventCatalog.PolarAlignStopped) {
                    endReporting.TrySetResult();
                    exitRecorded.Task.Wait(SignalTimeout);
                }
            };

            await LateReportStopAsync(svc).ConfigureAwait(false);
            Assert.That(exitRecorded.Task.IsCompleted, Is.True, "the exit recorded its finding while the stop path reported the end");

            var errors = await ErrorsAfterAsync(ws).ConfigureAwait(false);
            Assert.That(errors, Is.EqualTo(new[] { PolarAlignService.MountDidNotStop }), "told once, by the stop path");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"),
                "never left 'stopped' for a stop that was not confirmed");
            Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed));
        }
    }
}
