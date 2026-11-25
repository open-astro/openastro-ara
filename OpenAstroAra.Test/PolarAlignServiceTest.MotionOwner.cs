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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 (ARA-2), #2065 (ARA-7) — one motion owner. Polar Align holds the mount's motion owner for its whole run, the
    /// adjust loop included (Joey, 2026-11-20 on #2048), so a sequencer, Center or REST GoTo cannot run beside its turns;
    /// and before each turn it checks, on two fresh readings, that nothing else moves the mount and the guider does not
    /// guide (<c>mount_busy</c>). Every scenario runs on the fake GEM mount and ends with #2048's common asserts.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>The fake's true sky as the solve (<see cref="TrueSkySolver"/>), with <see cref="BeforeSolve"/> run first
        /// with the solve's number (1 = seed-a; 2 = seed-b, after the first turn and before the second).</summary>
        private sealed class HookedTrueSkySolver(FakeGemMount mount) : IPolarAlignFrameSolver {
            private int _calls;
            public Action<int>? BeforeSolve { get; set; }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                BeforeSolve?.Invoke(Interlocked.Increment(ref _calls));
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        /// <summary>A solve that never finds the sky: the seed fails by itself (<c>seed_solve_failed</c>).</summary>
        private sealed class NeverSolves : IPolarAlignFrameSolver {
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) =>
                Task.FromResult(new PolarAlignSolveOutcome(false, 0, 0));
        }

        /// <summary>The routine on the fake with bench motors and a guider the test holds.</summary>
        private static PolarAlignService NewGemServiceWith(FakeGemMount mount, WsRecorder ws, IPolarAlignFrameSolver solver, FakeGuideCapture? guider = null) {
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(guider ?? NewFake(), solver, mount, ws, TenDegreeStore());
            svc.AxisFastRateDegPerSec = 30.0;
            svc.AxisSlowRateDegPerSec = 10.0;
            return svc;
        }

        /// <summary>The run's <c>polar_align.error</c>: its reason and message.</summary>
        private static async Task<(string? Reason, string? Message)> RunErrorAsync(WsRecorder ws) {
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            return (error.GetProperty("reason").GetString(), error.GetProperty("message").GetString());
        }

        private static void AssertFailedBusy((string? Reason, string? Message) error, string because) =>
            Assert.That(error.Reason, Is.EqualTo(PolarAlignService.MountBusy), because);

        // ── the owner for the whole run ──────────────────────────────────────────────────────

        [Test]
        public async Task Polar_Align_holds_the_motion_owner_for_the_whole_run_and_a_GoTo_meanwhile_is_refused() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            Assert.That(mount.MotionOwner, Is.Null, "nothing holds the mount before the start");

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);

            // Adjusting: no motor runs (IsMovingMount is false), yet the run still holds the mount (Joey's decision).
            Assert.That(svc.IsMovingMount, Is.False, "the adjust loop turns nothing");
            Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "held through the adjust loop");
            var (ra, dec) = mount.TrueSky;
            var goTo = new Coordinates(Angle.ByDegree(ra + 5.0), Angle.ByDegree(dec), Epoch.JNOW);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => mount.SlewToCoordinatesAsync(goTo, CancellationToken.None),
                "a sequencer or Center GoTo is refused while Polar Align holds the mount").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(mount.MotionOwner, Is.Null, "given back once the run ended");
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.GoTo), Is.Empty, "the GoTo never reached the mount. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task Polar_Align_reads_its_start_pose_only_once_it_holds_the_mount() {
            // The run's turns and its 30° sweep limit start from the start reading. Read before the run held the mount, a
            // GoTo sent between that reading and the hold would move the mount away from it unseen.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            var owners = new System.Collections.Concurrent.ConcurrentQueue<string>();
            mount.OnFullRead = () => owners.Enqueue(mount.MotionOwner ?? "nobody");

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(owners, Is.Not.Empty, "the start read the mount");
            Assert.That(owners.First(), Is.EqualTo(MotionOwners.PolarAlign), "the start reading came after the hold");
            mount.AssertEndedStopped(tracking: false);
        }

        [Test]
        public async Task A_GoTo_accepted_mid_solve_does_not_run_beside_the_next_turn() {
            // A sequencer or Center GoTo that comes while the run solves seed-b (between its two turns): the mount is the
            // run's, so the GoTo is refused and the run's second turn goes out on a still mount.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new HookedTrueSkySolver(mount);
            Exception? refused = null;
            solver.BeforeSolve = n => {
                if (n == 2) {
                    var (ra, dec) = mount.TrueSky;
                    var goTo = new Coordinates(Angle.ByDegree(ra + 3.0), Angle.ByDegree(dec), Epoch.JNOW);
                    // A refusal faults the call at once; a GoTo the mount took would still be running (not awaited here).
                    var call = mount.SlewToCoordinatesAsync(goTo, CancellationToken.None);
                    refused = call.IsFaulted ? call.Exception!.InnerException : null;
                }
            };
            using var svc = NewGemServiceWith(mount, ws, solver);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(refused, Is.InstanceOf<SequenceEntityFailedException>(), "the GoTo mid-solve was refused");
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.GoTo), Is.Empty, mount.Describe());
            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), "both turns ran, on a still mount. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_run_that_fails_by_itself_gives_the_motion_owner_back() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemServiceWith(mount, ws, new NeverSolves());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);
            // The failure is published before the exit stops the mount; the owner goes back once the exit is done.
            var deadline = DateTime.UtcNow + SignalTimeout;
            while (mount.MotionOwner is not null && DateTime.UtcNow < deadline) {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.That(error.Reason, Is.EqualTo("seed_solve_failed"));
            Assert.That(svc.IsActive, Is.False);
            Assert.That(mount.MotionOwner, Is.Null, "no Stop came: the run's own exit gave the mount back");
        }

        [Test]
        public async Task A_Stop_of_a_stuck_run_gives_the_motion_owner_back_when_it_answers() {
            // A run stuck in a call that ignores cancellation never reaches its own exit, which gives the owner back: the
            // Stop does, once it has stopped the mount, or GoTo, Center, Park and Home stay refused for as long as it hangs.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new StuckSolver();
            using var svc = NewGemServiceWith(mount, ws, solver);
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(200);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign));
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

                Assert.That(mount.MotionOwner, Is.Null, "given back while the run is still stuck");
            } finally {
                solver.Release();
            }
        }

        [Test]
        public async Task A_shutdown_whose_Polar_Align_takeover_times_out_stops_tracking_since_the_held_mount_refuses_its_park() {
            // End Night's (and the unattended shutdown's) ladder ends Polar Align through its takeover before its Find Home
            // and park. A run stuck in a call that ignores cancellation outlasts the takeover's bound: the run is fenced but
            // still holds the mount, so the mediator refuses the Find Home and the park, and the park step falls back to
            // tracking off: an unattended mount must never be left tracking.
            var mount = PolledGemAt(-4.0);
            mount.MediatorRefusesHomeAndParkWhileOwned = true;
            var ws = new WsRecorder();
            var solver = new StuckSolver();
            using var svc = NewGemServiceWith(mount, ws, solver);
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(200); // the daemon's 9 s
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var ladder = new ShutdownLadder(telescope: mount, occupancy: new RigOccupancy().RegisterPolarAlign(() => svc));

            try {
                Assert.That(mount.Tracking, Is.True, "the mount tracks before the shutdown");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var home = await ladder.FindHomeAsync().ConfigureAwait(false);
                var rest = await ladder.RestMountAsync().ConfigureAwait(false);
                var took = watch.Elapsed;

                Assert.Multiple(() => {
                    Assert.That(took, Is.LessThan(TimeSpan.FromSeconds(10)), "each takeover answered at Polar Align's bound, not the ladder's 60 s");
                    Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the fenced run still holds the mount");
                    Assert.That(home.Outcome, Is.EqualTo(LadderOutcome.Failed), home.Text);
                    Assert.That(rest.Rest, Is.EqualTo(MountRest.TrackingOff), rest.Step.Text);
                    Assert.That(mount.Tracking, Is.False, "tracking stopped instead of the park");
                    Assert.That(mount.Motions.Where(m => m.Kind is MotionKind.FindHome or MotionKind.Park), Is.Empty,
                        "no Find Home or park reached the mount. " + mount.Describe());
                });
            } finally {
                solver.Release();
            }
        }

        // #2142 — the register confirms polar alignment's stop by IsActive. A run stuck in a call that ignores cancellation
        // is fenced at the takeover's bound (#2066): that stop took effect, so Emergency Stop does not name it as failed.
        [Test]
        public async Task A_fenced_stuck_run_counts_as_stopped_by_the_register() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new StuckSolver();
            using var svc = NewGemServiceWith(mount, ws, solver);
            svc.TakeoverStopBound = TimeSpan.FromMilliseconds(200);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var occupancy = new RigOccupancy().RegisterPolarAlign(() => svc);

            try {
                var stops = await occupancy.StopAsync(RigHolding.Any, MountStopReasons.EmergencyStop, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                Assert.That(stops.Single().Stopped, Is.True, "IsActive after the fenced takeover: " + svc.IsActive);
            } finally {
                solver.Release();
            }
        }

        [Test]
        public async Task A_Start_that_drains_a_run_that_never_unwound_gives_that_runs_motion_owner_back() {
            // A run failed by itself, stopped the mount and reported it, but its task never unwound (here its session row
            // hangs). A new Start waits for it only so long; a Start that is then refused before it takes the mount must
            // not leave the old run's hold behind, or GoTo, Center, Park and Home stay refused until that task ends.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var log = new HangingSessionLog();
            using var svc = NewServiceOn(NewFake(), new NeverSolves(), mount, ws, TenDegreeStore(), log: log);
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(50);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);
            await log.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(error.Reason, Is.EqualTo("seed_solve_failed"));
            Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the stuck run still holds the mount");

            mount.PositionUnverifiedReason = MountPoseTrust.NotReadYet;
            try {
                await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                    new PolarAlignStartConfirmation(PolarAlignDirections.East, false), null, CancellationToken.None)).ConfigureAwait(false);

                Assert.That(mount.MotionOwner, Is.Null, "the Start gave the old run's hold back");
            } finally {
                log.Release();
            }
        }

        /// <summary>A session log whose write hangs until <see cref="Release"/>: the run's task cannot unwind.</summary>
        private sealed class HangingSessionLog : IPolarAlignmentLog {
            private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
                Entered.TrySetResult();
                return _gate.Task;
            }
            public Task<PolarAlignmentRecord?> GetLatestMeasuredAsync(DateTimeOffset since, CancellationToken ct) =>
                Task.FromResult<PolarAlignmentRecord?>(null);
            public void Release() => _gate.TrySetResult();
        }

        [Test]
        public async Task The_two_readings_before_a_turn_are_MountSteadyFor_apart_one_second_on_the_daemon() {
            // Read a few ms apart, a nudge or a slow move by other means between them would almost never show.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var armed = 0;
            var readAt = new System.Collections.Concurrent.ConcurrentQueue<long>();
            mount.OnFullRead = () => {
                if (Volatile.Read(ref armed) > 0 && Interlocked.Decrement(ref armed) >= 0) {
                    readAt.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
                }
            };
            var solver = new HookedTrueSkySolver(mount) {
                BeforeSolve = n => {
                    if (n == 2) {
                        Volatile.Write(ref armed, 2); // the next two full readings are the second turn's pair
                    }
                },
            };
            using (var daemon = new PolarAlignService(NewFake(), NullLogger<PolarAlignService>.Instance, solver, mount, MediatorMotionControl.For(mount), NewStore())) {
                Assert.That(daemon.MountSteadyFor, Is.EqualTo(TimeSpan.FromSeconds(1)), "the daemon's spacing");
            }
            using var svc = NewGemServiceWith(mount, ws, solver);
            svc.MountSteadyFor = TimeSpan.FromMilliseconds(300);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var pair = readAt.ToArray();
            Assert.That(pair, Has.Length.EqualTo(2), "the second turn's two readings");
            Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(pair[0], pair[1]), Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(290)));
            mount.AssertEndedStopped(tracking: false);
        }

        // ── mount_busy before each turn ──────────────────────────────────────────────────────

        [Test]
        public async Task M21_a_mount_already_slewing_at_a_turn_fails_the_run_with_mount_busy() {
            // A GoTo the daemon never saw (the hand controller, another program on the driver) starts while the run solves
            // seed-b: the second turn must not go out beside it.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new HookedTrueSkySolver(mount);
            var started = false;
            solver.BeforeSolve = n => {
                if (n == 2) {
                    var (ra, dec) = mount.TrueSky;
                    started = mount.GoToByOtherMeans(ra + 3.0, dec);
                }
            };
            using var svc = NewGemServiceWith(mount, ws, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);

            Assert.That(started, Is.True, "the other GoTo started mid-solve");
            AssertFailedBusy(error, "the mount slewed when the second turn was due");
            Assert.That(error.Message, Does.Contain("slewing"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task M10_a_failed_Slewing_read_before_a_turn_fails_the_run_with_mount_busy() {
            // The mount's Slewing answers an error (HTTP 500 on AlpacaBridge): whether it moves is not known, so nothing
            // more moves.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new HookedTrueSkySolver(mount) {
                BeforeSolve = n => {
                    if (n == 2) {
                        mount.SlewingReadFails = true;
                    }
                },
            };
            using var svc = NewGemServiceWith(mount, ws, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);

            AssertFailedBusy(error, "a Slewing read that failed is not 'not slewing'");
            Assert.That(error.Message, Does.Contain("does not say whether it is slewing"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_mount_moved_between_the_two_readings_before_a_turn_fails_the_run_with_mount_busy() {
            // The hand pad nudges the mount between the two readings: both say "not slewing", but the mount moved.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var armed = 0;
            mount.OnFullRead = () => {
                if (armed > 0 && Interlocked.Decrement(ref armed) == 0) {
                    var pose = mount.TruePose;
                    mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + 0.5 });
                }
            };
            var solver = new HookedTrueSkySolver(mount) {
                BeforeSolve = n => {
                    if (n == 2) {
                        Volatile.Write(ref armed, 2); // the move lands before the second of the turn's two readings
                    }
                },
            };
            using var svc = NewGemServiceWith(mount, ws, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);

            AssertFailedBusy(error, "the mount moved by other means when the second turn was due");
            Assert.That(error.Message, Does.Contain("moved"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [TestCase("Guiding")]
        [TestCase("Calibrating")]
        public async Task A_guider_that_starts_guiding_mid_run_fails_the_next_turn_with_mount_busy(string state) {
            // Guiding started by the guider's own controls (not through Ara, which would take the camera and end the run):
            // the guider would chase the turn.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var guider = NewFake();
            var solver = new HookedTrueSkySolver(mount) {
                BeforeSolve = n => {
                    if (n == 2) {
                        guider.AppState = state;
                    }
                },
            };
            using var svc = NewGemServiceWith(mount, ws, solver, guider);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);

            AssertFailedBusy(error, "the guider was " + state + " when the second turn was due");
            // The sentence's fixed part says "stop guiding" whatever the state: the reason must name the state itself.
            Assert.That(error.Message, Does.Contain("the guider is " + state).IgnoreCase);
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "no second turn. " + mount.Describe());
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_start_on_a_slewing_mount_is_refused_with_mount_busy_and_nothing_moves() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var guider = NewFake();
            using var svc = NewGemServiceWith(mount, ws, new HookedTrueSkySolver(mount), guider);
            var (ra, dec) = mount.TrueSky;
            Assert.That(mount.GoToByOtherMeans(ra + 3.0, dec), Is.True);

            var ex = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountBusy));
            Assert.That(RaTurns(mount), Is.Empty, "nothing turned. " + mount.Describe());
            Assert.That(guider.LeaseCalls, Is.Empty, "refused before the guide camera was taken");
            Assert.That(mount.MotionOwner, Is.Null);
        }
    }
}
