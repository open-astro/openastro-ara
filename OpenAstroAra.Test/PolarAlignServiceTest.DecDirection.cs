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

        [Test]
        public void The_daemon_waits_10_s_for_a_still_pier_side_after_a_probe_next_to_the_pole_and_at_most_30_s() {
            var mount = PolledGemNearThePole(90.0);
            using var daemon = new PolarAlignService(NewFake(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PolarAlignService>.Instance,
                new TrueSkySolver(mount), mount, MediatorMotionControl.For(mount), NewStore());

            Assert.That(daemon.PoleReadingQuietFor, Is.EqualTo(TimeSpan.FromSeconds(10)), "longer than the HAE29C's 6 s");
            Assert.That(daemon.PoleReadingWaitMax, Is.EqualTo(TimeSpan.FromSeconds(30)));
        }

        [Test]
        public async Task From_Home_a_probe_over_the_pole_read_6_s_late_comes_back_and_the_move_arrives_on_its_side() {
            var mount = PolledGemNearThePole(90.0);
            mount.DecMotorSign = -1.0; // a positive rate raises the Dec axis: the probe goes over the pole from Home
            mount.RaAndSideLag = RigPierSideLag;
            var phiBefore = mount.TruePhiDeg;
            var store = TenDegreeStore();
            using var svc = NewGemService(mount, new WsRecorder(), store);
            svc.PoleReadingQuietFor = TimeSpan.FromSeconds(10); // the daemon's: longer than the 6 s lag

            var result = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(result.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), result.Message + " " + mount.Describe());
                Assert.That(OffOwnPoleDeg(mount), Is.InRange(3.9, 4.5), "inside the 2–5° start band");
                Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide), "it ends on the side of the pier it started on");
                Assert.That(mount.Motions, Has.All.Property(nameof(MotionEntry.Kind)).EqualTo(MotionKind.DecMotor), "Dec alone. " + mount.Describe());
                // The RA axis only tracked: 15″/s over the two waits at the pole (about 30 s) is under 0.15°.
                Assert.That(mount.TruePhiDeg, Is.EqualTo(phiBefore).Within(0.2), "the counterweights stay: the RA axis did not turn");
                Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Not.Null, "the probe's lesson is saved once the move arrived");
            });
            mount.AssertEndedStopped(tracking: true);
        }

        [Test]
        public async Task A_saved_Dec_direction_moves_off_the_pole_from_Home_with_no_probe_behind_a_6_s_pier_side_lag() {
            // The first move (from Dec 88°, as rig run A) learns the direction; a later move from Home uses it: one Dec
            // move, straight off the pole, never over it.
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecMotorSign = -1.0;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                var learned = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
                Assert.That(learned.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), learned.Message);
            }
            Assert.That(store.GetPolarAlignDecMotorDirection(), Is.Not.Null);
            var mount = PolledGemNearThePole(90.0);
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
            Assert.That(mount.TrueSide, Is.EqualTo(mount.InitialTrueSide), "never over the pole");
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
        public async Task From_Home_a_wrong_saved_Dec_direction_is_caught_0_3_degrees_over_the_pole_and_forgotten() {
            // #2189 review: from Home the Dec reading falls whichever way the axis goes, and the pass over the pole shows
            // 6 s late. A saved direction is proved on a 0.3° first leg and the wait before the rest of the move, so a
            // wrong one stops 0.3° over the pole (never 4°), fails, and is forgotten.
            var store = TenDegreeStore();
            var first = PolledGemNearThePole(88.0);
            first.DecMotorSign = -1.0;
            using (var svc = NewGemService(first, new WsRecorder(), store)) {
                var learned = await svc.MoveToStartAsync(MoveConfirmed, CancellationToken.None).ConfigureAwait(false);
                Assert.That(learned.Outcome, Is.EqualTo(PolarAlignMoveToStartOutcomes.Arrived), learned.Message);
            }
            var mount = PolledGemNearThePole(90.0);
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
                Assert.That(OffOwnPoleDeg(mount), Is.LessThanOrEqualTo(PolarAlignService.MoveToStartProbeDeg + 0.1), "0.3° over the pole at most");
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
