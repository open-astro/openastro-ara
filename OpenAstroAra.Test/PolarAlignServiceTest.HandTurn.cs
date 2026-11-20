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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2083 — the hand-turn run (NINA's manual mode) on <see cref="FakeGemMount"/>: the test plays the user, turning the
    /// true RA axis by hand when the run asks (<see cref="FakeGemMount.MoveByOtherMeans"/>, the clutch open, so the mount's
    /// own reading never moves), and the solves see where the tube really points. The run must measure the axis and reach
    /// the live loop with nothing moved by Ara: no motor move, no GoTo, no Find Home, and tracking never turned on. Like a
    /// motor run, each Start makes one measurement (#2082).
    /// </summary>
    public partial class PolarAlignServiceTest {

        private static async Task<PolarAlignStartPlanDto> StartByHandAsync(PolarAlignService svc, string stage) {
            var plan = await svc.GetStartPlanAsync(stage, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(stage, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, HandTurn: true), null, CancellationToken.None)
                .ConfigureAwait(false);
            return plan;
        }

        /// <summary>Waits until the run asks for the turn to <paramref name="point"/> (its first <c>hand_turn</c> event for
        /// it from event <paramref name="from"/> on), then turns the true RA axis <paramref name="deg"/> (null: the turn
        /// asked) the way it asked (east: the hour angle falls), with the clutch open. Returns the direction asked.</summary>
        private static async Task<string> TurnByHandWhenAskedAsync(WsRecorder ws, FakeGemMount mount, int point, double? deg, int from = 0) {
            static bool Asks((string Type, System.Text.Json.JsonElement Payload) e, int point) =>
                e.Type == WsEventCatalog.PolarAlignHandTurn
                && e.Payload.GetProperty("point").GetInt32() == point
                && e.Payload.GetProperty("waiting").GetBoolean();
            await ws.WhenAsync(events => events.Skip(from).Any(e => Asks(e, point))).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var asked = ws.Events.Skip(from).First(e => Asks(e, point)).Payload;
            var direction = asked.GetProperty("direction").GetString()!;
            var turnDeg = deg ?? asked.GetProperty("target_deg").GetDouble();
            var pose = mount.TruePose;
            var sign = direction == PolarAlignDirections.East ? -1.0 : 1.0;
            mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + (sign * turnDeg) }, encodersFollow: false);
            return direction;
        }

        private static bool TrackingOnSent(FakeGemMount mount) => mount.Commands.Any(c => c.Text == "tracking on");

        [TestCase(true)]
        [TestCase(false)]
        public async Task A_hand_turn_fine_run_measures_the_axis_with_no_motor_move_GoTo_or_Find_Home_and_never_turns_tracking_on(bool tracking) {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0, tracking: tracking, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            var plan = await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            var asked2 = await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            var asked3 = await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            Assert.That(status.HandTurn, Is.True);
            Assert.That(ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignStarted).Payload.GetProperty("hand_turn").GetBoolean(),
                Is.True, "polar_align.started says the run turns by hand");
            Assert.That(plan.HandTurnAvailable, Is.True, "the plan tells the app this rig takes a hand-turn start");
            Assert.That((asked2, asked3), Is.EqualTo((plan.Direction, plan.Direction)), "the turns go the way the user confirmed");
            Assert.That(status.CurrentErrorArcmin, Is.LessThan(3.0), "the fake's axis sits on the pole: only refraction is left");
            Assert.That(mount.Motions, Is.Empty, "Ara moved nothing: no motor move, no GoTo, no Find Home. " + mount.Describe());
            Assert.That(TrackingOnSent(mount), Is.False, "a hand-turn run never turns tracking on");
            Assert.That(mount.Forbidden, Is.Empty);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_hand_turn_run_shows_how_far_the_axis_has_turned_and_waits_until_the_turn_is_in() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            // Half the step: the run keeps waiting and says how far the axis has turned.
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 5.0).ConfigureAwait(false);
            // Three solves past the half turn, all still (two would take a turn), or the run moving on to point 3.
            static bool HalfTurnSeen((string Type, System.Text.Json.JsonElement Payload) e) =>
                e.Type == WsEventCatalog.PolarAlignHandTurn
                && e.Payload.GetProperty("turned_deg").ValueKind == System.Text.Json.JsonValueKind.Number
                && e.Payload.GetProperty("turned_deg").GetDouble() > 4.0;
            static bool Point3((string Type, System.Text.Json.JsonElement Payload) e) =>
                e.Type == WsEventCatalog.PolarAlignHandTurn && e.Payload.GetProperty("point").GetInt32() == 3;
            await ws.WhenAsync(events => events.Count(HalfTurnSeen) >= 3 || events.Any(Point3))
                .WaitAsync(SignalTimeout).ConfigureAwait(false);
            var waiting = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(ws.Events.Any(Point3), Is.False, "5° of a 10° step is not a turn: the run did not move on");
            Assert.That(waiting.State, Is.EqualTo("seeding"));
            Assert.That(waiting.HandTurnWait, Is.Not.Null);
            Assert.That(waiting.HandTurnWait!.Point, Is.EqualTo(2), "5° of a 10° step is not a turn yet");
            Assert.That(waiting.HandTurnWait.TurnedDeg, Is.EqualTo(5.0).Within(0.2));
            Assert.That(mount.Motions, Is.Empty);
        }

        [Test]
        public async Task A_hand_turn_check_gives_its_verdict_with_no_motor_move() {
            // Tracking off at the start, as the fine stage's live loop leaves it: a check that turned it on would show.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0, tracking: false, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Check).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.TotalErrorArcmin, Is.LessThan(3.0));
            Assert.That(status.CheckSolvesDone, Is.EqualTo(3));
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
            Assert.That(TrackingOnSent(mount), Is.False);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignChecked), CounterweightLimitDeg);
        }

        private static readonly bool[] OneHandStart = { true };

        [Test]
        public async Task A_hand_turn_Check_from_the_adjust_loop_ends_it_and_takes_its_two_turns_by_hand_with_no_motor_move() {
            // #2082 — Check on the adjust screen is a confirmed Start(check) through the session's sweep plan; the user picks
            // Turn RA by hand in its confirm step too. The loop ends like Stop, then the check asks for its turns the way
            // its plan says, and its verdict sits beside the loop's readout. Ara moves nothing in either run.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var checkFrom = ws.Events.Count;
            var plan = await StartByHandAsync(svc, PolarAlignStages.Check).ConfigureAwait(false);
            var asked2 = await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, from: checkFrom).ConfigureAwait(false);
            var asked3 = await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, from: checkFrom).ConfigureAwait(false);
            var status = await PollStateAsync(svc, checkFrom, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            Assert.That(status.HandTurn, Is.True);
            Assert.That(status.CheckResult!.FineErrorArcmin, Is.Not.Null, "the loop's readout travels into the check");
            Assert.That((asked2, asked3), Is.EqualTo((plan.Direction, plan.Direction)), "the check's turns go the way its plan says");
            Assert.That(ws.Events.Skip(checkFrom).Where(e => e.Type == WsEventCatalog.PolarAlignStarted)
                    .Select(e => e.Payload.GetProperty("hand_turn").GetBoolean()), Is.EqualTo(OneHandStart),
                "the loop ended and one check started, by hand");
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
            Assert.That(TrackingOnSent(mount), Is.False, "a hand-turn check never turns tracking on");
        }

        [Test]
        public async Task A_hand_turn_run_never_drives_the_Polar_Aligner_and_leaves_its_bolts_to_the_user() {
            // A host-driven Polar Aligner with auto on (the default): a motor run would probe and correct with it.
            var aligner = new FakeAligner(azOffset: 20, altOffset: 10);
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, aligner: aligner);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            await AlignerEventAsync(ws, PolarAlignCorrectionStates.Manual).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.Correction?.State, Is.EqualTo(PolarAlignCorrectionStates.Manual), "the person turns the bolts, as with auto off");
            Assert.That(aligner.Moves, Is.Empty, "a hand-turn run moves no Polar Aligner motor");
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public async Task A_finished_hand_turn_run_leaves_tracking_as_stop_tracking_when_done_says(bool stopTrackingWhenDone, bool trackingAfter) {
            // Tracking on at the start; the live loop turns it off. Done gives back the tracking the run found only when
            // stop_tracking_when_done is off, as every run does: the one time a hand-turn run sends tracking on.
            var mount = PolledGemAt(-4.0);
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { StopTrackingWhenDone = stopTrackingWhenDone });
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, store);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var trackingWhileAdjusting = mount.Tracking;
            var tracksOnBeforeDone = mount.Commands.Count(c => c.Text == "tracking on");
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(trackingWhileAdjusting, Is.False, "the live loop needs the camera still");
            Assert.That(tracksOnBeforeDone, Is.Zero, "nothing turned tracking on while the run measured");
            mount.AssertEndedStopped(tracking: trackingAfter);
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
        }

        [Test]
        public async Task A_start_that_picks_the_other_way_of_turning_RA_while_a_run_is_active_is_refused_and_a_matching_retry_is_accepted() {
            // Two devices: a motor run turns the mount (the daemon's own speeds and 30° steps, so it is still turning), and
            // the other device, which has not seen it start yet, confirms Turn RA by hand.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, NewStore(), benchMotors: false);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None)
                .ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            RefusalException? refusal = null;
            try {
                await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, HandTurn: true), null,
                    CancellationToken.None).ConfigureAwait(false);
            } catch (RefusalException ex) {
                refusal = ex;
            }
            var retry = await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null,
                CancellationToken.None).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(refusal?.Cause, Is.EqualTo(RefusalCatalog.PolarAlignAlreadyRunning), "the hand start is refused, never a silent accept");
            Assert.That(retry, Is.Not.Null, "the same start again is a no-op accept");
            Assert.That(status.HandTurn, Is.False, "the motor run goes on");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.EqualTo(1), "no second run started");
        }

        [Test]
        public async Task A_hand_turn_run_whose_first_reading_is_within_3_degrees_of_the_pole_fails_as_on_axis_and_asks_for_no_turn() {
            // #2082 — the plan read Dec 86.9° and the user confirmed Turn RA by hand; the mount was moved to Dec 87.5° before
            // the run's first reading. A hand-turn run makes the motor run's one on-axis check, so the user is never asked to
            // turn a camera that looks along the axis.
            var mount = new FakeStopMount(tracking: true) { DecDeg = 86.9 };
            var fake = NewFake();
            fake.LeaseGate = active => {
                if (active) {
                    mount.DecDeg = 87.5; // after the plan, before the run's first reading
                }
                return Task.CompletedTask;
            };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, guider: fake);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("on_axis"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(PolarAlignService.OnAxisMessage));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignHandTurn), Is.Zero, "nothing was asked of the user");
            Assert.That(mount.Log.Where(IsMotion), Is.Empty, "zero motions: " + string.Join(", ", mount.Log));
        }

        [Test]
        public void HandTurnDeg_takes_the_Earths_turn_out_only_when_the_mount_does_not_track() {
            // 60 s at the sidereal rate is about 0.25° of RA drift with tracking off.
            var drift = 60.0 * PolarAlignService.SiderealDegPerSecond;
            Assert.That(PolarAlignService.HandTurnDeg(10.0, 20.0, 60.0, tracking: true), Is.EqualTo(10.0).Within(1e-9));
            Assert.That(PolarAlignService.HandTurnDeg(10.0, 20.0 + drift, 60.0, tracking: false), Is.EqualTo(10.0).Within(1e-9));
            Assert.That(PolarAlignService.HandTurnDeg(355.0, 5.0, 0.0, tracking: true), Is.EqualTo(10.0).Within(1e-9), "across 0°");
        }
    }
}
