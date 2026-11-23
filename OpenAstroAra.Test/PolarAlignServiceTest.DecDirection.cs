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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2166 rig check, run C (HAE29C, AlpacaBridge 6.0.0-beta.4, 2026-11-23): from Home the 0.3° Dec probe went over the
    /// pole, but the mount's pier side and RA reading showed it ~6 s late, so the probe read as "away" and the move went on
    /// over the pole until pier_changed stopped it. A probe that starts next to the pole now waits for the pier side and
    /// RA reading to stand still, goes back past the pole when it went over, and the direction a probe learned is saved
    /// for the mount and driver, so later moves need no probe at all.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>How late the HAE29C's pier side and RA reading showed the pole crossing.</summary>
        private static readonly TimeSpan RigPierSideLag = TimeSpan.FromSeconds(6);

        /// <summary>The scope's true side when the first Dec move has ended (read at the next full reading): from within 0.5°
        /// of the pole, the side the first leg left the pole on.</summary>
        private static Func<PierSide?> SideAfterTheFirstLeg(FakeGemMount mount) {
            PierSide? side = null;
            var before = mount.OnFullRead;
            mount.OnFullRead = () => {
                before?.Invoke();
                if (side is null && DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    side = mount.TrueSide;
                }
            };
            return () => side;
        }

        /// <summary>Arrived 2–5° (about 4°) from the pole, on the side the first leg left it on, by Dec moves alone; the RA axis
        /// only tracked (15″/s over the waits at the pole is under 0.2°).</summary>
        private static void AssertLeftThePoleAndArrived(PolarAlignMoveToStartResultDto result, FakeGemMount mount, PierSide? sideAfterLeaving, double phiBefore) {
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(3.9, 4.5), "4° from the pole");
                Assert.That(sideAfterLeaving, Is.Not.Null, mount.Describe());
                Assert.That(mount.TrueSide, Is.EqualTo(sideAfterLeaving), "never over the pole after the first leg");
                Assert.That(mount.Motions, Has.All.Property(nameof(MotionEntry.Kind)).EqualTo(MotionKind.DecMotor), "Dec alone. " + mount.Describe());
                Assert.That(mount.TruePhiDeg, Is.EqualTo(phiBefore).Within(0.2), "the counterweights stay: the RA axis did not turn");
                Assert.That(mount.MotionOwner, Is.Null);
            });
        }

        [Test]
        public void The_daemon_waits_10_s_for_a_still_pier_side_after_a_probe_next_to_the_pole_and_at_most_30_s() {
            var mount = PolledGemNearThePole(90.0);
            using var daemon = new PolarAlignService(NewFake(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PolarAlignService>.Instance,
                new TrueSkySolver(mount), mount, MediatorMotionControl.For(mount), NewStore());

            Assert.That(daemon.PoleReadingQuietFor, Is.EqualTo(TimeSpan.FromSeconds(10)), "longer than the HAE29C's 6 s");
            Assert.That(daemon.PoleReadingWaitMax, Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        [Test]
        public async Task From_Home_behind_a_6_s_pier_side_lag_the_first_leg_leaves_the_pole_and_the_move_arrives_4_degrees_off_it() {
            // Rig run C (2026-11-23): from Home the first move went over the pole and the pier side showed it ~6 s late. The
            // first leg now only leaves the pole; after the wait the reported side is the start's, and the probe from 0.5°
            // off cannot cross the pole.
            var mount = PolledGemNearThePole(90.0);
            mount.DecMotorSign = -1.0; // a positive rate raises the Dec axis: over the pole from Home
            mount.RaAndSideLag = RigPierSideLag;
            var phiBefore = mount.TruePhiDeg;
            var sideAfterLeaving = SideAfterTheFirstLeg(mount);
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, new WsRecorder(), store);
            svc.PoleReadingQuietFor = TimeSpan.FromSeconds(10); // the daemon's: longer than the 6 s lag

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            AssertLeftThePoleAndArrived(result, mount, sideAfterLeaving(), phiBefore);
            Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Not.Null, "the probe's lesson is saved once the move arrived");
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task A_saved_Dec_direction_moves_off_the_pole_from_0_7_degrees_with_no_probe_behind_a_6_s_pier_side_lag() {
            // The first move (from Dec 88°, as rig run A) learns the direction; a later move from 0.7° off the pole, on the same
            // side, uses it: a 0.3° first leg the saved way, the wait that proves it, then the rest. No probe, never over it.
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecMotorSign = -1.0;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                var learned = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
                Assert.That(learned.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), learned.Message);
            }
            Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Not.Null);
            var mount = PolledGemNearThePole(89.3);
            mount.DecMotorSign = -1.0;
            mount.RaAndSideLag = RigPierSideLag;
            var phiBefore = mount.TruePhiDeg;
            using var again = NewGemService(mount, new WsRecorder(), store);
            again.PoleReadingQuietFor = TimeSpan.FromSeconds(10); // the daemon's: the first leg's wait outlasts the 6 s lag

            var result = await again.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            AssertArrivedOnItsSide(result, mount, phiBefore);
            var moves = DecMoves(mount);
            Assert.That(moves, Has.Length.EqualTo(2), "the saved direction: a 0.3° first leg, the wait, then the rest. " + mount.Describe());
            Assert.That(Math.Sign(moves[0].Rate), Is.EqualTo(Math.Sign(moves[1].Rate)), "both legs go the saved way: no probe, no turn back");
        }

        [Test]
        public async Task A_saved_Dec_direction_that_turns_out_wrong_fails_as_turn_wrong_way_and_is_forgotten() {
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecMotorSign = -1.0;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
            }
            Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Not.Null);
            var mount = PolledGemNearThePole(88.0);
            mount.DecMotorSign = 1.0; // the motor now turns the other way from the saved direction
            using var again = NewGemService(mount, new WsRecorder(), store);

            var result = await again.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message);
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnWrongWay), result.Message + " " + mount.Describe());
                Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Null, "forgotten, so the next move probes again");
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "stopped, never reversed. " + mount.Describe());
            });
        }

        [Test]
        public async Task Half_a_degree_from_the_pole_a_wrong_saved_Dec_direction_is_caught_on_its_first_leg_and_forgotten() {
            // #2189 review: from 0.5° off the pole a wrong 0.3° first leg goes toward the pole and stops short of it, under
            // the 0.5° wrong-way stop and with no crossing. The leg must show 0.15° away, or the move fails there.
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecMotorSign = -1.0;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                var learned = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
                Assert.That(learned.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), learned.Message);
            }
            var mount = PolledGemNearThePole(89.5);
            mount.DecMotorSign = 1.0; // the motor now turns the other way from the saved direction
            mount.RaAndSideLag = RigPierSideLag;
            using var again = NewGemService(mount, new WsRecorder(), store);
            again.PoleReadingQuietFor = TimeSpan.FromSeconds(10);

            var result = await again.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message + " " + mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnWrongWay), result.Message);
                Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Null, "forgotten, so the next move probes again");
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "only the 0.3° first leg moved. " + mount.Describe());
                Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide), "never over the pole");
            });
        }

        [Test]
        public async Task A_pier_side_and_RA_reading_that_never_stand_still_after_a_probe_at_the_pole_fail_after_30_s() {
            // #2189 review: the wait at the pole is bounded. After the leg that leaves the pole the RA reading steps 1.2° every
            // 6 s (a driver whose pointing model shifts next to the pole): never still for 10 s, and small on the sky (ΔRA ×
            // cos Dec, 0.7° off the pole), so the RA guard does not trip first. After 30 s the move fails turn_mismatch with
            // the Dec motor stopped.
            var mount = PolledGemNearThePole(90.0);
            TimeSpan? since = null;
            mount.OnFullRead = () => {
                if (DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    since ??= mount.Now;
                    mount.RaJumpHours = 0.08 * Math.Floor((mount.Now - since.Value).TotalSeconds / 6.0);
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());
            svc.PoleReadingQuietFor = TimeSpan.FromSeconds(10);
            svc.PoleReadingWaitMax = TimeSpan.FromSeconds(30);

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message + " " + mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnMismatch), result.Message);
                Assert.That(result.Message, Does.Contain("did not stand still within 30 s"));
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "only the probe moved. " + mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
                Assert.That(mount.MotionOwner, Is.Null, "the motion owner is given back");
            });
        }

        // ── #2189 rig check: a sky-relative Dec rate (AlpacaBridge iOptron: positive is north on either side) ──────────

        /// <summary>The scope's pier side as a saved direction names it.</summary>
        private static string SideName(PierSide side) => side == PierSide.pierWest ? "west" : "east";

        [Test]
        public async Task Rig_run_1_from_Home_with_no_saved_direction_leaves_the_pole_on_the_mounts_side_and_arrives_4_degrees_off_it() {
            // Rig 2026-11-23 run 1: from Home a positive rate stayed on the mount's side, 0.4° off the pole and steady, and the
            // old move read that as "away", went on at the same sign (north: toward the pole) and over it to pier_changed.
            var mount = PolledGemNearThePole(90.0);
            mount.DecRateSkyRelative = true;
            mount.PoleMovePicksOver = false;
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, new WsRecorder(), store);
            svc.PoleReadingQuietFor = TimeSpan.FromSeconds(10);

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(3.9, 4.5), "4° from the pole");
                Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide), "the side the first leg left the pole on");
                Assert.That(DecMoves(mount), Has.Length.EqualTo(3), "leave the pole, the probe, the move off it. " + mount.Describe());
                Assert.That(store.GetPolarAlignDecMotorDirection()?.TowardPoleSign, Is.EqualTo(1), "a positive rate is north: toward the pole");
            });
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task Rig_run_2_from_Dec_88_probes_toward_the_pole_arrives_and_saves_positive_toward_the_pole_on_its_side() {
            var mount = PolledGemNearThePole(88.0);
            mount.DecRateSkyRelative = true;
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, new WsRecorder(), store);

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            var dec = DecMoves(mount);
            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(3.9, 4.5));
                Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide));
                Assert.That(dec, Has.Length.EqualTo(2), "the probe, then the move off the pole. " + mount.Describe());
                Assert.That(dec[0].Rate, Is.GreaterThan(0), "the probe at a positive rate (north: toward the pole)");
                Assert.That(dec[1].Rate, Is.LessThan(0), "then the other way");
                var saved = store.GetPolarAlignDecMotorDirection();
                Assert.That(saved?.TowardPoleSign, Is.EqualTo(1));
                Assert.That(saved?.PierSide, Is.EqualTo(SideName(mount.InitialTrueSide)));
            });
        }

        [Test]
        public async Task Rig_run_3_from_Home_with_a_saved_direction_leaves_the_pole_onto_the_other_side_and_arrives_there_never_crossing_after() {
            // Rig 2026-11-23 run 3: the direction saved on one side (run 2), then from Home the mount took the first move over
            // to the other side, and the old move called the saved direction wrong (turn_wrong_way 0.39° over the pole).
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecRateSkyRelative = true;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                var learned = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
                Assert.That(learned.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), learned.Message);
            }
            var mount = PolledGemNearThePole(90.0);
            mount.DecRateSkyRelative = true;
            mount.PoleMovePicksOver = true;
            mount.RaAndSideLag = RigPierSideLag;
            PierSide? afterLeaving = null;
            mount.OnFullRead = () => {
                if (afterLeaving is null && DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    afterLeaving = mount.TrueSide;
                }
            };
            using var again = NewGemService(mount, new WsRecorder(), store);
            again.PoleReadingQuietFor = TimeSpan.FromSeconds(10);

            var result = await again.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(afterLeaving, Is.Not.EqualTo(mount.InitialTrueSide), "the mount took the first leg over the pole");
                Assert.That(mount.TrueSide, Is.EqualTo(afterLeaving), "never over the pole after the first leg");
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(3.9, 4.5), "4° from the pole");
            });
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task A_probe_from_half_a_degree_off_the_pole_that_reads_over_it_fails_as_pier_changed() {
            // From 0.5° off the pole a 0.3° probe cannot cross it; a reading that says it did (the RA reading flipped by 12 h
            // and the pier side with it) is a mount that did not do what it was told: the move fails, never flips the mount.
            var mount = PolledGemNearThePole(89.5);
            var flipped = false;
            mount.OnFullRead = () => {
                if (!flipped && DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    flipped = true;
                    mount.RaJumpHours = 12.0;
                    mount.ReportedSideOverride = mount.InitialTrueSide == PierSide.pierEast ? PierSide.pierWest : PierSide.pierEast;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message + " " + mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.PierChanged), result.Message);
                Assert.That(result.Message, Does.Contain("the Dec probe went over the pole"));
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "no move after the probe. " + mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
            });
        }

        [Test]
        public async Task A_first_leg_from_Home_that_reads_under_0_4_degrees_off_the_pole_fails_as_turn_mismatch() {
            // After the leg that leaves the pole the reported Dec stays 0.2° from it (a reading that did not follow): the
            // move does not go on from a start that still has no side.
            var mount = PolledGemNearThePole(90.0);
            var shifted = false;
            mount.OnFullRead = () => {
                if (!shifted && DecMoves(mount).Length == 1 && !mount.IsMoving) {
                    shifted = true;
                    mount.ReportedDecOffsetDeg = 0.3;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message + " " + mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.TurnMismatch), result.Message);
                Assert.That(result.Message, Does.Contain("the first Dec move left the scope"));
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), "nothing moves after the leg. " + mount.Describe());
            });
        }

        [Test]
        public async Task An_RA_reading_that_moves_during_the_leg_that_leaves_the_pole_fails_as_ra_moved() {
            // The leg that leaves the pole judges no side and no pass over the pole, but the RA axis must still not turn: an
            // RA reading that moves 80° 0.3° from the pole (0.4° on the sky) stops it.
            var mount = PolledGemNearThePole(90.0);
            var reads = 0;
            mount.OnPoseRead = () => {
                if (DecMoves(mount).Length == 1 && mount.IsMoving && Interlocked.Increment(ref reads) == 3) {
                    mount.RaJumpHours = 80.0 / 15.0;
                }
            };
            using var svc = NewGemService(mount, new WsRecorder());

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Failed), result.Message + " " + mount.Describe());
                Assert.That(result.Reason, Is.EqualTo(PolarAlignService.RaMoved), result.Message);
                Assert.That(DecMoves(mount), Has.Length.EqualTo(1), mount.Describe());
                Assert.That(mount.IsMoving, Is.False);
            });
        }

        /// <summary>The angle on the sky between two (RA, Dec) pointings, degrees.</summary>
        private static double SkyAngleDeg((double RaDeg, double DecDeg) a, (double RaDeg, double DecDeg) b) {
            static double R(double d) => d * Math.PI / 180.0;
            var cos = (Math.Sin(R(a.DecDeg)) * Math.Sin(R(b.DecDeg))) + (Math.Cos(R(a.DecDeg)) * Math.Cos(R(b.DecDeg)) * Math.Cos(R(a.RaDeg - b.RaDeg)));
            return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
        }

        [Test]
        public async Task A_start_just_off_the_pole_that_leaves_it_onto_the_other_side_stays_within_the_confirmed_reach() {
            // #2189 review: from 0.45° off the pole a sky-relative positive rate goes north, over the pole, and the move ends
            // 4° off it on the other side: 4.45° from where it started. The confirmed reach (the target plus 0.5°) covers it.
            var mount = PolledGemNearThePole(89.55);
            mount.DecRateSkyRelative = true;
            var start = mount.TrueSky;
            var farthest = 0.0;
            void Sample() => farthest = Math.Max(farthest, SkyAngleDeg(start, mount.TrueSky));
            mount.OnPoseRead = Sample;
            mount.OnFullRead = Sample;
            using var svc = NewGemService(mount, new WsRecorder());
            svc.PoleReadingQuietFor = TimeSpan.FromSeconds(10);
            var offer = (await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false)).MoveToStart!;

            var result = await svc.MoveToStartAsync(new PolarAlignMoveToStartConfirmation(offer.TargetOffPoleDeg, offer.ReachDeg), CancellationToken.None).ConfigureAwait(false);
            Sample();

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(mount.TrueSide, Is.Not.EqualTo(mount.InitialTrueSide), "the first leg went over the pole");
                Assert.That(farthest, Is.GreaterThan(4.3), "farther than the old 4.3° reach");
                Assert.That(farthest, Is.LessThanOrEqualTo(offer.ReachDeg + 0.05), "never past the reach the user confirmed");
            });
        }

        [Test]
        public void A_saved_Dec_direction_stands_only_for_the_pier_side_it_was_learned_on() {
            var saved = new PolarAlignDecMotorDirectionDto(1, "west", "hae29c-unique-id", "AlpacaBridge", "6.0.0", DateTimeOffset.UtcNow);

            Assert.Multiple(() => {
                Assert.That(saved.IsFor("hae29c-unique-id", "AlpacaBridge", "6.0.0", "west"), Is.True);
                Assert.That(saved.IsFor("hae29c-unique-id", "AlpacaBridge", "6.0.0", "east"), Is.False,
                    "a driver may map the Dec rate to the axis or to the sky: the other side probes again");
                Assert.That(saved.IsFor("hae29c-unique-id", "AlpacaBridge", "6.0.1", "west"), Is.False, "another driver version");
            });
        }

        [Test]
        public void Test_direction_again_forgets_the_saved_Dec_direction_too() {
            var store = TenDegreeStore();
            store.PutPolarAlignDecMotorDirection(new PolarAlignDecMotorDirectionDto(1, "west", "hae29c-unique-id", string.Empty, "6.0.0", DateTimeOffset.UtcNow));
            var idle = new Mock<IPolarAlignService>();
            idle.SetupGet(s => s.IsActive).Returns(false);

            EquipmentEndpoints.RetestPolarAlignMotorDirection(store, idle.Object);

            Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Null);
        }
    }
}
