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
    /// One motion owner. Polar Align holds the mount's motion owner for its whole run, the adjust loop included, so a
    /// sequencer, Center or REST GoTo cannot run beside its turns; a start while another routine holds the mount is
    /// refused (<c>polar-align-mount-busy</c>). Every scenario runs on the fake GEM mount and ends with #2048's common
    /// asserts.
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
            svc.AxisRateDegPerSec = 30.0;
            return svc;
        }

        /// <summary>The run's <c>polar_align.error</c>: its reason and message.</summary>
        private static async Task<(string? Reason, string? Message)> RunErrorAsync(WsRecorder ws) {
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            return (error.GetProperty("reason").GetString(), error.GetProperty("message").GetString());
        }

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            mount.CanFindHome = true; // the ladder's Find Home step is for a mount that can
            var ladder = new ShutdownLadder(telescope: mount, polarAlign: () => svc);

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
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await RunErrorAsync(ws).ConfigureAwait(false);
            await log.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(error.Reason, Is.EqualTo("seed_solve_failed"));
            Assert.That(mount.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the stuck run still holds the mount");

            mount.PositionUnverifiedReason = MountPoseTrust.NotReadYet;
            try {
                await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);

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

        // ── another owner ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_start_while_another_routine_holds_the_mount_is_refused_as_mount_busy_and_nothing_moves() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var guider = NewFake();
            using var svc = NewGemServiceWith(mount, ws, new TrueSkySolver(mount), guider);
            var other = mount.AcquireMotion("another-routine");

            var ex = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountBusy));
            Assert.That(RaTurns(mount), Is.Empty, "nothing turned. " + mount.Describe());
            Assert.That(guider.LeaseCalls, Is.Empty, "refused before the guide camera was taken");
            Assert.That(mount.MotionOwner, Is.EqualTo("another-routine"), "the other routine still holds the mount");
            // Once the other routine gives the mount back, the start runs.
            other.Dispose();
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            mount.AssertEndedStopped(tracking: false);
        }
    }
}
