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
        /// <param name="encodersFollow">The hand pad (the mount's reading turns with the axis) rather than the clutch.</param>
        private static async Task<string> TurnByHandWhenAskedAsync(WsRecorder ws, FakeGemMount mount, int point, double? deg, int from = 0,
                bool encodersFollow = false) {
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
            mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + (sign * turnDeg) }, encodersFollow);
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

        /// <summary>The true-sky solve (<see cref="TrueSkySolver"/>), recording each frame's id (its file name).</summary>
        private sealed class RecordingTrueSkySolver(FakeGemMount mount) : IPolarAlignFrameSolver {
            private readonly System.Collections.Generic.List<string> _frames = new();
            public string[] Frames {
                get {
                    lock (_frames) {
                        return _frames.ToArray();
                    }
                }
            }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                lock (_frames) {
                    _frames.Add(System.IO.Path.GetFileNameWithoutExtension(fitsPath));
                }
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        [Test]
        public async Task A_hand_turn_run_averages_frames_per_point_at_each_point_once_the_turn_is_in() {
            // #2082 with #2083: three frames per point. The first point is averaged where the mount points; points 2 and 3
            // are averaged once the turn is taken and the mount still, their ids going on from the wait's frames.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var solver = new RecordingTrueSkySolver(mount);
            var logger = new CapturingLogger();
            var store = TenDegreeStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { FramesPerPoint = 3 });
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, store, logger: logger);
            svc.AxisFastRateDegPerSec = 30.0;
            svc.AxisSlowRateDegPerSec = 10.0;

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, encodersFollow: true).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, encodersFollow: true).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            var scatter = logger.Entries.Where(e => e.EventId == 4574).Select(e => e.Message).ToArray();
            Assert.That(scatter, Has.Length.EqualTo(3), "each of the three points is averaged");
            Assert.That(scatter, Has.All.Contains("3 solves averaged"));
            foreach (var point in new[] { "seed-b", "seed-c" }) {
                var ids = solver.Frames.Where(f => f.StartsWith(point + "-", StringComparison.Ordinal)).ToArray();
                Assert.That(ids, Is.Unique, "the averaged frames' ids go on from the wait's");
                Assert.That(ids.Length, Is.GreaterThanOrEqualTo(2 + 3), point + ": the wait's still solves, then three frames averaged");
            }
            Assert.That(status.CurrentErrorArcmin, Is.LessThan(3.0));
            Assert.That(mount.Motions, Is.Empty, "Ara moved nothing");
        }

        [Test]
        public async Task A_hand_turn_run_shows_how_far_the_axis_has_turned_and_waits_until_the_turn_is_in() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            // Half the step with the hand pad: the run keeps waiting and says how far the mount's reading shows the turn.
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 5.0, encodersFollow: true).ConfigureAwait(false);
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

        /// <summary>A plate solve of a mount whose RA axis sits <paramref name="tiltDeg"/> from the celestial pole (tilted
        /// toward the meridian, an altitude error): the fake's pointing, which is about its own axis, turned by the polar
        /// error in the ground frame. The mount's readings stay about its own axis, as an unsynced mount's do.</summary>
        private sealed class TiltedAxisSolver(FakeGemMount mount, double tiltDeg) : IPolarAlignFrameSolver {
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                const double R = Math.PI / 180.0;
                var (ra, dec) = mount.TrueSky;
                var lst = mount.TrueLstHours * 15.0;
                var ha = (lst - ra) * R;
                var (x, y, z) = (Math.Cos(dec * R) * Math.Cos(ha), Math.Cos(dec * R) * Math.Sin(ha), Math.Sin(dec * R));
                var (c, s) = (Math.Cos(tiltDeg * R), Math.Sin(tiltDeg * R));
                (x, z) = ((c * x) + (s * z), (-s * x) + (c * z));
                var haSky = Math.Atan2(y, x) / R;
                var decSky = Math.Asin(Math.Clamp(z, -1.0, 1.0)) / R;
                return Task.FromResult(new PolarAlignSolveOutcome(true, GemGeometry.Wrap360(lst - haSky), decSky));
            }
        }

        /// <summary>The routine on the fake with <see cref="TiltedAxisSolver"/>, NINA's 10° steps.</summary>
        private static PolarAlignService NewTiltedGemService(FakeGemMount mount, WsRecorder ws, double tiltDeg) {
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(NewFake(), new TiltedAxisSolver(mount, tiltDeg), mount, ws, TenDegreeStore());
            svc.AxisFastRateDegPerSec = 30.0;
            svc.AxisSlowRateDegPerSec = 10.0;
            return svc;
        }

        private static bool IsHandTurn((string Type, System.Text.Json.JsonElement Payload) e) => e.Type == WsEventCatalog.PolarAlignHandTurn;

        private static bool ClaimsAngle((string Type, System.Text.Json.JsonElement Payload) e) =>
            IsHandTurn(e) && e.Payload.TryGetProperty("turned_deg", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Number;

        [Test]
        public async Task A_clutch_turn_with_the_RA_axis_3_degrees_off_the_pole_is_taken_by_the_solved_distance_claims_no_angle_and_measures_the_error() {
            // The camera 4° from the RA axis, the axis 3° from the pole: a true 10° turn moves the solved RA only about 6°,
            // so a turn read from the solved RA never reaches 8° and the run would wait for ever. With the clutch open the
            // mount's reading stands, so the run claims no angle and takes each turn by the distance the pointing moved.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 86.0, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            using var svc = NewTiltedGemService(mount, ws, tiltDeg: 3.0);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(180.0).Within(6.0), "the fit finds the 3° polar error");
            Assert.That(ws.Events.Where(IsHandTurn), Is.Not.Empty);
            Assert.That(ws.Events.Where(ClaimsAngle).Select(e => e.Payload.GetRawText()), Is.Empty,
                "with the clutch open no angle is claimed, so the screen never says 'turned 6°' or 'the other way'");
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
        }

        [Test]
        public async Task A_hand_pad_turn_with_the_RA_axis_3_degrees_off_the_pole_shows_the_mounts_own_turn() {
            // The hand pad turns the mount's reading with the axis: the run shows that turn (5.0°), not the solved RA's
            // change (about 3° here), and takes the turn once the reading shows 80 % of the step.
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 86.0, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            var ws = new WsRecorder();
            using var svc = NewTiltedGemService(mount, ws, tiltDeg: 3.0);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 5.0, encodersFollow: true).ConfigureAwait(false);
            await ws.WhenAsync(events => events.Count(ClaimsAngle) >= 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var waiting = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var pose = mount.TruePose;
            mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + (waiting.HandTurnWait!.Direction == PolarAlignDirections.East ? -5.0 : 5.0) });
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, encodersFollow: true).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(waiting.HandTurnWait.Point, Is.EqualTo(2), "5° of a 10° step is not a turn yet");
            Assert.That(waiting.HandTurnWait.TurnedDeg, Is.EqualTo(5.0).Within(0.2), "the mount's own turn, wherever the pole is");
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(180.0).Within(6.0));
        }

        [TestCase(true, 25.0)]
        [TestCase(false, 60.0)]
        public async Task A_hand_turn_past_the_measurements_band_fails_the_run_as_hand_turn_too_far(bool encodersFollow, double turnDeg) {
            // Two 10° turns and 2° of slack: a turn that takes the axis further from the first frame ends the run, by the
            // mount's reading with the hand pad, and by the least turn the solved distance needs with the clutch open.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: turnDeg, encodersFollow: encodersFollow).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("hand_turn_too_far"), error.GetRawText());
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public async Task After_a_clutch_turn_a_motor_start_is_refused_until_the_mount_is_synced(bool encodersFollow, bool refused) {
            // A hand run with the clutch open leaves the mount's reading off by the user's turns: Polar Align forgets its
            // sweep origin, and refuses a motor start (its band and direction come from that reading) until a sync. A hand
            // start still goes ahead. The hand pad keeps the reading true: nothing is refused.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, encodersFollow: encodersFollow).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, encodersFollow: encodersFollow).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            RefusalException? refusal = null;
            try {
                await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (RefusalException ex) {
                refusal = ex;
            }
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            var handStarted = (await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).HandTurn;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            mount.SyncEpoch++;
            var synced = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(synced.Direction, synced.NewOrigin), null, CancellationToken.None)
                .ConfigureAwait(false);
            var motorStarted = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(refusal?.Cause, refused ? Is.EqualTo(RefusalCatalog.PolarAlignClutchTurned) : Is.Null);
            if (refused) {
                Assert.That(refusal!.Message, Is.EqualTo(PolarAlignService.ClutchTurnedMessage));
                Assert.That(plan.NewOrigin, Is.True, "the sweep origin was a reading the clutch turn made wrong: forgotten");
            }
            Assert.That(handStarted, Is.True, "a hand-turn start goes ahead");
            Assert.That(motorStarted.HandTurn, Is.False, "after a sync the motor start goes ahead");
        }

        [Test]
        public async Task A_hand_turn_run_gives_the_mount_back_when_it_ends_so_a_sync_goes_out_and_clears_the_clutch_refusal() {
            // #2066 x #2083 — the hand run holds the mount's motion owner like a motor run, so the sync the clutch refusal
            // asks for (Center's) is not sent while it runs. Once the run ends it gives the mount back: the sync goes out,
            // and a motor start goes ahead.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var ownerWhileRunning = mount.MotionOwner;
            var syncedWhileRunning = mount.SyncAsTheDaemonDoes();
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var ownerAfter = mount.MotionOwner;
            var syncedAfter = mount.SyncAsTheDaemonDoes();

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            RefusalException? refusal = null;
            try {
                await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (RefusalException ex) {
                refusal = ex;
            }
            var motorStarted = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(ownerWhileRunning, Is.EqualTo(MotionOwners.PolarAlign), "the hand run holds the mount");
            Assert.That(syncedWhileRunning, Is.False, "no sync while the run holds the mount");
            Assert.That(ownerAfter, Is.Null, "given back once the run ended");
            Assert.That(syncedAfter, Is.True, "the sync goes out once the run ended");
            Assert.That(refusal, Is.Null, "the sync cleared the clutch refusal");
            Assert.That(motorStarted.HandTurn, Is.False, "the motor start went ahead");
        }

        [Test]
        public async Task A_motor_Check_from_the_adjust_loop_after_a_clutch_turn_is_refused_and_the_loop_runs_on() {
            // The adjust screen's Check with Move the mount after a clutch-open hand run: refused while the loop still runs,
            // so the user keeps the live readout and the mount is not stopped. Refused only after the loop ended, the
            // readout would be gone for a check that never starts.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var from = ws.Events.Count;
            var stopsBefore = mount.Stops.Count;
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            RefusalException? refusal = null;
            try {
                await svc.StartAsync(PolarAlignStages.Check, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (RefusalException ex) {
                refusal = ex;
            }
            var after = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var stopsAfter = mount.Stops.Count;
            var loopEnded = ws.Events.Skip(from).Any(e => e.Type == WsEventCatalog.PolarAlignStopped);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(refusal?.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClutchTurned));
            Assert.That(after.State, Is.EqualTo("adjusting"), "the loop still runs");
            Assert.That(loopEnded, Is.False, "the loop was not ended");
            Assert.That(stopsAfter, Is.EqualTo(stopsBefore), "nothing was stopped");
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public async Task After_a_clutch_turn_the_exit_does_not_record_where_the_run_left_the_mount(bool encodersFollow, bool recorded) {
            // The exit's reading is where the next start would measure "moved by other means" from. With the clutch open it
            // is off by the user's turns, so it stays forgotten with the origin; the hand pad keeps the reading true.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, encodersFollow: encodersFollow).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 3, deg: 10.0, encodersFollow: encodersFollow).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(svc.SweepLastLeftRaDeg, recorded ? Is.Not.Null : Is.Null);
        }

        [Test]
        public async Task A_Stop_while_a_hand_turn_run_waits_for_a_turn_clears_the_wait_from_the_status() {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await ws.WhenAsync(events => events.Any(IsHandTurn)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var waiting = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var stopped = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(waiting.HandTurnWait?.Point, Is.EqualTo(2));
            Assert.That(stopped.State, Is.EqualTo("stopped"));
            Assert.That(stopped.HandTurnWait, Is.Null, "no run waits for a turn after Stop");
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task A_hand_pad_turn_whose_mount_reads_fail_as_the_turn_lands_is_no_clutch_turn_and_a_motor_start_goes_ahead(int failAt) {
            // The hand pad turns the mount's reading with the axis, but the Alpaca reads of RA/Dec time out on the frames
            // just after the turn for point failAt. A failed read says nothing about the clutch: those frames are not
            // measured, the next reads show the turn, and nothing is refused afterwards.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            foreach (var point in new[] { 2, 3 }) {
                await TurnByHandWhenAskedAsync(ws, mount, point: point, deg: 10.0, encodersFollow: true).ConfigureAwait(false);
                if (point == failAt) {
                    mount.FreshReadsFail = 3; // a frame in flight may take one; the frames after the turn fail too
                }
            }
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            RefusalException? refusal = null;
            try {
                await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (RefusalException ex) {
                refusal = ex;
            }
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"),
                string.Join("; ", ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignError).Select(e => e.Payload.GetRawText())));
            Assert.That(mount.FreshReadsFail, Is.Zero, "the reads did fail during the run");
            Assert.That(refusal, Is.Null, "a failed read is no clutch turn: the motor start goes ahead");
        }

        /// <summary>A plate solve of where the tube really points that, on the second solve after the true RA axis has
        /// turned <see cref="TurnedDeg"/> from where it first stood, turns the mount's tracking off: the user (or the mount)
        /// changing tracking between two frames of a hand-turn wait. <see cref="FlippedAtEvent"/>: the event count then.</summary>
        private sealed class TrackingFlipSolver(FakeGemMount mount, WsRecorder ws) : IPolarAlignFrameSolver {
            public const double TurnedDeg = 8.0;
            private double? _firstHaDeg;
            private int _turnedSolves;
            public volatile int FlippedAtEvent = -1;

            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRaDegJnow, double? hintDecDegJnow, CancellationToken ct) {
                var ha = mount.TruePose.MechHaDeg;
                _firstHaDeg ??= ha;
                if (Math.Abs(ha - _firstHaDeg.Value) >= TurnedDeg && ++_turnedSolves == 2) {
                    FlippedAtEvent = ws.Events.Count;
                    mount.SetTrackingEnabled(false);
                }
                var (ra, dec) = mount.TrueSky;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, dec));
            }
        }

        private static readonly bool[] RestartedThenTaken = { true, false };

        [Test]
        public async Task A_frame_where_tracking_changed_is_not_measured_and_the_still_solves_start_again() {
            // A 10° hand-pad turn is taken on the second still solve. Tracking turns off on the frame after the turn is first
            // solved, a frame that would be the second still solve: it is not measured, and the count of still solves
            // starts again, so the frame after it waits (one still solve) and the next takes the turn.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            var solver = new TrackingFlipSolver(mount, ws);
            using var svc = NewServiceOn(NewFake(), solver, mount, ws, TenDegreeStore());

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 10.0, encodersFollow: true).ConfigureAwait(false);
            static bool Point((string Type, System.Text.Json.JsonElement Payload) e, int point) =>
                IsHandTurn(e) && e.Payload.GetProperty("point").GetInt32() == point;
            await ws.WhenAsync(events => events.Any(e => Point(e, 3))).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(solver.FlippedAtEvent, Is.GreaterThanOrEqualTo(0), "tracking changed during the wait");
            var afterFlip = ws.Events.Skip(solver.FlippedAtEvent).Where(e => Point(e, 2))
                .Select(e => e.Payload.GetProperty("waiting").GetBoolean()).ToArray();
            Assert.That(afterFlip, Is.EqualTo(RestartedThenTaken),
                "the changed frame not measured, then one still solve that waits, then the turn taken: " + string.Join(", ", afterFlip));
            Assert.That(mount.Motions, Is.Empty, mount.Describe());
        }

        [Test]
        public async Task A_hand_turn_wait_whose_mount_reads_hang_goes_on_solving_and_publishing() {
            // An AlpacaBridge read stuck on the wire: each solved frame's RA/Dec read gives up after HandTurnReadTimeout and
            // counts as a failed read, so the wait loop keeps solving and telling the app rather than stalling on the read.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.HandTurnReadTimeout = TimeSpan.FromMilliseconds(200);
            var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await ws.WhenAsync(events => events.Any(IsHandTurn)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            mount.HangReads = hang;
            await mount.ReadHung.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var hungFrom = ws.Events.Count;
            Exception? stalled = null;
            try {
                await ws.WhenAsync(events => events.Skip(hungFrom).Count(IsHandTurn) >= 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            } catch (TimeoutException ex) {
                stalled = ex;
            }
            var waiting = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            mount.HangReads = null;
            hang.SetResult();
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(stalled, Is.Null, "the wait loop stalled on a hung mount read");
            Assert.That(waiting.HandTurnWait?.Point, Is.EqualTo(2), "still waiting for the turn");
        }

        private static (double RaDeg, double DecDeg, DateTimeOffset At, (double RaDeg, double DecDeg)? Reading) HandAt(
                double raDeg, double decDeg, double seconds, (double RaDeg, double DecDeg)? reading) =>
            (raDeg, decDeg, DateTimeOffset.UnixEpoch.AddSeconds(seconds), reading);

        [Test]
        public void MeasureHandTurn_flags_a_clutch_turn_only_when_the_mount_was_read_at_both_ends_and_stood() {
            // The camera 5° from the pole, tracking: the solves move 10° of RA (about 0.87° on the sky), well past the
            // distance a clutch turn needs, while the mount's reading stands, or one end's read failed.
            var start = HandAt(0.0, 85.0, 0.0, (0.0, 85.0));
            var nowRead = HandAt(10.0, 85.0, 60.0, (0.0, 85.0));
            var nowUnread = HandAt(10.0, 85.0, 60.0, null);
            var fromUnread = HandAt(0.0, 85.0, 0.0, null);

            var stood = PolarAlignService.MeasureHandTurn(start, start, nowRead, 1.0, 10.0, tracking: true);
            var readFailedNow = PolarAlignService.MeasureHandTurn(start, start, nowUnread, 1.0, 10.0, tracking: true);
            var readFailedAtTheLastPoint = PolarAlignService.MeasureHandTurn(start, fromUnread, nowRead, 1.0, 10.0, tracking: true);

            Assert.That((stood.Reached, stood.ByClutch), Is.EqualTo((true, true)), "read at both ends and stood: the clutch is open");
            Assert.That((readFailedNow.Reached, readFailedNow.ByClutch, readFailedNow.ReadingUnknown), Is.EqualTo((false, false, true)),
                "this frame's read failed: not measured, the next frame reads again");
            Assert.That((readFailedAtTheLastPoint.Reached, readFailedAtTheLastPoint.ByClutch), Is.EqualTo((true, false)),
                "the last point's reading is unknown: the solves take the turn, and no clutch turn is flagged");
            Assert.That(readFailedAtTheLastPoint.TurnedDeg, Is.Null, "no angle is claimed");
        }

        [Test]
        public void HandTurnSettled_takes_a_clutch_turn_only_after_three_solves_at_rest_and_the_minimum_wait() {
            var wait = PolarAlignService.DefaultHandTurnClutchMinWait;
            Assert.That(wait, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(PolarAlignService.HandTurnSettled(byReading: true, 2, TimeSpan.Zero, wait), Is.True, "the hand pad: two solves at rest");
            Assert.That(PolarAlignService.HandTurnSettled(byReading: true, 1, TimeSpan.FromMinutes(1), wait), Is.False);
            Assert.That(PolarAlignService.HandTurnSettled(byReading: false, 2, TimeSpan.FromMinutes(1), wait), Is.False, "the clutch: two are not enough");
            Assert.That(PolarAlignService.HandTurnSettled(byReading: false, 3, TimeSpan.FromSeconds(4.9), wait), Is.False, "the clutch: too soon after the ask");
            Assert.That(PolarAlignService.HandTurnSettled(byReading: false, 3, wait, wait), Is.True);
        }

        [Test]
        public async Task A_small_nudge_with_the_clutch_open_is_not_taken_as_the_turn_before_the_points_minimum_wait() {
            // The camera 5° from the pole: a 4° nudge with the clutch open moves the solves past the distance an 8° turn
            // needs at the axis distance the run allows for. Held still, it is still not taken before the wait is up, so
            // the user has time to finish the 10° turn.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.HandTurnClutchMinWait = TimeSpan.FromMinutes(1);

            await StartByHandAsync(svc, PolarAlignStages.Fine).ConfigureAwait(false);
            await TurnByHandWhenAskedAsync(ws, mount, point: 2, deg: 4.0).ConfigureAwait(false);
            var nudged = ws.Events.Count;
            static bool Point((string Type, System.Text.Json.JsonElement Payload) e, int point) =>
                IsHandTurn(e) && e.Payload.GetProperty("point").GetInt32() == point;
            await ws.WhenAsync(events => events.Skip(nudged).Count(e => Point(e, 2)) >= 6 || events.Any(e => Point(e, 3)))
                .WaitAsync(SignalTimeout).ConfigureAwait(false);
            var waiting = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(ws.Events.Any(e => Point(e, 3)), Is.False, "five solves at rest after a 4° nudge, and the turn is not taken yet");
            Assert.That(waiting.HandTurnWait?.Point, Is.EqualTo(2));
        }

        [Test]
        public void ReadingAt_moves_an_averaged_points_reading_to_its_mean_time_only_when_the_mount_does_not_track() {
            // #2082 with #2083: the reading is read as the turn is taken, the averaged point's time is 60 s later.
            var readAt = DateTimeOffset.UnixEpoch;
            var at = readAt.AddSeconds(60.0);
            var drift = 60.0 * PolarAlignService.SiderealDegPerSecond;
            var off = PolarAlignService.ReadingAt((10.0, 85.0), readAt, at, tracking: false);
            var on = PolarAlignService.ReadingAt((10.0, 85.0), readAt, at, tracking: true);
            Assert.That(off!.Value.RaDeg, Is.EqualTo(10.0 + drift).Within(1e-9), "a standing axis's RA reading runs on with the Earth's turn");
            Assert.That(off.Value.DecDeg, Is.EqualTo(85.0));
            Assert.That(on, Is.EqualTo(((double, double)?)(10.0, 85.0)), "tracking holds the reading");
            Assert.That(PolarAlignService.ReadingAt(null, readAt, at, tracking: false), Is.Null, "a failed read stays failed");
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
