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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Test {

    /// <summary>#2166 — "Move to start position": the Dec motor alone takes a scope within 3° of the pole to 4° from it,
    /// under the guards of every Polar Align turn, and only when asked.</summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A counterweight-down mount on its normal side (HA 2 h) at <paramref name="decDeg"/>, on a clock that only
        /// the routine's reads move.</summary>
        private static FakeGemMount PolledGemNearThePole(double decDeg = 88.0, double latitudeDeg = SiteLatDeg) {
            var mount = FakeGemMount.CounterweightDownAt(2.0, decDeg, lstHours: 20.0, latitudeDeg: latitudeDeg, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            return mount;
        }

        /// <summary>The scope's true distance from the site's own pole (south too).</summary>
        private static double OffOwnPoleDeg(FakeGemMount mount) => 90.0 - (Math.Sign(mount.LatitudeDeg) * mount.TrueSky.DecDeg);

        private static void AssertArrivedOnItsSide(PolarAlignMoveToStartResultDto result, FakeGemMount mount, double phiBefore,
                double nearDeg = 3.9, double farDeg = 4.5) {
            var dec = DecMoves(mount);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(StatusOf(result.Preflight, PolarAlignPreflightChecks.StartPointing), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(nearDeg, farDeg), "inside the 2–5° start band");
                Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide), "it ends on the side of the pier it started on");
                Assert.That(mount.TruePhiDeg, Is.EqualTo(phiBefore).Within(0.05), "the counterweights stay: the RA axis did not turn");
                Assert.That(mount.Motions.Where(m => m.Kind != MotionKind.DecMotor), Is.Empty, "Dec alone. " + mount.Describe());
                Assert.That(dec.Select(m => m.End), Has.None.EqualTo(MotionEnd.Reversed), "never reversed mid-move");
                Assert.That(mount.MotionOwner, Is.Null);
            });
        }

        private static MotionEntry[] DecMoves(FakeGemMount mount) =>
            mount.Motions.Where(m => m.Kind == MotionKind.DecMotor && !m.Refused).ToArray();

        private static string StatusOf(PolarAlignPreflightDto dto, string id) => dto.Checks.Single(c => c.Id == id).Status;

        private static double OffPoleDeg(FakeGemMount mount) => 90.0 - mount.TrueSky.DecDeg;

        // ── the offer, and nothing moves before the call ─────────────────────────────────────

        [Test]
        public async Task The_preflight_at_Dec_88_offers_the_move_to_the_start_position_and_moves_nothing() {
            var mount = PolledGemNearThePole();
            using var svc = NewGemService(mount, new WsRecorder());

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.StartPointing), Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(preflight.MoveToStart, Is.Not.Null, "within 3° of the pole: the move is offered");
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.Null);
                Assert.That(preflight.MoveToStart.TargetOffPoleDeg, Is.EqualTo(4.0));
                Assert.That(preflight.MoveToStart.ReachDeg, Is.EqualTo(4.3).Within(1e-9));
                Assert.That(mount.Motions, Is.Empty, "the offer moves nothing. " + mount.Describe());
                Assert.That(mount.Commands.Where(c => c.IsMotion), Is.Empty);
            });
        }

        [Test]
        public async Task Off_the_pole_the_move_is_not_offered_and_is_refused_with_nothing_moved() {
            var mount = PolledGemNearThePole(decDeg: 85.0);
            using var svc = NewGemService(mount, new WsRecorder());

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart, Is.Null);
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMoveToStartNotNeeded));
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }

        // ── the move ─────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Move_to_start_turns_Dec_alone_to_4_degrees_from_the_pole_and_returns_a_passing_start_pointing() {
            var mount = PolledGemNearThePole();
            var phiBefore = mount.TruePhiDeg;
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            var dec = DecMoves(mount);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message);
                Assert.That(StatusOf(result.Preflight, PolarAlignPreflightChecks.StartPointing), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(result.Preflight.MoveToStart, Is.Null, "nothing more to move");
                Assert.That(mount.Motions.Where(m => m.Kind != MotionKind.DecMotor), Is.Empty, "Dec alone: no RA turn, no GoTo. " + mount.Describe());
                Assert.That(dec, Has.Length.EqualTo(2), "the probe, then the move off the pole");
                Assert.That(dec.Select(m => Math.Sign(m.Rate)).Distinct().Count(), Is.EqualTo(1), "the probe went away from the pole: the same way on");
                Assert.That(dec.Select(m => m.End), Has.All.EqualTo(MotionEnd.Stopped), "each move ended on its own MoveAxis 0");
                Assert.That(OffPoleDeg(mount), Is.InRange(3.9, 4.5), "4° from the pole, inside the 2–5° start band");
                Assert.That(mount.TruePhiDeg, Is.EqualTo(phiBefore).Within(0.05), "the counterweights stay down: the RA axis did not turn");
                Assert.That(mount.Tracking, Is.True, "the tracking the mount had is handed back");
                Assert.That(mount.MotionOwner, Is.Null, "the motion owner is given back");
                Assert.That(svc.IsActive, Is.False);
            });
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task A_probe_toward_the_pole_is_stopped_and_settled_and_a_new_move_goes_the_other_way() {
            var mount = PolledGemNearThePole();
            mount.DecMotorSign = -1.0; // a positive rate raises the Dec axis: toward the pole
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            var dec = DecMoves(mount);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message);
                Assert.That(dec, Has.Length.EqualTo(2), mount.Describe());
                Assert.That(dec[0].Rate, Is.GreaterThan(0), "the probe at a positive rate");
                Assert.That(dec[1].Rate, Is.LessThan(0), "then the other way");
                Assert.That(dec[0].End, Is.EqualTo(MotionEnd.Stopped), "the probe stopped on its own: never reversed mid-move");
                Assert.That(dec.Select(m => m.End), Has.None.EqualTo(MotionEnd.Reversed));
                Assert.That(dec[1].StartedAt, Is.GreaterThanOrEqualTo(dec[0].EndedAt!.Value), "a new move, after the probe ended");
                Assert.That(OffPoleDeg(mount), Is.InRange(3.9, 4.5));
            });
        }

        // ── refusals, before anything moves ──────────────────────────────────────────────────

        [TestCase(null)]
        [TestCase(4.0)]
        public async Task Move_to_start_is_refused_without_clearance_for_its_reach_and_nothing_moves(double? confirmedReachDeg) {
            var mount = PolledGemNearThePole();
            var store = TenDegreeStore();
            var settings = store.GetPolarAlignSettings() with { MountClearance = null };
            store.PutPolarAlignSettings(confirmedReachDeg is double reach ? settings.Confirmed(reachDeg: reach) : settings);
            using var svc = NewGemService(mount, new WsRecorder(), store);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.ClearanceNotConfirmed));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
                Assert.That(refusal.Parameters["reach_deg"], Is.EqualTo(PolarAlignService.MoveToStartReachDeg));
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }

        [Test]
        public async Task Move_to_start_is_refused_on_an_unverified_position_and_nothing_moves() {
            var mount = PolledGemNearThePole();
            mount.PositionUnverifiedReason = "power_loss";
            using var svc = NewGemService(mount, new WsRecorder());

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.PoseUnverified));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }

        [Test]
        public async Task Move_to_start_is_refused_on_a_parked_mount_and_nothing_moves() {
            var mount = NewMount();
            mount.Object.GetInfo().AtPark = true;
            mount.Setup(m => m.GetCurrentPosition()).Returns(new Coordinates(0.0, 88.0, Epoch.JNOW, Coordinates.RAType.Degrees));
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.MountParked));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountParked));
            });
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsNotIn(0.0)), Times.Never, "nothing moved");
        }

        [Test]
        public async Task Move_to_start_is_refused_on_a_mount_that_cannot_move_its_Dec_axis_and_nothing_moves() {
            var mount = PolledGemNearThePole();
            mount.CanMoveSecondaryAxis = false;
            using var svc = NewGemService(mount, new WsRecorder());

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.CannotMoveDec));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignCannotMoveDec));
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }

        [Test]
        public async Task Move_to_start_is_refused_while_another_motion_owner_holds_the_mount_and_nothing_moves() {
            var mount = PolledGemNearThePole();
            using var svc = NewGemService(mount, new WsRecorder());
            using var other = mount.AcquireMotion("center");

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountBusy));
                Assert.That(mount.MotionOwner, Is.EqualTo("center"), "the other owner keeps the mount");
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }

        [Test]
        public async Task Move_to_start_is_refused_while_a_Polar_Align_run_is_going_and_moves_no_Dec() {
            var mount = PolledGemAt(-4.0);
            using var svc = NewGemService(mount, new WsRecorder());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignRunActive));
                Assert.That(DecMoves(mount), Is.Empty, mount.Describe());
            });
        }

        // ── the guards of every turn ─────────────────────────────────────────────────────────

        [Test]
        public async Task A_Dec_move_whose_reading_stops_changing_fails_as_stalled_and_its_motor_is_stopped() {
            var mount = PolledGemNearThePole();
            mount.ReadingFrozen = true;
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed));
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.SlewStalled));
                Assert.That(DecMoves(mount).Select(m => m.End), Has.All.EqualTo(MotionEnd.Stopped), "MoveAxis 0 on the way out. " + mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
                Assert.That(mount.MotionOwner, Is.Null);
            });
        }

        [Test]
        public async Task A_Dec_move_past_twice_its_planned_time_fails_as_timed_out() {
            var mount = PolledGemNearThePole();
            mount.MaxAxisRateDegPerSec = 3.0; // the motor runs at under a third of the 10°/s asked
            using var svc = NewGemService(mount, new WsRecorder());
            svc.AxisMoveBoundSlack = TimeSpan.Zero;
            svc.AxisMoveStall = TimeSpan.FromHours(1);

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed));
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.SlewTimeout));
                Assert.That(mount.IsMoving, Is.False, mount.Describe());
            });
        }

        [Test]
        public async Task A_Dec_move_whose_RA_reading_moves_fails_as_ra_moved() {
            var mount = PolledGemNearThePole();
            var reads = 0;
            mount.OnPoseRead = () => {
                if (++reads == 2) {
                    var pose = mount.TruePose;
                    mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + 5.0 });
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed));
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.RaMoved));
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "no move after the guard. " + mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
            });
        }

        [Test]
        public async Task A_Dec_move_whose_pier_side_changes_fails_as_pier_changed() {
            var mount = PolledGemNearThePole();
            var reads = 0;
            mount.OnPoseRead = () => {
                if (++reads == 2) {
                    mount.ReportedSideOverride = PierSide.pierWest;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed));
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.PierChanged));
                Assert.That(mount.IsMoving, Is.False, mount.Describe());
            });
        }

        [Test]
        public async Task Stop_during_the_move_to_start_stops_the_mount_and_nothing_moves_after() {
            var mount = PolledGemNearThePole();
            using var svc = NewGemService(mount, new WsRecorder());
            Task? stopping = null;
            var reads = 0;
            mount.OnPoseRead = () => {
                if (++reads == 6) {
                    stopping = Task.Run(() => svc.StopAsync(null, CancellationToken.None));
                    SpinWait.SpinUntil(() => mount.Stops.Any(s => s.Kind == StopKind.Abort), SignalTimeout);
                }
            };

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);
            await stopping!.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var abort = mount.Stops.First(s => s.Kind == StopKind.Abort);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Stopped), result.Message);
                Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)));
                Assert.That(mount.MotionOwner, Is.Null);
                Assert.That(svc.IsActive, Is.False);
            });
            mount.AssertNoMotionSince(abort.At);
        }

        [Test]
        public async Task Emergency_Stop_during_the_move_to_start_ends_it_and_the_move_sends_no_stop_of_its_own() {
            var mount = PolledGemNearThePole();
            using var svc = NewGemService(mount, new WsRecorder());
            var reads = 0;
            mount.OnPoseRead = () => {
                if (++reads == 6) {
                    mount.AbortSlewAsync(MountStopReasons.EmergencyStop, CancellationToken.None).GetAwaiter().GetResult();
                }
            };

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            var emergency = mount.Stops.First(s => s.Kind == StopKind.Abort);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Stopped));
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.SlewAbortedExternally));
                Assert.That(emergency.Reason, Is.EqualTo(MountStopReasons.EmergencyStop));
                Assert.That(mount.Stops.Where(s => s.Kind == StopKind.Abort && s.Reason == "polar_align"), Is.Empty,
                    "Emergency Stop owns the mount: no AbortSlew of Polar Align's own. " + mount.Describe());
                Assert.That(mount.MotionOwner, Is.Null);
            });
            mount.AssertNoMotionSince(emergency.At);
        }

        // ── at and next to the pole: a probe that goes over it ──────────────────────────────

        [TestCase(90.0, 1.0)]
        [TestCase(90.0, -1.0)]
        [TestCase(89.9, -1.0)]
        public async Task From_Home_or_next_to_the_pole_the_move_arrives_on_the_side_it_started_whichever_way_the_Dec_motor_turns(
                double decDeg, double decMotorSign) {
            var mount = PolledGemNearThePole(decDeg);
            mount.DecMotorSign = decMotorSign;
            var phiBefore = mount.TruePhiDeg;
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            AssertArrivedOnItsSide(result, mount, phiBefore);
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task A_probe_over_the_pole_on_a_driver_that_reads_Dec_past_90_is_folded_back_and_the_move_arrives() {
            var mount = PolledGemNearThePole(90.0);
            mount.DecMotorSign = -1.0; // a positive rate raises the Dec axis: over the pole from Home
            mount.DecPastPoleUnfolded = true;
            var phiBefore = mount.TruePhiDeg;
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            AssertArrivedOnItsSide(result, mount, phiBefore);
            Assert.That(DecMoves(mount)[0].TrueTo.DecAxisDeg, Is.GreaterThan(90.0), "the probe went over the pole. " + mount.Describe());
        }

        // ── the RA guard on the sky ──────────────────────────────────────────────────────────

        [Test]
        public async Task A_mount_that_reports_J2000_moves_to_the_start_with_no_ra_moved() {
            var mount = PolledGemNearThePole();
            mount.ReportedFrame = Epoch.J2000; // its axes read as J2000: the JNOW conversion near the pole moves the RA
            var phiBefore = mount.TruePhiDeg;
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            // The JNOW pole is about 0.15° off the J2000 one: the move ends 4° from the JNOW pole.
            AssertArrivedOnItsSide(result, mount, phiBefore, nearDeg: 3.7, farDeg: 4.5);
        }

        // ── the southern hemisphere ──────────────────────────────────────────────────────────

        [TestCase(1.0)]
        [TestCase(-1.0)]
        public async Task At_a_southern_site_the_move_takes_the_scope_4_degrees_from_the_south_pole(double decMotorSign) {
            var mount = PolledGemNearThePole(-88.0, latitudeDeg: -SiteLatDeg);
            mount.DecMotorSign = decMotorSign;
            var phiBefore = mount.TruePhiDeg;
            var store = TenDegreeStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = -SiteLatDeg });
            using var svc = NewGemService(mount, new WsRecorder(), store);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(preflight.MoveToStart, Is.Not.Null, "within 3° of the south pole: the move is offered");
            AssertArrivedOnItsSide(result, mount, phiBefore);
            Assert.That(mount.TrueSky.DecDeg, Is.LessThan(-85.0), "near the south pole, not the north");
        }

        // ── the second move and the end check ────────────────────────────────────────────────

        [Test]
        public async Task A_second_move_that_goes_toward_the_pole_is_stopped_and_fails_as_turn_wrong_way_and_is_never_reversed() {
            var mount = PolledGemNearThePole();
            var flipped = false;
            mount.OnPoseRead = () => {
                // The Dec motor turns the other way from the probe's lesson once the second move runs.
                if (!flipped && DecMoves(mount).Length == 2) {
                    flipped = true;
                    mount.DecMotorSign = -mount.DecMotorSign;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            var dec = DecMoves(mount);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnWrongWay), result.Message);
                Assert.That(dec, Has.Length.EqualTo(2), "no move after the wrong way. " + mount.Describe());
                Assert.That(dec.Select(m => m.End), Has.All.EqualTo(MotionEnd.Stopped), "stopped, never reversed");
                Assert.That(mount.IsMoving, Is.False);
                Assert.That(mount.MotionOwner, Is.Null);
            });
        }

        [Test]
        public async Task A_move_that_ends_outside_2_to_5_degrees_from_the_pole_fails_as_turn_mismatch() {
            var mount = PolledGemNearThePole();
            var shifted = false;
            mount.OnFullRead = () => {
                // Once the second move's motor stopped, the Dec reading jumps 2.5° toward the pole: it ends 1.5° from it.
                if (!shifted && DecMoves(mount) is { Length: 2 } dec && dec[1].End != MotionEnd.Running) {
                    shifted = true;
                    mount.ReportedDecOffsetDeg = 2.5;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnMismatch), result.Message);
                Assert.That(result.Message, Does.Contain("not 2–5°"));
                Assert.That(mount.IsMoving, Is.False);
            });
        }

        // ── more refusals ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Move_to_start_is_refused_on_an_alt_az_mount_and_nothing_moves() {
            var mount = NewMount();
            var service = mount.As<ITelescopeService>();
            service.Setup(t => t.MountAlignmentMode).Returns(ASCOM.Common.DeviceInterfaces.AlignmentMode.AltAz);
            service.Setup(t => t.ReadNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => FreshReading(0.0, 88.0));
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart!.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.AltAzMount));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignAltAzMount));
            });
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsNotIn(0.0)), Times.Never, "nothing moved");
            service.Verify(t => t.MoveAxisAsync(It.IsAny<int>(), It.IsNotIn(0.0), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Move_to_start_is_refused_with_no_site_set_and_nothing_moves() {
            var mount = PolledGemNearThePole();
            var store = TenDegreeStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = 0.0, LongitudeDeg = 0.0 });
            using var svc = NewGemService(mount, new WsRecorder(), store);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.MoveToStartAsync(CancellationToken.None));

            Assert.Multiple(() => {
                Assert.That(preflight.MoveToStart?.BlockedBy, Is.EqualTo(PolarAlignMoveToStartBlocks.SiteLatitude));
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignSiteNotSet));
                Assert.That(mount.Motions, Is.Empty, mount.Describe());
            });
        }
    }
}
