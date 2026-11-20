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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
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
    /// #2066 — the holes left in the abort contract of #2048 (the stop-contract PR of its plan), on
    /// <see cref="FakeGemMount"/>: a stop to a mount that is not connected goes out once it reconnects, inside its window;
    /// Polar Align counts as its own only the stops tagged <c>polar_align</c>; a takeover that gives up waiting, or an end
    /// that abandons a stuck run, fences the run, so no late stop of it lands on a park. The tests that time the contract
    /// run on its own defaults, not the bench's.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>The daemon's stop timings (#2048's abort contract: 2 s before any confirmation, two reads 1.5 s apart,
        /// resent every 2 s, an 8 s window, 3 s per command), read from a service built with its defaults, in place of the
        /// bench's compressed ones.</summary>
        private static void UseTheContractsStopTimings(PolarAlignService svc) {
            var mount = NewMount().Object;
            using var daemon = new PolarAlignService(NewFake(), NullLogger<PolarAlignService>.Instance, new ScriptedSolver(),
                mount, MediatorMotionControl.For(mount), NewStore(), new WsRecorder(), log: null,
                new FakeFrameFetcher());
            svc.StopCommandTimeout = daemon.StopCommandTimeout;
            svc.StopVerifyPoll = daemon.StopVerifyPoll;
            svc.StopVerifyStillFor = daemon.StopVerifyStillFor;
            svc.StopVerifyNotBefore = daemon.StopVerifyNotBefore;
            svc.StopResendAfter = daemon.StopResendAfter;
            svc.StopVerifyWindow = daemon.StopVerifyWindow;
        }

        /// <summary>A run on the fake GEM in its first turn, at the daemon's motor speeds (the 30° step: about 12 s).</summary>
        private static async Task<PolarAlignService> InFirstGemTurnAsync(FakeGemMount mount, WsRecorder ws, Action<PolarAlignService>? setUp = null) {
            var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            setUp?.Invoke(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            return svc;
        }

        private static StopEntry[] AbortsSince(FakeGemMount mount, TimeSpan at) =>
            mount.Stops.Where(s => s.Kind == StopKind.Abort && s.At >= at).ToArray();

        // ── a stop to a mount that is not connected ──────────────────────────────────────────

        [Test]
        public async Task A_stop_that_finds_the_mount_disconnected_goes_out_once_it_reconnects_and_is_confirmed() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws, s => {
                UseTheContractsStopTimings(s);
                s.AxisMovePoll = TimeSpan.FromSeconds(2); // the run waits in its poll when the Stop lands
            }).ConfigureAwait(false);

            await mount.Disconnect().ConfigureAwait(false); // the Wi-Fi drops mid-turn; the RA motor runs on
            var stop = svc.StopAsync(null, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var reconnectedAt = mount.Now;
            await mount.Connect().ConfigureAwait(false);
            await stop.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var abort = AbortsSince(mount, TimeSpan.Zero).FirstOrDefault();
            Assert.That(abort, Is.Not.Null, "no AbortSlew reached the mount. " + mount.Describe());
            Assert.That(abort!.At, Is.GreaterThanOrEqualTo(reconnectedAt), "the stop went out once the mount was back");
            Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "confirmed: no mount_did_not_stop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
            mount.AssertNoMotionSince(abort.At);
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public async Task A_stop_that_finds_the_mount_disconnected_keeps_trying_for_its_window_then_reports_mount_did_not_stop() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws, s => {
                UseTheContractsStopTimings(s);
                s.StopVerifyWindow = TimeSpan.FromSeconds(3); // shorter than the daemon's 8 s, for the test's time
                s.AxisMovePoll = TimeSpan.FromSeconds(2);
            }).ConfigureAwait(false);

            await mount.Disconnect().ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var elapsed = watch.Elapsed;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(svc.StopVerifyWindow - TimeSpan.FromMilliseconds(250)),
                "the stop waited its whole window for the mount to come back before it gave up");
            Assert.That(AbortsSince(mount, TimeSpan.Zero), Is.Empty, "nothing reached a mount that never came back");
            Assert.That(mount.IsMoving, Is.True, "the RA motor runs on: the reported 'not confirmed' is the truth");
        }

        // ── Polar Align's own stops are the ones tagged polar_align ──────────────────────────

        // The old exit of a failed run resends its AbortSlew while the next Start drains it. One resend is refused before the
        // daemon counts it (the mount dropped off), and the user's Stop Mount lands while it is refused: the count moved, but
        // not by Polar Align. Counted as its own, the Stop Mount vanished from the new run's baseline, and that run turned
        // the mount after a Stop Mount pressed after its Start.
        [Test]
        public async Task A_Stop_Mount_racing_a_refused_Polar_Align_abort_still_stops_the_next_run_before_it_moves() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.MoveAxisRefused = true; // the first run fails at its first turn
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.StopResendAfter = TimeSpan.FromSeconds(1); // the old exit resends after the next Start was called
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(3);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(10);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while ((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State != "failed") {
                Assert.That(watch.Elapsed, Is.LessThan(SignalTimeout), "the refused move never failed the run");
                await Task.Delay(5).ConfigureAwait(false);
            }
            mount.MoveAxisRefused = false;
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            // The new run's stop count is read at this call; the race lands after it, while the Start drains the old run.
            var start = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);
            var raced = 0;
            mount.RefuseAbort = reason => {
                if (reason != PolarAlignService.AbortReason || Interlocked.Exchange(ref raced, 1) == 1) {
                    return false;
                }
                _ = mount.AbortSlewAsync(CancellationToken.None); // Stop Mount (user_request), counted
                return true;
            };
            await start.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var mark = Mark(svc);
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(Volatile.Read(ref raced), Is.EqualTo(1), "the old exit resent its stop after the Start was called");
            Assert.That(status.State, Is.EqualTo("failed"), mount.Describe());
            var error = ws.Events.Skip(mark).First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Stop Mount"));
            Assert.That(RaTurns(mount), Is.Empty, "no turn after the Stop Mount. " + mount.Describe());
        }

        // The same resend, but the count moves by more than Polar Align can read one by one (OwnAbortsReadable, 16) while it
        // is on its way. Even when every one of those counts is tagged polar_align, none is taken as Polar Align's own: a
        // stop from outside then ends the run, never the other way round.
        [Test]
        public async Task A_send_that_moves_the_count_past_what_it_can_read_counts_none_as_own_and_the_next_run_ends() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.MoveAxisRefused = true; // the first run fails at its first turn
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.StopResendAfter = TimeSpan.FromSeconds(1); // the old exit resends after the next Start was called
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(3);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(10);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while ((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State != "failed") {
                Assert.That(watch.Elapsed, Is.LessThan(SignalTimeout), "the refused move never failed the run");
                await Task.Delay(5).ConfigureAwait(false);
            }
            mount.MoveAxisRefused = false;
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            var start = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);
            var burst = 0;
            mount.RefuseAbort = reason => {
                if (reason != PolarAlignService.AbortReason || Interlocked.Exchange(ref burst, 1) == 1) {
                    return false;
                }
                for (var i = 0; i < 17; i++) {
                    mount.FenceMotion(PolarAlignService.AbortReason); // 17 counts, then the send's own: 18 in one send
                }
                return false;
            };
            await start.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var mark = Mark(svc);
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(Volatile.Read(ref burst), Is.EqualTo(1), "the old exit resent its stop after the Start was called");
            Assert.That(status.State, Is.EqualTo("failed"), mount.Describe());
            var error = ws.Events.Skip(mark).First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(RaTurns(mount), Is.Empty, "no turn after a send whose counts could not be read. " + mount.Describe());
        }

        // ── a takeover that gives up waiting fences the run ──────────────────────────────────

        // The user's Stop waits for a run stuck in a call that ignores cancellation; the safety park's first step (bounded at
        // 10 s) waits behind it. Past the takeover's bound the park goes out, and then the Stop gave up on the run and sent its
        // own stop round (an AbortSlew, since the parking mount slews): the park was aborted. Fenced, nothing lands on it.
        [Test]
        public async Task A_takeover_that_outwaits_its_bound_fences_the_stuck_run_so_no_stop_lands_on_the_park() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.GoToRateDegPerSec = 15.0; // a park of about 4 s
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var solver = new StuckSolver();
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, TenDegreeStore());
            svc.RunUnwindGrace = TimeSpan.FromSeconds(2.5); // the Stop gives up on the stuck run, inside the park
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(200);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                var stop = svc.StopAsync(null, CancellationToken.None);
                var watch = Stopwatch.StartNew();
                // The safety park's first step, bounded as SafetyReactionService bounds it.
                await svc.StopForTakeoverAsync("safety_park", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var answeredIn = watch.Elapsed;
                var parkAt = mount.Now;
                var park = mount.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                await stop.WaitAsync(SignalTimeout).ConfigureAwait(false);
                solver.Release(); // the stuck run unwinds now, in the middle of the park
                var parked = await park.WaitAsync(SignalTimeout).ConfigureAwait(false);

                // Well under the 2.5 s grace, with 1.3 s of slack over the bound for a loaded runner's scheduling.
                Assert.That(answeredIn, Is.LessThan(TimeSpan.FromSeconds(1.5)), "the takeover answered at its bound, not after the stuck run");
                Assert.That(parked, Is.True, "the park arrived. " + mount.Describe());
                Assert.That(mount.Stops.Where(s => s.At >= parkAt && s.Kind != StopKind.LeaseStop), Is.Empty,
                    "no stop after the park went out. " + mount.Describe());
            } finally {
                solver.Release();
            }
        }

        // A turn's read hangs on the wire (an Alpaca GET that ignores cancellation). The takeover gives up on the run at its
        // bound and fences it, and the park goes out. When the read finally returns, the turn's own clean-up stop (MoveAxis 0
        // in its finally) went out regardless of the fence and landed on the park.
        [Test]
        public async Task A_turn_stuck_on_a_hung_read_sends_no_motor_stop_after_a_takeover_fenced_it() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.GoToRateDegPerSec = 15.0;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, benchMotors: false); // the daemon's speeds: the hang lands mid-turn
            svc.RunUnwindGrace = TimeSpan.FromSeconds(2.5);
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(500);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mount.HangReads = hang;
            await mount.ReadHung.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                await svc.StopForTakeoverAsync("safety_park", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var parkAt = mount.Now;
                var park = mount.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                mount.HangReads = null;
                hang.TrySetResult(); // the turn's read returns now, in the middle of the park
                var parked = await park.WaitAsync(SignalTimeout).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); // the turn's finally has run by now

                Assert.That(parked, Is.True, "the park arrived. " + mount.Describe());
                Assert.That(mount.Stops.Where(s => s.At >= parkAt && s.Kind != StopKind.LeaseStop), Is.Empty,
                    "no stop from the fenced turn. " + mount.Describe());
            } finally {
                mount.HangReads = null;
                hang.TrySetResult();
            }
        }

        // A run fails on its own and its exit confirms the stop, then hangs on its last pointing read. The takeover gives up at
        // its bound and fences the run, and the park goes out (tracking on first, as iOptron needs). When the read returns, the
        // exit turned tracking off in the middle of the park.
        [Test]
        public async Task An_exit_whose_last_read_hangs_past_a_takeover_writes_no_tracking_after_it() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.GoToRateDegPerSec = 15.0;
            mount.MoveAxisRefused = true; // the run fails at its first turn, and its exit stops and confirms
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.RunUnwindGrace = TimeSpan.FromSeconds(2.5);
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(500);
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readsAfterStop = 0;
            mount.OnFreshRead = kind => {
                // The exit's confirmation reads the mount twice (the anchor, then the still read); its third is the last read.
                if (kind == "now" && mount.Stops.Count > 0 && Interlocked.Increment(ref readsAfterStop) == 3) {
                    mount.HangReads = hang;
                }
            };
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.ReadHung.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                await svc.StopForTakeoverAsync("safety_park", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var parkAt = mount.Now;
                Assert.That(mount.SetTrackingEnabled(true), Is.True, "the park turns tracking on first");
                var park = mount.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                mount.HangReads = null;
                hang.TrySetResult(); // the exit's read returns now, in the middle of the park
                var parked = await park.WaitAsync(SignalTimeout).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); // the exit has finished by now

                Assert.That(parked, Is.True, "the park arrived. " + mount.Describe());
                Assert.That(mount.Commands.Where(c => c.At >= parkAt).Select(c => c.Text), Has.None.EqualTo("tracking off"),
                    "no tracking write from the fenced exit. " + mount.Describe());
                Assert.That(mount.Stops.Where(s => s.At >= parkAt && s.Kind != StopKind.LeaseStop), Is.Empty,
                    "no stop from the fenced exit. " + mount.Describe());
            } finally {
                mount.HangReads = null;
                hang.TrySetResult();
            }
        }

        // A run fails on its own and its exit is confirming the stop when a read hangs (bounded by StopCommandTimeout). The
        // takeover gives up meanwhile, fences the run, and the park goes out. The read then times out with no answer, and the
        // loop's resend round (an AbortSlew, since no reading said still) went out after the check at the top of the loop had
        // already passed: the park was aborted. The round checks the fence itself.
        [Test]
        public async Task An_exit_confirming_its_stop_through_a_hung_read_sends_no_resend_after_a_takeover_fenced_it() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.GoToRateDegPerSec = 15.0;
            mount.MoveAxisRefused = true; // the run fails at its first turn, and its exit stops and confirms
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.RunUnwindGrace = TimeSpan.FromSeconds(3);
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(300);
            svc.StopCommandTimeout = TimeSpan.FromSeconds(1.5); // the hung read gives up inside the park
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mount.OnFreshRead = kind => {
                if (kind == "now" && mount.Stops.Count > 0) {
                    mount.HangReads = hang; // the exit's first confirmation read
                }
            };
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.ReadHung.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                await svc.StopForTakeoverAsync("safety_park", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var parkAt = mount.Now;
                var park = mount.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                var parked = await park.WaitAsync(SignalTimeout).ConfigureAwait(false);
                await Task.Delay(svc.StopCommandTimeout).ConfigureAwait(false); // the hung read has given up by now

                Assert.That(parked, Is.True, "the park arrived. " + mount.Describe());
                Assert.That(mount.Stops.Where(s => s.At >= parkAt && s.Kind != StopKind.LeaseStop), Is.Empty,
                    "no resend from the fenced exit. " + mount.Describe());
            } finally {
                mount.OnFreshRead = null;
                mount.HangReads = null;
                hang.TrySetResult();
            }
        }

        // #2066's Stop_while_opLock_held_aborts_within_1s: a Stop whose run is stuck in a call that ignores cancellation
        // sends its stop at once, not after the run's unwind grace, when the end confirms the stop itself.
        [Test]
        public async Task A_Stop_whose_run_is_stuck_reaches_the_mount_within_1s() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var solver = new StuckSolver();
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, TenDegreeStore());
            svc.RunUnwindGrace = TimeSpan.FromSeconds(3); // well past 1 s: a stop held back for the run lands after it
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                var at = mount.Now;
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

                var first = mount.Stops.FirstOrDefault(s => s.At >= at && s.Kind != StopKind.LeaseStop);
                Assert.That(first, Is.Not.Null, "the Stop reached the mount. " + mount.Describe());
                Assert.That(first!.At - at, Is.LessThan(TimeSpan.FromSeconds(1)), "the first stop went out within 1 s. " + mount.Describe());
            } finally {
                solver.Release();
            }
        }

        // stop_tracking_when_done off: Done hands back the tracking the run found (off here). A Done that abandons a run stuck
        // past its grace left the hand-back to the run's own exit, and then fenced that exit: nobody handed tracking back, and
        // the mount tracked on toward the pier. The end that fences the run does the hand-back itself.
        [Test]
        public async Task A_Done_that_abandons_a_stuck_run_hands_back_the_tracking_the_run_found() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0, tracking: false);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var solver = new StuckSolver();
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, HandBackStoreTenDegrees());
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(300);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                Assert.That(mount.Tracking, Is.True, "Polar Align turned tracking on for its frames");
                await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
                Assert.That(mount.Tracking, Is.False, "the Done handed back the tracking the run found. " + mount.Describe());
                solver.Release(); // the stuck run unwinds now; its fenced exit writes nothing
                await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                Assert.That(mount.Tracking, Is.False, "still handed back after the run unwound");
            } finally {
                solver.Release();
            }
        }

        private static InMemoryProfileStore HandBackStoreTenDegrees() {
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { StopTrackingWhenDone = false });
            return store;
        }

        // The Stop gave up on a run stuck past its grace and stopped the mount itself. The run unwinds whenever its call
        // returns: its exit then stopped the mount again and turned tracking off in the middle of whatever came next (here
        // the user's park). The end that abandoned it fences its stops.
        [Test]
        public async Task A_stuck_run_a_Stop_abandoned_sends_no_stop_when_it_unwinds_later() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.GoToRateDegPerSec = 15.0;
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var solver = new StuckSolver();
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, TenDegreeStore());
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(300);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
                var parkAt = mount.Now;
                var park = mount.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                solver.Release(); // the stuck run unwinds now, in the middle of the park
                var parked = await park.WaitAsync(SignalTimeout).ConfigureAwait(false);

                Assert.That(parked, Is.True, "the park arrived. " + mount.Describe());
                Assert.That(mount.Stops.Where(s => s.At >= parkAt && s.Kind != StopKind.LeaseStop), Is.Empty,
                    "no stop from the abandoned run. " + mount.Describe());
                Assert.That(mount.Commands.Where(c => c.At >= parkAt).Select(c => c.Text), Has.None.StartsWith("tracking"),
                    "no tracking write from the abandoned run");
            } finally {
                solver.Release();
            }
        }
    }
}
