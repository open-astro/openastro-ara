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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// What a run does by itself after Done: it stops the mount, sends it Home (<c>homing</c>), measures again
    /// (<c>checking</c>), sends it Home again (<c>homing</c>) and ends <c>checked</c>. A Stop works at every moment of it;
    /// a Find Home that fails ends the run <c>failed</c> with <c>home_failed</c>; a mount that cannot find Home turns back
    /// the way it came instead. Run on the fake GEM mount at Home, on a clock the routine's timed waits move, with a
    /// camera half a degree off the RA axis (on the axis every frame would show the same field).
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A whole run: Start, the adjust loop, Done, and the run's end. The status at <c>checked</c>, or at
        /// <c>failed</c> wherever the run failed.</summary>
        private static async Task<PolarAlignStateDto> RunThroughDoneAsync(PolarAlignService svc) {
            var mark = Mark(svc);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);
            if (status.State == "failed") {
                return status;
            }
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            // Only an error or the verdict after Done ends the wait: the adjust loop's own events do not.
            return await PollStateAsync(svc, Mark(svc), "checked", "failed").ConfigureAwait(false);
        }

        private static bool IsStage((string Type, JsonElement Payload) e, string state) =>
            e.Type == WsEventCatalog.PolarAlignStage && e.Payload.GetProperty("state").GetString() == state;

        /// <summary>Waits for the <c>polar_align.stage</c> event that announces <paramref name="state"/> (after the first
        /// <paramref name="skip"/> events); its payload.</summary>
        private static async Task<JsonElement> WaitForChainStateAsync(WsRecorder ws, string state, int skip = 0) {
            await ws.WhenAsync(events => events.Skip(skip).Any(e => IsStage(e, state))).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return ws.Events.Skip(skip).First(e => IsStage(e, state)).Payload;
        }

        /// <summary>A plate solve from a camera <paramref name="coneDeg"/> off the RA axis of the fake: the tube's RA, that
        /// far from where the tube points (the pole, from Home).</summary>
        private sealed class OffAxisCameraSolver(FakeGemMount mount, double coneDeg = 0.5) : IPolarAlignFrameSolver {
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec - coneDeg));
            }
        }

        /// <summary>A mount at Home (counterweight down, the tube on the pole) that tracks and finds Home by itself.</summary>
        private static FakeGemMount GemAtHome(ManualMountClock? clock = null) =>
            new(GemGeometry.Home(south: false), lstHours: 20.0, clock: clock ?? new ManualMountClock { AutoAdvance = true });

        /// <summary>The routine on the fake at Home, at the daemon's own 3°/s with NINA's 10° steps.</summary>
        private static PolarAlignService NewHomeService(FakeGemMount mount, WsRecorder ws, IPolarAlignmentLog? log = null) {
            mount.EventCount = () => ws.Events.Count;
            return NewServiceOn(NewFake(), new OffAxisCameraSolver(mount), mount, ws, TenDegreeStore(), log: log);
        }

        /// <summary>Turns the clock's own advance off when the run announces <paramref name="moment"/> of the chain:
        /// <c>first home</c>, <c>verify</c> or <c>last home</c>. The run then stands still at its next timed wait (the
        /// mount on its way Home, or in the first verify turn): the clock's <see cref="ManualMountClock.TimerSet"/>, taken
        /// before Done, completes there.</summary>
        private static void HoldAfterDoneAt(string moment, PolarAlignService svc, WsRecorder ws, ManualMountClock clock) =>
            ws.OnPublish = type => {
                if (type != WsEventCatalog.PolarAlignStage) {
                    return;
                }
                var status = svc.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                var here = moment switch {
                    "first home" => status.State == "homing" && status.CheckResult is null,
                    "verify" => status.State == "checking",
                    _ => status.State == "homing" && status.CheckResult is not null,
                };
                if (here) {
                    clock.AutoAdvance = false;
                }
            };

        private static readonly string[] HomingCheckingHoming = { "homing", "checking", "checking", "checking", "checking", "homing" };
        private static readonly string[] CheckingOnly = { "checking", "checking", "checking", "checking" };
        private static readonly int[] SolvesCountedUp = { 0, 0, 1, 2, 3, 3 };
        private static readonly string[] CompleteThenChecked = { "complete", "checked" };
        private static readonly string[] CompleteThenFailed = { "complete", "failed" };
        private static readonly double[] TwoTurnsEastThenBack = { -10.0, -10.0, 10.0, 10.0 };
        private static readonly MotionKind[] TurnsHomeTurnsHome = {
            MotionKind.RaMotor, MotionKind.RaMotor, MotionKind.FindHome, MotionKind.RaMotor, MotionKind.RaMotor, MotionKind.FindHome,
        };

        // ── the chain ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Done_stops_the_mount_sends_it_Home_verifies_and_returns_Home_with_the_events_in_order() {
            var mount = GemAtHome();
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewHomeService(mount, ws, log);
            // The status as each stage event went out.
            var seen = new List<(string State, string? Stage, PolarAlignCheckResultDto? Verdict)>();
            ws.OnPublish = type => {
                if (type == WsEventCatalog.PolarAlignStage) {
                    var s = svc.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                    lock (seen) {
                        seen.Add((s.State, s.Stage, s.CheckResult));
                    }
                }
            };
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var adjusted = (await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).CurrentErrorArcmin!.Value;
            var mark = ws.Events.Count;
            var commandsAtDone = mount.Commands.Count;

            var accepted = await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(accepted.OperationType, Is.EqualTo("polar-align.complete"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero);
            Assert.That((status.State, status.Stage, status.CheckSolvesDone), Is.EqualTo(("checked", PolarAlignStages.Verify, 3)));
            // The events after Done, in order: homing, checking (and its three solves), homing, then the terminal checked.
            var after = ws.Events.Skip(mark).ToArray();
            var stages = after.Where(e => e.Type == WsEventCatalog.PolarAlignStage).Select(e => e.Payload).ToArray();
            Assert.That(stages.Select(p => p.GetProperty("state").GetString()), Is.EqualTo(HomingCheckingHoming));
            Assert.That(stages.Select(p => p.GetProperty("stage").GetString()), Has.All.EqualTo(PolarAlignStages.Verify));
            Assert.That(stages.Select(p => p.GetProperty("check_solves_done").GetInt32()), Is.EqualTo(SolvesCountedUp));
            Assert.That(after.Last().Type, Is.EqualTo((string)WsEventCatalog.PolarAlignChecked), "checked is the run's last event");
            Assert.That(after.Count(e => e.Type == WsEventCatalog.PolarAlignChecked), Is.EqualTo(1));
            // The verdict is on the last homing's event and on the status then, while the mount is still on its way Home.
            Assert.That(stages.Take(5).Select(p => p.TryGetProperty("check_result", out _)), Has.All.False, "no verdict before the measurement ended");
            var verdict = stages[5].GetProperty("check_result");
            Assert.That(verdict.GetProperty("total_error_arcmin").GetDouble(), Is.EqualTo(status.CheckResult!.TotalErrorArcmin));
            Assert.That(verdict.GetProperty("verdict").GetString(), Is.EqualTo(status.CheckResult.Verdict));
            (string State, string? Stage, PolarAlignCheckResultDto? Verdict)[] statuses;
            lock (seen) {
                statuses = seen.Skip(seen.Count - 6).ToArray();
            }
            Assert.That(statuses.Take(5).Select(s => s.Verdict), Has.All.Null);
            Assert.That((statuses[5].State, statuses[5].Verdict), Is.EqualTo(("homing", status.CheckResult)), "check_result during the last homing");
            // The mount: stopped first, then Home, the two verify turns the measurement's own way, and Home again.
            var commands = mount.Commands.Skip(commandsAtDone).Select(c => c.Text).ToArray();
            Assert.That(Array.IndexOf(commands, "move secondary 0"), Is.InRange(0, Array.IndexOf(commands, "findhome") - 1),
                "the mount is stopped before it is sent Home: " + string.Join(", ", commands));
            Assert.That(mount.Motions.Select(m => m.Kind), Is.EqualTo(TurnsHomeTurnsHome), mount.Describe());
            Assert.That(RaTurns(mount).Select(t => t.TrueDeltaHaDeg), Has.All.EqualTo(-10.0).Within(TrueTurnToleranceDeg), mount.Describe());
            Assert.That(mount.Motions.Where(m => m.Kind == MotionKind.FindHome).Select(m => m.End), Has.All.EqualTo(MotionEnd.Arrived));
            Assert.That(mount.TruePose, Is.EqualTo(GemGeometry.Home(south: false)), "the run ends with the mount at Home");
            Assert.That(mount.GetInfo().AtHome, Is.True);
            mount.AssertEndedStopped(tracking: false);
            Assert.That(mount.MotionOwner, Is.Null, "the motion owner is given back");
            // The fake's axis is the true pole: both measurements read the refraction term.
            Assert.That(status.CheckResult.TotalErrorArcmin, Is.EqualTo(0.98).Within(0.2));
            Assert.That(status.CheckResult.FineErrorArcmin, Is.EqualTo(adjusted).Within(0.011), "compared with the adjust readout at Done");
            Assert.That(status.LastResult!.ErrorArcmin, Is.EqualTo(status.CheckResult.TotalErrorArcmin).Within(1e-6));
            Assert.That(log.Rows.Select(r => r.Outcome), Is.EqualTo(CompleteThenChecked));
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignChecked), CounterweightLimitDeg);
        }

        [Test]
        public async Task Polar_Aligns_own_Find_Home_is_not_refused_by_its_own_hold_and_is_not_read_as_a_stop_from_outside() {
            // The telescope service refuses a mediator Find Home while a routine holds the mount: the run sends its own as the
            // holder and never gives the hold back meanwhile.
            var mount = GemAtHome();
            mount.MediatorRefusesHomeAndParkWhileOwned = true;
            var ws = new WsRecorder();
            using var svc = NewHomeService(mount, ws);

            var status = await RunThroughDoneAsync(svc).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"), "neither Find Home was refused");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "and neither ended the run as a stop from outside");
            var commands = mount.Commands.Select(c => c.Text).ToArray();
            var homes = Enumerable.Range(0, commands.Length).Where(i => commands[i] == "findhome").ToArray();
            Assert.That(homes, Has.Length.EqualTo(2));
            Assert.That(commands.Take(homes[^1]).Count(c => c == "release motion"), Is.Zero, "the hold never lapses before or between the Find Homes");
            foreach (var home in homes) {
                Assert.That(commands[home - 1], Is.EqualTo("act as holder"), "each Find Home goes out as the holder's own move");
            }
        }

        // A Wi-Fi blip in the adjust loop (a new mount session) must not block the run's own moves after Done: the pose is
        // flagged at the run's end, not before its Home and verify turns.
        [Test]
        public async Task A_reconnect_during_adjust_still_verifies_after_Done_and_flags_the_pose_at_the_end() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            mount.Reconnect();
            var mark = ws.Events.Count;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"), mount.Describe());
            Assert.That(mount.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.StopUnconfirmed), "flagged once the run ended");
        }

        [Test]
        public async Task A_mount_that_cannot_find_Home_verifies_with_two_turns_back_and_no_Home() {
            var mount = PolledGemAt(-4.0); // cannot find Home
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var mark = ws.Events.Count;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(mount.Motions.Select(m => m.Kind), Has.All.EqualTo(MotionKind.RaMotor), "no Find Home, no GoTo. " + mount.Describe());
            Assert.That(RaTurns(mount).Select(t => t.TrueDeltaHaDeg), Is.EqualTo(TwoTurnsEastThenBack).Within(TrueTurnToleranceDeg),
                "two turns east to measure, two turns back to verify. " + mount.Describe());
            Assert.That(ws.Events.Skip(mark).Where(e => e.Type == WsEventCatalog.PolarAlignStage).Select(e => e.Payload.GetProperty("state").GetString()),
                Is.EqualTo(CheckingOnly), "never homing");
            Assert.That(status.CheckResult!.TotalErrorArcmin, Is.EqualTo(0.98).Within(0.2));
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignChecked), CounterweightLimitDeg);
        }

        [Test]
        public async Task Done_records_the_adjust_readout_as_a_complete_row_and_the_last_result_and_the_verdict_is_compared_with_it() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, NewMount(), ws, log: log);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            // The second progress event: the first live iteration is counted (the seed publishes iteration 0).
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var mark = ws.Events.Count;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await WaitForChainStateAsync(ws, "checking", mark).ConfigureAwait(false);

            // The row and the last result are written before the verify stage is announced.
            Assert.That(log.Rows.TryPeek(out var row), Is.True, "Done writes the adjust readout's row");
            Assert.That(row!.Outcome, Is.EqualTo("complete"));
            Assert.That(row.FinalErrorArcmin, Is.Not.Null.And.EqualTo(1.0).Within(0.2));
            Assert.That(row.Iterations, Is.GreaterThanOrEqualTo(1));
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.FineErrorArcmin, Is.EqualTo(row.FinalErrorArcmin!.Value).Within(0.011), "the verdict sits beside Done's readout");
            Assert.That(status.CheckResult.FineDeltaArcmin, Is.Not.Null.And.LessThan(0.1), "both measured the same axis");
            Assert.That(log.Rows.Select(r => r.Outcome), Is.EqualTo(CompleteThenChecked));
            Assert.That(PaCalls(fake).Last(), Is.False, "the run released the lease at its end");
        }

        // ── Done outside the adjust loop ─────────────────────────────────────────────────────

        [Test]
        public async Task Done_with_no_routine_active_and_Done_while_seeding_homing_or_checked_are_no_op_accepts() {
            // No routine: accepted, nothing happens.
            var idleWs = new WsRecorder();
            using (var idle = NewService(NewFake(), new ScriptedSolver(), ws: idleWs)) {
                var accepted = await idle.CompleteAsync("k1", CancellationToken.None).ConfigureAwait(false);
                Assert.That((accepted.OperationType, accepted.IdempotencyKey), Is.EqualTo(("polar-align.complete", "k1")));
                Assert.That((await idle.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
                Assert.That(idleWs.Events, Is.Empty);
            }
            // Seeding (parked on a capture that never completes): Done does not end the measurement, and is not kept for later.
            var seedingWs = new WsRecorder();
            using (var seeding = NewService(NewFake(answerCaptures: false), new ScriptedSolver(), ws: seedingWs)) {
                await seeding.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
                await seeding.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
                Assert.That((await seeding.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("seeding"));
                await seeding.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            // Homing: a second Done while the mount goes Home changes nothing, and the chain runs once.
            var clock = new ManualMountClock { AutoAdvance = true };
            var mount = GemAtHome(clock);
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewHomeService(mount, ws, log);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var onTheWayHome = clock.TimerSet.Task;
            HoldAfterDoneAt("first home", svc, ws, clock);
            var mark = ws.Events.Count;
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await onTheWayHome.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var eventsWhileHoming = ws.Events.Count;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("homing"));
            Assert.That(ws.Events.Count, Is.EqualTo(eventsWhileHoming), "no event for the second Done");
            ws.OnPublish = null;
            clock.AutoAdvance = true;
            clock.Advance(svc.HomeWatchPoll); // the waits held on the clock go on
            Assert.That((await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(log.Rows.Select(r => r.Outcome), Is.EqualTo(CompleteThenChecked), "one Done, one verdict");

            // Checked: Done leaves the verdict standing.
            var eventsAtEnd = ws.Events.Count;
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(ws.Events.Count, Is.EqualTo(eventsAtEnd));
        }

        // ── Stop ─────────────────────────────────────────────────────────────────────────────

        [TestCase("first home")]
        [TestCase("verify")]
        [TestCase("last home")]
        public async Task A_Stop_after_Done_stops_the_mount_where_it_is_and_nothing_moves_after(string moment) {
            var cutShort = moment == "verify" ? MotionKind.RaMotor : MotionKind.FindHome;
            var clock = new ManualMountClock { AutoAdvance = true };
            var mount = GemAtHome(clock);
            var ws = new WsRecorder();
            using var svc = NewHomeService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var held = clock.TimerSet.Task;
            HoldAfterDoneAt(moment, svc, ws, clock);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await held.WaitAsync(SignalTimeout).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(0.5)); // half a second of Find Home, or of the verify turn
            Assert.That(mount.IsMoving, Is.True, mount.Describe());
            Assert.That(svc.IsMovingMount, Is.True, "the telescope endpoints refuse other moves meanwhile");

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
            var last = mount.Motions[^1];
            Assert.That((last.Kind, last.End), Is.EqualTo((cutShort, MotionEnd.Aborted)), "the Stop's AbortSlew ended it. " + mount.Describe());
            var abort = mount.Stops.First(s => s.Kind == StopKind.Abort && s.At >= at);
            Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)));
            mount.AssertNoMotionSince(abort.At);
            Assert.That(mount.GetInfo().AtHome, Is.False, "the mount stays where the Stop found it");
            mount.AssertEndedStopped(tracking: false);
            Assert.That(mount.MotionOwner, Is.Null);
            Assert.That(svc.IsMovingMount, Is.False);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignChecked), Is.Zero);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // ── failures ─────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Find_Home_that_fails_ends_the_run_failed_with_home_failed_the_mount_stopped_and_the_hold_released() {
            var mount = GemAtHome();
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewHomeService(mount, ws, log);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            mount.FindHomeRefused = true;
            var mark = ws.Events.Count;
            var at = mount.Now;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            var error = ws.Events.Skip(mark).Single(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("home_failed"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(PolarAlignService.HomeFailedMessage));
            Assert.That(PolarAlignService.HomeFailedMessage, Does.Contain("did not go Home").And.Contain("stopped the mount").And.Contain("check the mount"));
            Assert.That((status.State, status.Stage, status.CheckResult), Is.EqualTo(("failed", PolarAlignStages.Verify, (PolarAlignCheckResultDto?)null)));
            // The failure's exit stopped the mount (the AbortSlew a Find Home that may run calls for) before it was reported.
            var abort = mount.Stops.Single(s => s.Kind == StopKind.Abort && s.At >= at);
            Assert.That(abort.Reason, Is.EqualTo("polar_align"));
            Assert.That(abort.EventsBefore, Is.LessThanOrEqualTo(LastEventIndex(ws, WsEventCatalog.PolarAlignError)), "stopped before the failure is reported");
            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), "no verify turn after the failed Home. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            Assert.That(mount.MotionOwner, Is.Null, "the motion owner is given back");
            Assert.That(svc.IsMovingMount, Is.False);
            Assert.That(log.Rows.Select(r => r.Outcome), Is.EqualTo(CompleteThenFailed));
        }

        [TestCase("findhome", "verify")]
        [TestCase("park", "first home")]
        public async Task A_Find_Home_or_park_from_outside_after_Done_still_ends_the_run(string by, string moment) {
            var clock = new ManualMountClock { AutoAdvance = true };
            var mount = GemAtHome(clock);
            var ws = new WsRecorder();
            using var svc = NewHomeService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var held = clock.TimerSet.Task;
            HoldAfterDoneAt(moment, svc, ws, clock);
            var mark = ws.Events.Count;
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await held.WaitAsync(SignalTimeout).ConfigureAwait(false);

            // The run waits on the clock: in its verify turn, or for the mount's Find Home (the fake's own wait is the other timer).
            var waits = moment == "verify" ? 1 : 2;
            var deadline = DateTime.UtcNow + SignalTimeout;
            while (clock.PendingTimers < waits) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the run never waited on the clock");
                await Task.Delay(5).ConfigureAwait(false);
            }

            // As the telescope service counts a park or the user's Find Home: a stop for every routine that drives the motors.
            var at = mount.Now;
            mount.FenceMotion(by);
            clock.Advance(TimeSpan.FromSeconds(1)); // the run's next look at the stop count

            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1, mark).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.Skip(mark).First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("stopped_by").GetString(), Is.EqualTo(by));
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"));
            if (moment != "verify") {
                Assert.That(mount.Motions[^1].End, Is.EqualTo(MotionEnd.Running), "seen while the mount was still on its way Home, not once it arrived");
            }
            Assert.That(mount.Stops.Where(s => s.Kind == StopKind.Abort && s.At >= at), Is.Empty, "the mount is left to the " + by + ": no AbortSlew of the run's own");
            Assert.That(mount.MotionOwner, Is.Null);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignChecked), Is.Zero);
        }

        // ── the mount is stopped before each report ──────────────────────────────────────────

        [Test]
        public async Task The_verdict_is_reported_after_the_mount_is_back_Home_stopped_and_tracking_is_off() {
            // The bench mount logs every command: the stop at Done, the two Find Homes, and never a GoTo or an unpark.
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            mount.EventCount = () => ws.Events.Count;
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            var at = mount.Count;

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, Mark(svc), "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            var entries = mount.Entries;
            var log = entries.Select(e => e.Command).ToArray();
            AssertStopRoundFrom(log, at, abort: false); // nothing moves while adjusting: no AbortSlew
            var firstHome = Array.IndexOf(log, "findhome", at);
            var lastHome = Array.LastIndexOf(log, "findhome");
            Assert.That(firstHome, Is.GreaterThan(Array.IndexOf(log, "move secondary 0", at)), "stopped, then sent Home: " + string.Join(", ", log.Skip(at)));
            Assert.That(lastHome, Is.GreaterThan(firstHome), "Home again after the verify turns");
            Assert.That(log.Skip(firstHome + 1).Take(lastHome - firstHome - 1).Where(IsMotion), Is.Not.Empty.And.All.StartsWith("move primary"),
                "between the two Find Homes only the verify turns move the mount");
            Assert.That(log, Does.Not.Contain("goto").And.Not.Contain("unpark"), string.Join(", ", log));
            // The final exit: a stop round with nothing to abort, then tracking off, all before the verdict's event.
            var round = ExitRoundStart(log, abort: false);
            Assert.That(round, Is.GreaterThan(lastHome));
            Assert.That(log.Last(), Is.EqualTo("tracking off"));
            var checkedAt = ws.Events.Select((e, i) => (e, i)).First(x => x.e.Type == WsEventCatalog.PolarAlignChecked).i;
            Assert.That(entries.Skip(lastHome).Select(e => e.EventsBefore), Is.All.LessThanOrEqualTo(checkedAt),
                "the mount is Home, stopped and not tracking before the verdict is reported");
            Assert.That(mount.Tracking, Is.False);
        }
    }
}
