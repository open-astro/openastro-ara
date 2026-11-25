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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 / #2067 — any stop Polar Align did not raise ends its run: Stop Mount, Emergency Stop (whose first step latches
    /// the stop before any rung), a park or a Find Home. The run reads the mount's stop count once, at its Start, and checks
    /// it before every motor move and tracking switch-on, after every seed frame, and between the stages, so a stop between
    /// two turns (during a solve) starts no further turn. Emergency Stop and the safety park take the mount over: the run
    /// leaves the stop and its confirmation to them.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A solver whose solve lands where the bench mount points, and that runs <see cref="BeforeSolve"/>
        /// with the solve's number (1 = the first) before it answers.</summary>
        private sealed class HookedSolver : IPolarAlignFrameSolver, IMountSky {
            private int _calls;
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
            public Action<int>? BeforeSolve { get; set; }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                BeforeSolve?.Invoke(Interlocked.Increment(ref _calls));
                return Task.FromResult(Sky?.Invoke() is { } sky ? new PolarAlignSolveOutcome(true, sky.RaDeg, sky.DecDeg) : SolveB);
            }
        }

        // The hole the run-wide watch closes: the epoch was read afresh at each move, so a Stop Mount tapped while the run
        // solved seed-b was read as the baseline of the next turn, and that turn started the motor after the stop.
        [Test]
        public async Task Stop_Mount_during_the_seed_solve_starts_no_further_turn() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var stoppedAt = -1;
            var solver = new HookedSolver {
                BeforeSolve = n => {
                    if (n == 2) { // seed-b, after the first turn
                        stoppedAt = mount.Count;
                        mount.StopMount();
                    }
                },
            };
            using var svc = NewService(NewFake(), solver, mount.Mock, ws, store: NewStore());

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(stoppedAt, Is.GreaterThan(0), "the run reached the seed-b solve");
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Stop Mount"));
            AssertNoMotionAfter(mount, stoppedAt);
            Assert.That(mount.Since(stoppedAt), Does.Not.Contain("tracking on"));
            Assert.That(mount.Tracking, Is.False, "the run's exit leaves tracking off");
        }

        // A stage switches tracking on before its turns. A Stop Mount that lands after the check between the stages, while
        // the stage still waits on the guider (its frame size), must leave tracking off: the switch-on checks the run's
        // reading itself, so no tracking write and no turn follow the stop.
        [Test]
        public async Task Stop_Mount_just_before_a_stages_tracking_switch_on_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: false);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var stoppedAt = -1;
            var guider = NewFake();
            guider.OnFrameSizeRead = () => {
                if (Interlocked.CompareExchange(ref stoppedAt, mount.Count, -1) == -1) {
                    mount.StopMount();
                }
            };
            using var svc = NewService(guider, new HookedSolver(), mount.Mock, ws, store: NewStore());

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(Volatile.Read(ref stoppedAt), Is.GreaterThanOrEqualTo(0), "the stop landed while the stage read the frame size");
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(mount.Log, Does.Not.Contain("tracking on"), string.Join(", ", mount.Log));
            AssertNoMotionAfter(mount, stoppedAt);
            Assert.That(mount.Tracking, Is.False);
        }

        private static readonly string[] OnlyTheCheckStarted = { PolarAlignStages.Check };

        // Emergency Stop latches the stop before its first rung. While a slow rung (here the sequence-run abort) still holds
        // the ladder, the user's Check ends the adjust loop and starts the check (#2082): it must not turn tracking on or
        // turn the mount. The loop is parked in single-frame mode, so it has not seen the latch: the check keeps the loop's
        // reading of the stop count, taken at its Start, and fails before it moves.
        [Test]
        public async Task Emergency_Stop_during_PA_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { LoopMode = PolarAlignLoopModes.SingleFrame });
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: store);
            svc.CaptureRequestPoll = TimeSpan.FromMilliseconds(10);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var deadline = DateTime.UtcNow + SignalTimeout;
            while (!(await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).WaitingForCapture) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the adjust loop parks for a frame request");
                await Task.Delay(10).ConfigureAwait(false);
            }
            Assert.That(mount.Tracking, Is.False, "the adjust loop runs with tracking off");

            var runAbortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runAbortGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).Returns(() => {
                runAbortEntered.TrySetResult();
                return runAbortGate.Task;
            });
            var es = new EmergencyStopService(sequencerResolver: () => sequencer.Object,
                telescope: (ITelescopeService)mount.Mock.Object, polarAlign: svc);

            var at = mount.Count;
            var stop = es.ExecuteAsync();
            await runAbortEntered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var mark = ws.Events.Count;
            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            var started = ws.Events.Skip(mark).Where(e => e.Type == WsEventCatalog.PolarAlignStarted).ToArray();
            Assert.That(started.Select(e => e.Payload.GetProperty("stage").GetString()), Is.EqualTo(OnlyTheCheckStarted),
                "the Check got past its plan, ended the loop and started");
            runAbortGate.SetResult(0);
            var result = await stop.WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(mount.Since(at), Does.Not.Contain("tracking on"), string.Join(", ", mount.Since(at)));
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.Tracking, Is.False);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Emergency Stop"));
            Assert.That(result.FailedRungs, Does.Not.Contain("stop_polar_align"));
        }

        // Emergency Stop tells the run to leave the mount to it: a mount that ignores its stop is not confirmed (and resent)
        // for 8 s inside Emergency Stop's 10 s rung. The ladder stops and checks the mount itself.
        [Test]
        public async Task A_takeover_ends_the_run_without_waiting_for_the_stops_confirmation() {
            var mount = new FakeStopMount(tracking: true) { IgnoresStops = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(5);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            var watch = Stopwatch.StartNew();
            await svc.StopForTakeoverAsync("emergency_stop", CancellationToken.None).ConfigureAwait(false);

            Assert.That(watch.Elapsed, Is.LessThan(svc.StopVerifyWindow), "no confirmation window");
            Assert.That(mount.Since(at).Count(c => c == "abort polar_align"), Is.EqualTo(1), "the first round only, never resent");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no mount_did_not_stop: the ladder checks the mount");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        // A park from outside while the user adjusts ends the run, and the run's exit sends no stop of its own: an AbortSlew
        // or MoveAxis 0 from it would abort the park, and a tracking write could make the mount refuse it.
        [Test]
        public async Task A_park_from_outside_ends_the_run_and_its_exit_leaves_the_mount_to_the_park() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            mount.ParkFromOutside();
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("parked"));
            Assert.That(mount.Since(at + 1), Is.Empty, $"nothing after the park: {string.Join(", ", mount.Since(at))}");
        }

        // A takeover ends a turn without the routine's own confirmed stop. The motion it was making is the sender's now: a
        // new run must not inherit it, or the telescope endpoints would refuse Park and GoTo while that run only solves.
        [Test]
        public async Task IsMovingMount_reads_false_in_a_new_run_after_a_takeover_ended_a_turn() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var secondRun = 0;
            var movingDuringSolve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PolarAlignService? service = null;
            var solver = new HookedSolver {
                BeforeSolve = _ => {
                    if (Volatile.Read(ref secondRun) == 1 && service is { } s) {
                        movingDuringSolve.TrySetResult(s.IsMovingMount);
                    }
                },
            };
            using var svc = NewService(NewFake(), solver, mount.Mock, ws, store: NewStore());
            service = svc;
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);
            await svc.StopForTakeoverAsync("emergency_stop", CancellationToken.None).ConfigureAwait(false);
            Assert.That(svc.IsMovingMount, Is.False, "no run");

            Volatile.Write(ref secondRun, 1);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(await movingDuringSolve.Task.WaitAsync(SignalTimeout).ConfigureAwait(false), Is.False,
                "the new run's first solve: nothing turns");
        }

        // Each stop from outside is named, and only the senders that own the mount afterwards take it over: Emergency Stop
        // checks it itself, a park and a Find Home move it on purpose (an AbortSlew from the exit would abort them).
        [TestCase("user_request", "Stop Mount", false)]
        [TestCase("watchdog", "watchdog", false)]
        [TestCase("emergency_stop", "Emergency Stop", true)]
        [TestCase("park", "parked", true)]
        [TestCase("findhome", "sent Home", true)]
        [TestCase("pad_stop", "direction pad", false)]
        [TestCase(null, "Stop Mount", false)]
        public void Each_stop_from_outside_is_named_and_only_its_owners_take_the_mount_over(string? by, string named, bool takesOver) {
            Assert.That(PolarAlignService.StoppedFromOutsideMessage(by), Does.Contain(named));
            Assert.That(PolarAlignService.TakesTheMountOver(by), Is.EqualTo(takesOver));
        }

        // A Start pressed right after a failed turn, while the old run's exit still resends its AbortSlew: those sends are
        // Polar Align's own, so the new run must not start under a count they make stale (the telescope service would then
        // refuse its first motor command).
        [Test]
        public async Task A_Start_while_the_last_runs_exit_resends_its_stop_still_turns_the_mount() {
            var mount = new FakeStopMount(tracking: true) { RefusesMotor = true };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HookedSolver(), mount.Mock, ws, store: NewStore());
            svc.StopResendAfter = TimeSpan.FromMilliseconds(300); // the resends land after the next Start was called
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(1.5);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(10);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while ((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State != "failed") {
                Assert.That(watch.Elapsed, Is.LessThan(SignalTimeout), "the refused move never failed the run");
                await Task.Delay(5).ConfigureAwait(false);
            }
            mount.RefusesMotor = false;

            var at = mount.Count;
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var mark = Mark(svc); // the Start waited for the old run, whose error event is out by now
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(mount.Since(at).Count(c => c == "abort polar_align"), Is.GreaterThanOrEqualTo(2), "the old exit resent its stop meanwhile");
            Assert.That(mount.Since(at).Where(c => c.EndsWith(" refused", StringComparison.Ordinal)), Is.Empty, string.Join(", ", mount.Since(at)));
            Assert.That(status.State, Is.EqualTo("adjusting"),
                string.Join(" | ", ws.Events.Skip(mark).Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.ToString())));
        }

        // The telescope endpoints refuse a GoTo, a pad move, Home and Park only while a turn runs (or its stop is not
        // confirmed yet), not while the run exposes, solves or the user adjusts.
        [Test]
        public async Task IsMovingMount_reads_true_only_while_a_turn_runs() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            Assert.That(svc.IsMovingMount, Is.False, "no run");

            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);
            Assert.That(svc.IsMovingMount, Is.True, "mid-turn");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(svc.IsMovingMount, Is.False, "stopped and confirmed");
        }

        /// <summary>A solver that runs <paramref name="stop"/> on the first live frame (the fourth solve, after seeds A, B
        /// and C), before it answers.</summary>
        private sealed class StopOnFirstLiveSolve(IPolarAlignFrameSolver inner, Action stop) : IPolarAlignFrameSolver {
            private int _solves;
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                if (Interlocked.Increment(ref _solves) == 4) {
                    stop();
                }
                return inner.SolveAsync(fitsPath, binning, hintRa, hintDec, ct);
            }
        }

        // The Polar Aligner moves the mount's polar axis. A Stop Mount tapped while a live frame exposes or solves lands after
        // the loop's check at the top of the frame: the measurement must not be answered with a Polar Aligner move.
        [Test]
        public async Task Stop_Mount_during_a_live_frame_sends_the_Polar_Aligner_no_move() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var stopped = 0;
            var solver = new StopOnFirstLiveSolve(new CorrectedSky(aligner), () => {
                Interlocked.Exchange(ref stopped, 1);
                mount.StopMount();
            });
            using var svc = NewService(NewFake(), solver, mount.Mock, ws, store: NewStore(), aligner: aligner);
            svc.AlignerPoll = TimeSpan.FromMilliseconds(5);

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(Volatile.Read(ref stopped), Is.EqualTo(1), "the run reached its first live frame");
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(aligner.Moves, Is.Empty, "no Polar Aligner move after the stop");
        }
    }
}
