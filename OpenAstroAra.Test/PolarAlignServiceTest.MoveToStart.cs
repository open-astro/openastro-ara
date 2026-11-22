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
        private static FakeGemMount PolledGemNearThePole(double decDeg = 88.0) {
            var mount = FakeGemMount.CounterweightDownAt(2.0, decDeg, lstHours: 20.0, clock: new ManualMountClock());
            mount.FreshReadTakes = FreshRead;
            return mount;
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
                    mount.MoveByOtherMeans(pose with { MechHaDeg = pose.MechHaDeg + 1.0 });
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
    }
}
