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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2083 — the hand-turn run (NINA's manual mode) on <see cref="FakeGemMount"/>: the test plays the user, turning the
    /// true RA axis by hand when the run asks (<see cref="FakeGemMount.MoveByOtherMeans"/>, the clutch open, so the mount's
    /// own reading never moves), and the solves see where the tube really points. The run must measure the axis and reach
    /// the live loop with nothing moved by Ara: no motor move, no GoTo, no Find Home, and tracking never turned on.
    /// </summary>
    public partial class PolarAlignServiceTest {

        private static async Task<PolarAlignStartPlanDto> StartByHandAsync(PolarAlignService svc, string stage) {
            var plan = await svc.GetStartPlanAsync(stage, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(stage, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin, HandTurn: true), null, CancellationToken.None)
                .ConfigureAwait(false);
            return plan;
        }

        /// <summary>Waits until the run asks for the turn to <paramref name="point"/> (its first <c>hand_turn</c> event for
        /// it), then turns the true RA axis <paramref name="deg"/> the way it asked (east: the hour angle falls), with the
        /// clutch open. Returns the direction asked.</summary>
        private static async Task<string> TurnByHandWhenAskedAsync(WsRecorder ws, FakeGemMount mount, int point, double deg) {
            static bool Asks((string Type, System.Text.Json.JsonElement Payload) e, int point) =>
                e.Type == WsEventCatalog.PolarAlignHandTurn
                && e.Payload.GetProperty("point").GetInt32() == point
                && e.Payload.GetProperty("waiting").GetBoolean();
            await ws.WhenAsync(events => events.Any(e => Asks(e, point))).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var direction = ws.Events.First(e => Asks(e, point)).Payload.GetProperty("direction").GetString()!;
            var pose = mount.TruePose;
            var sign = direction == PolarAlignDirections.East ? -1.0 : 1.0;
            mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + (sign * deg) }, encodersFollow: false);
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
            var mount = PolledGemAt(-4.0);
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

        [Test]
        public async Task A_hand_turn_start_with_the_camera_on_the_axis_fails_and_never_tilts_the_tube() {
            // Dec 88.5°: a motor run tilts the tube 5° off the axis with the Dec motor; a hand run asks the user to.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 88.5, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.HandTurnOnAxis));
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
            Assert.That(ws.Count(WsEventCatalog.PolarAlignHandTurn), Is.Zero, "nothing was asked of the user");
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
