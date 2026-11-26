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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>
    /// #2064 — the fake GEM mount's own physics, reporting and ledger, on a hand-moved clock. Every expected number is
    /// worked out from #2048's definitions in the test (an hour angle, a rate times a time), never read back from the fake.
    /// </summary>
    [TestFixture]
    public class FakeGemMountTest {

        private const double Lst = 20.0;
        private static readonly string[] ForbiddenMembers = {
            "Sync", "Sync", "MeridianFlip", "Action telescope:setpierside", "CommandString", "CommandBool", "CommandBlind",
        };
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        private static readonly bool[] HonouredThenIgnored = { true, false };

        private static (FakeGemMount Mount, ManualMountClock Clock) At(double haHours, double decDeg, bool tracking = false, double latitudeDeg = 45.0) {
            var clock = new ManualMountClock();
            return (FakeGemMount.CounterweightDownAt(haHours, decDeg, Lst, latitudeDeg, tracking, clock), clock);
        }

        private static void Run(FakeGemMount mount, ManualMountClock clock, TimeSpan by) {
            clock.Advance(by);
            mount.Integrate();
        }

        private static double ReportedRaDeg(FakeGemMount mount) => mount.GetCurrentPosition().RADegrees;

        // ── geometry ───────────────────────────────────────────────────────────────────────

        // hour angle (h) → true φ (#2048: 90° − the distance to the nearer of 0 h and 12 h) and the counterweight-down side
        [TestCase(6.0, 0.0, PierSide.pierEast)]
        [TestCase(4.0, 30.0, PierSide.pierEast)]
        [TestCase(0.0, 90.0, PierSide.pierEast)]
        [TestCase(-4.0, 30.0, PierSide.pierWest)]
        [TestCase(-6.0, 0.0, PierSide.pierWest)]
        [TestCase(-10.0, 60.0, PierSide.pierWest)]
        [TestCase(11.0, 75.0, PierSide.pierEast)]
        public void FakeGemMount_poses_a_counterweight_down_start_by_the_hour_angle(double haHours, double phiDeg, PierSide side) {
            var (mount, _) = At(haHours, 85.0);
            Assert.That(mount.TruePhiDeg, Is.EqualTo(phiDeg).Within(1e-9));
            Assert.That(mount.TrueSide, Is.EqualTo(side));
            Assert.That(mount.GetInfo().SideOfPier, Is.EqualTo(side), "the hour-angle model agrees on a counterweight-down pose");
            Assert.That(GemGeometry.Wrap180(mount.GetInfo().SiderealTime * 15.0 - ReportedRaDeg(mount)), Is.EqualTo(haHours * 15.0).Within(1e-6));
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(85.0).Within(1e-9));
        }

        [Test]
        public void FakeGemMount_tracking_turns_the_RA_axis_at_the_sidereal_rate_and_holds_the_reported_RA() {
            var (mount, clock) = At(-4.0, 85.0, tracking: true);
            var ha0 = mount.TruePose.MechHaDeg;
            var ra0 = ReportedRaDeg(mount);
            Run(mount, clock, TimeSpan.FromMinutes(10));
            Assert.That(GemGeometry.Wrap180(mount.TruePose.MechHaDeg - ha0), Is.EqualTo(600 * 360.98564736629 / 86400.0).Within(1e-6));
            Assert.That(GemGeometry.Wrap180(ReportedRaDeg(mount) - ra0), Is.EqualTo(0.0).Within(1e-6), "a tracking mount keeps its RA");

            mount.SetTrackingEnabled(false);
            var ha1 = mount.TruePose.MechHaDeg;
            Run(mount, clock, TimeSpan.FromMinutes(10));
            Assert.That(mount.TruePose.MechHaDeg, Is.EqualTo(ha1).Within(1e-9), "tracking off: the axis stands");
            Assert.That(GemGeometry.Wrap180(ReportedRaDeg(mount) - ra0), Is.EqualTo(600 * 360.98564736629 / 86400.0).Within(1e-6), "and the RA reading drifts east");
        }

        // ── motor model ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task FakeGemMount_motor_move_stops_when_its_lease_lapses() {
            var (mount, clock) = At(-4.0, 85.0);
            var ha0 = mount.TruePose.MechHaDeg;
            await mount.MoveAxisAsync(0, 2.0, leaseMs: 1000, CancellationToken.None);

            Run(mount, clock, TimeSpan.FromMilliseconds(600));
            Assert.That(mount.IsMoving, Is.True);
            await mount.MoveAxisAsync(0, 2.0, leaseMs: 1000, CancellationToken.None); // renewed at 0.6 s: runs to 1.6 s
            Run(mount, clock, TimeSpan.FromMilliseconds(900));
            Assert.That(mount.IsMoving, Is.True, "the renewal kept it going");
            Run(mount, clock, TimeSpan.FromSeconds(5));

            Assert.That(mount.IsMoving, Is.False);
            // A positive rate raises the RA reading: the hour angle falls 2°/s for the 1.6 s the lease allowed.
            Assert.That(GemGeometry.Wrap180(mount.TruePose.MechHaDeg - ha0), Is.EqualTo(-3.2).Within(1e-6));
            var move = mount.Motions.Single();
            Assert.That((move.Kind, move.End, move.EndedAt), Is.EqualTo((MotionKind.RaMotor, MotionEnd.LeaseLapsed, (TimeSpan?)TimeSpan.FromMilliseconds(1600))));
            Assert.That(mount.Stops.Single().Kind, Is.EqualTo(StopKind.LeaseStop));
        }

        [Test]
        public async Task FakeGemMount_motor_move_without_a_lease_runs_until_its_stop() {
            var (mount, clock) = At(-4.0, 85.0);
            await mount.MoveAxisAsync(0, -1.5, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(10));
            await mount.MoveAxisAsync(0, 0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(10));

            Assert.That(mount.Motions.Single().TrueDeltaHaDeg, Is.EqualTo(15.0).Within(1e-6), "−1.5°/s for 10 s: the hour angle grows 15°");
            Assert.That(mount.Motions.Single().End, Is.EqualTo(MotionEnd.Stopped));
            Assert.That(mount.TotalTrueRaSweepDeg, Is.EqualTo(15.0).Within(1e-6));
        }

        [Test]
        public async Task FakeGemMount_ra_motor_sign_minus_one_turns_the_axis_the_other_way() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.RaMotorSign = -1.0;
            var ra0 = ReportedRaDeg(mount);
            await mount.MoveAxisAsync(0, 1.0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(5));

            Assert.That(mount.Motions.Single().TrueDeltaHaDeg, Is.EqualTo(5.0).Within(1e-6));
            Assert.That(GemGeometry.Wrap180(ReportedRaDeg(mount) - ra0), Is.EqualTo(-5.0 + (5.0 * GemGeometry.SiderealDegPerSecond)).Within(1e-6),
                "the RA reading falls for a positive rate (less the sky's 5 s turn)");
        }

        [Test]
        public async Task FakeGemMount_renewal_after_an_abort_is_refused_and_nothing_restarts() {
            var (mount, clock) = At(-4.0, 85.0);
            var epoch = mount.AbortEpoch;
            await mount.MoveAxisAsync(0, 2.0, 3000, epoch, CancellationToken.None);
            Run(mount, clock, OneSecond);
            await mount.AbortSlewAsync("user_request", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveAxisAsync(0, 2.0, 3000, epoch, CancellationToken.None));
            Run(mount, clock, OneSecond);
            Assert.That(mount.IsMoving, Is.False);
            Assert.That(mount.Motions.Select(m => m.End), Is.EqualTo(new[] { MotionEnd.Aborted, MotionEnd.Refused }));
        }

        [Test]
        public async Task FakeGemMount_dec_drift_moves_the_dec_axis_during_an_RA_turn() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.DecDriftDegPerSec = 0.2;
            await mount.MoveAxisAsync(0, 1.0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(5));

            // HA −4 h is through the pole: Dec axis 180° − 85° = 95°, and a rising axis angle lowers the pointing's Dec.
            Assert.That(mount.TruePose.DecAxisDeg, Is.EqualTo(96.0).Within(1e-6));
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(84.0).Within(1e-6));
        }

        // ── firmware GoTo ──────────────────────────────────────────────────────────────────

        private static Coordinates AtHourAngle(double haHours, double decDeg) =>
            new(GemGeometry.Wrap360((Lst - haHours) * 15.0), decDeg, Epoch.JNOW, Coordinates.RAType.Degrees);

        private static async Task<bool> GoToAsync(FakeGemMount mount, ManualMountClock clock, Coordinates target) {
            var slew = mount.SlewToCoordinatesAsync(target, MoveAsker.Session, CancellationToken.None);
            for (var i = 0; i < 600 && !slew.IsCompleted; i++) {
                Run(mount, clock, OneSecond);
                await Task.Yield();
            }
            return await slew.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // target hour angle (h) → the side the firmware takes and the true φ it lands at
        [TestCase(2.0, PierSide.pierEast, 60.0)]
        [TestCase(5.0, PierSide.pierEast, 15.0)]
        [TestCase(-2.0, PierSide.pierWest, 60.0)]
        [TestCase(-5.0, PierSide.pierWest, 15.0)]
        public async Task FakeGemMount_GoTo_takes_the_counterweight_down_side_by_the_target_hour_angle_sign(double haHours, PierSide side, double phiDeg) {
            var (mount, clock) = At(6.0, 90.0); // Home
            Assert.That(await GoToAsync(mount, clock, AtHourAngle(haHours, 60.0)), Is.True);

            Assert.That(mount.TrueSide, Is.EqualTo(side));
            Assert.That(mount.TruePhiDeg, Is.EqualTo(phiDeg).Within(1e-6));
            Assert.That(GemGeometry.Pointing(mount.TruePose, south: false).HaDeg, Is.EqualTo(haHours * 15.0).Within(1e-6));
            var goTo = mount.Motions.Single();
            Assert.That((goTo.Kind, goTo.End), Is.EqualTo((MotionKind.GoTo, MotionEnd.Arrived)));
            Assert.That(goTo.TruePhiMaxDeg, Is.EqualTo(phiDeg).Within(1e-6), "φ rises steadily from Home: its largest is where it lands");
            Assert.That(mount.LimitHit, Is.False);
        }

        [Test]
        public async Task FakeGemMount_GoTo_uses_the_mounts_own_clock() {
            var (mount, clock) = At(6.0, 90.0);
            mount.MountClockOffsetHours = 2.0; // the mount thinks LST is 22 h
            Assert.That(await GoToAsync(mount, clock, AtHourAngle(2.0, 60.0)), Is.True);

            // The firmware aims at HA 22 − RA = 4 h: it lands counterweight-down at the hour angle IT computed.
            Assert.That(GemGeometry.Pointing(mount.TruePose, south: false).HaDeg, Is.EqualTo(60.0).Within(1e-6));
            Assert.That(GemGeometry.Wrap180(ReportedRaDeg(mount) - mount.TrueSky.RaDeg), Is.EqualTo(30.0).Within(1e-6),
                "it reports RA 2 h east of where it really points");
        }

        [Test]
        public async Task FakeGemMount_soft_limit_refuses_a_GoTo_and_sets_limit_hit() {
            var (mount, clock) = At(6.0, 90.0);
            mount.Limits = new SoftLimits(MaxAbsHourAngleHours: 3.0);
            var before = mount.TruePose;

            Assert.That(await GoToAsync(mount, clock, AtHourAngle(4.0, 60.0)), Is.False);

            Assert.That(mount.LimitHit, Is.True);
            Assert.That(mount.TruePose, Is.EqualTo(before), "nothing moved");
            var refused = mount.Motions.Single();
            Assert.That((refused.Refused, refused.End), Is.EqualTo((true, MotionEnd.Refused)));
            Assert.That(refused.RefusedReason, Does.Contain("hour angle"));
            Assert.That(mount.CommonViolations(endEventIndex: null, limitDeg: 60.0, expectLimitHit: true), Is.Empty, "M33: limit_hit is the expected one");
        }

        [Test]
        public async Task FakeGemMount_soft_limit_on_altitude_refuses_a_GoTo_below_it() {
            var (mount, clock) = At(6.0, 90.0);
            mount.Limits = new SoftLimits(MinAltitudeDeg: 10.0);
            // HA 6 h, Dec 0 at latitude 45°: on the horizon.
            Assert.That(await GoToAsync(mount, clock, AtHourAngle(6.0, 0.0)), Is.False);
            Assert.That(mount.LimitHit, Is.True);
            Assert.That(mount.LimitReason, Does.Contain("altitude"));
        }

        [Test]
        public async Task FakeGemMount_soft_limit_stops_a_running_motor_move_and_sets_limit_hit() {
            var (mount, clock) = At(4.0, 85.0); // φ 30°, the normal side
            mount.Limits = new SoftLimits(MaxCounterweightDeg: 45.0);
            // A positive rate lowers the hour angle toward the meridian: φ rises 1° per degree.
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(20));

            Assert.That(mount.IsMoving, Is.False);
            Assert.That(mount.LimitHit, Is.True);
            Assert.That(mount.TruePhiDeg, Is.EqualTo(45.0).Within(2.0 * 0.01 + 1e-9), "stopped within one 10 ms step of the limit");
            Assert.That(mount.TruePhiDeg, Is.LessThanOrEqualTo(45.0 + 1e-9));
            Assert.That(mount.Motions.Single().End, Is.EqualTo(MotionEnd.LimitHit));
        }

        [Test]
        public async Task FakeGemMount_ms2_fallback_lands_a_refused_GoTo_counterweight_up() {
            var (mount, clock) = At(6.0, 90.0);
            mount.Limits = new SoftLimits(MaxAbsHourAngleHours: 3.0);
            mount.Ms2Fallback = true;

            Assert.That(await GoToAsync(mount, clock, AtHourAngle(4.0, 80.0)), Is.True, "the re-aimed GoTo arrives");

            // Counterweight-down would be (HA 60°, Dec axis 80°); :MS2# takes mechanical HA + 12 h, Dec axis 180° − 80°.
            Assert.That(mount.TruePose.MechHaDeg, Is.EqualTo(-120.0).Within(1e-6));
            Assert.That(mount.TruePose.DecAxisDeg, Is.EqualTo(100.0).Within(1e-6));
            Assert.That(mount.TruePhiDeg, Is.EqualTo(150.0).Within(1e-6), "180° − 30°: counterweight up");
            Assert.That(GemGeometry.Pointing(mount.TruePose, south: false).HaDeg, Is.EqualTo(60.0).Within(1e-6), "at the target all the same");
            Assert.That(mount.LimitHit, Is.True, "the firmware refused it first");
            Assert.That(mount.Motions.Select(m => m.Kind), Is.EqualTo(new[] { MotionKind.GoTo, MotionKind.GoToCounterweightUp }));
            Assert.That(mount.GetInfo().SideOfPier, Is.EqualTo(PierSide.pierEast), "the hour-angle model cannot see it");
            Assert.That(mount.TrueSide, Is.EqualTo(PierSide.pierWest));
            var violations = mount.CommonViolations(endEventIndex: null, limitDeg: 60.0);
            Assert.That(violations, Has.Some.Contains("max_true_phi"));
            Assert.That(violations, Has.Some.Contains("pier side changed"));
        }

        [Test]
        public async Task FakeGemMount_GoTo_while_a_motor_runs_is_sent_while_slewing() {
            var (mount, clock) = At(6.0, 90.0);
            await mount.MoveAxisAsync(1, 1.0, leaseMs: null, CancellationToken.None);
            var slew = mount.SlewToCoordinatesAsync(AtHourAngle(5.0, 80.0), MoveAsker.Session, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromMinutes(1));
            await mount.MoveAxisAsync(1, 0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromMinutes(1));
            await slew.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(mount.Motions.Single(m => m.Kind == MotionKind.GoTo).SentWhileSlewing, Is.True);
            Assert.That(mount.CommonViolations(null, 90.0), Has.Some.Contains("while the mount was moving"));
        }

        // ── reporting ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task FakeGemMount_cache_ms_delays_position_and_Slewing() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.DriverCache = TimeSpan.FromMilliseconds(1000);
            var first = await mount.ReadPointingNowAsync(CancellationToken.None);
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);

            Run(mount, clock, TimeSpan.FromMilliseconds(500));
            Assert.That(await mount.ReadPointingNowAsync(CancellationToken.None), Is.EqualTo(first), "still the cached position");
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.False, "and the cached Slewing, from before the move");
            var stale = await mount.ReadNowAsync(CancellationToken.None);
            Assert.That(stale!.ReadAtUtc, Is.EqualTo(clock.GetUtcNow() - TimeSpan.FromMilliseconds(500)), "the reading says how old it is");

            Run(mount, clock, TimeSpan.FromMilliseconds(600));
            var moved = await mount.ReadPointingNowAsync(CancellationToken.None);
            Assert.That((moved!.Value.RaHours - first!.Value.RaHours) * 15.0, Is.EqualTo(2.2 + (1.1 * GemGeometry.SiderealDegPerSecond)).Within(1e-6),
                "1.1 s at 2°/s, and the sky's turn");
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.True);

            await mount.MoveAxisAsync(0, 0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromMilliseconds(200));
            Assert.That(mount.IsMoving, Is.False);
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.True, "a stopped mount still reads as slewing until the cache refreshes");
            Run(mount, clock, TimeSpan.FromMilliseconds(900));
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task FakeGemMount_cache_ms_keeps_reporting_Slewing_after_a_move_ends_by_itself() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.DriverCache = TimeSpan.FromMilliseconds(1000);
            await mount.MoveAxisAsync(0, 2.0, leaseMs: 250, CancellationToken.None);
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.True);

            Run(mount, clock, TimeSpan.FromMilliseconds(500)); // the lease stopped it at 0.25 s
            Assert.That(mount.IsMoving, Is.False);
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.True, "the cache still says it moves");
            Run(mount, clock, TimeSpan.FromMilliseconds(600));
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task FakeGemMount_slewing_false_early_reads_false_while_the_mount_still_moves() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.SlewingFalseAfter = TimeSpan.FromMilliseconds(300);
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromMilliseconds(100));
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.True);
            Run(mount, clock, TimeSpan.FromMilliseconds(400));
            Assert.That(await mount.ReadSlewingNowAsync(CancellationToken.None), Is.False);
            Assert.That(mount.IsMoving, Is.True);
        }

        [Test]
        public void FakeGemMount_pier_source_decides_the_reported_side_of_a_counterweight_up_pose() {
            // The tube on its normal side pointing at HA −4 h: counterweight up (φ 150°).
            var clock = new ManualMountClock();
            var mount = new FakeGemMount(new GemPose(-60.0, 85.0), Lst, clock: clock);
            Assert.That(mount.TruePhiDeg, Is.EqualTo(150.0).Within(1e-9));
            Assert.That(mount.TrueSide, Is.EqualTo(PierSide.pierEast));

            Assert.That(mount.GetInfo().SideOfPier, Is.EqualTo(PierSide.pierWest), "the hour-angle model reads it as counterweight-down");
            mount.PierSource = PierSideSource.Mechanical;
            Assert.That(mount.GetInfo().SideOfPier, Is.EqualTo(PierSide.pierEast));
            mount.PierSource = PierSideSource.Unknown;
            Assert.That(mount.GetInfo().SideOfPier, Is.EqualTo(PierSide.pierUnknown));
        }

        [Test]
        public void FakeGemMount_destination_side_follows_its_mode() {
            var (mount, _) = At(6.0, 90.0);
            var west = AtHourAngle(-2.0, 70.0);
            Assert.That(mount.DestinationSideOfPier(west), Is.EqualTo(PierSide.pierWest));
            mount.Dsop = DestinationSideMode.Lie;
            Assert.That(mount.DestinationSideOfPier(west), Is.EqualTo(PierSide.pierEast));
            mount.Dsop = DestinationSideMode.NotImplemented;
            Assert.That(mount.DestinationSideOfPier(west), Is.EqualTo(PierSide.pierUnknown));
            mount.Dsop = DestinationSideMode.Error;
            Assert.Throws<InvalidOperationException>(() => mount.DestinationSideOfPier(west));
            mount.Dsop = DestinationSideMode.Mechanical;
            mount.Limits = new SoftLimits(MaxAbsHourAngleHours: 1.0);
            mount.Ms2Fallback = true;
            Assert.That(mount.DestinationSideOfPier(west), Is.EqualTo(PierSide.pierEast), "the :MS2# re-aim lands on the other side");
        }

        [Test]
        public void FakeGemMount_clock_and_site_errors_change_the_readings_not_the_pose() {
            var (mount, _) = At(-4.0, 85.0);
            var ra = ReportedRaDeg(mount);
            var pose = mount.TruePose;
            mount.MountClockOffsetHours = 2.0;
            mount.SiteLonErrDeg = 5.0;

            Assert.That(GemGeometry.Wrap180(ReportedRaDeg(mount) - ra), Is.EqualTo(30.0).Within(1e-6), "the mount's own LST is 2 h ahead");
            Assert.That(mount.GetInfo().SiderealTime, Is.EqualTo(Lst + (5.0 / 15.0)).Within(1e-6), "the daemon's LST is 5° ahead");
            Assert.That(mount.TruePose, Is.EqualTo(pose));
        }

        [Test]
        public void FakeGemMount_position_lies_ra_jump_and_ra_freeze() {
            var (mount, clock) = At(-4.0, 85.0, tracking: false);
            var ra = ReportedRaDeg(mount);
            mount.RaJumpHours = 12.0;
            Assert.That(Math.Abs(GemGeometry.Wrap180(ReportedRaDeg(mount) - ra)), Is.EqualTo(180.0).Within(1e-6));
            mount.RaJumpHours = 0.0;
            mount.ReadingFrozen = true;
            Run(mount, clock, TimeSpan.FromMinutes(30));
            Assert.That(ReportedRaDeg(mount), Is.EqualTo(ra).Within(1e-9), "frozen, while the sky turns");
        }

        [Test]
        public async Task FakeGemMount_failed_fresh_reads_answer_null_and_the_cache_falls_to_the_placeholder() {
            var (mount, _) = At(-4.0, 85.0);
            mount.FreshReadsFail = 1;
            Assert.That(await mount.ReadPointingNowAsync(CancellationToken.None), Is.Null);
            Assert.That(mount.GetCurrentPosition().Epoch, Is.EqualTo(Epoch.J2000));
            Assert.That((mount.GetCurrentPosition().RADegrees, mount.GetCurrentPosition().Dec), Is.EqualTo((0.0, 0.0)));
            Assert.That(await mount.ReadPointingNowAsync(CancellationToken.None), Is.Not.Null);
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(85.0).Within(1e-9));
        }

        [Test]
        public async Task FakeGemMount_power_cycle_that_reports_Home_leaves_the_true_pose_where_it_was() {
            var (mount, _) = At(-4.0, 85.0);
            var pose = mount.TruePose;
            var session = mount.SessionEpoch;
            mount.PowerCycle(reportsHome: true);

            Assert.That(mount.TruePose, Is.EqualTo(pose));
            Assert.That(mount.GetInfo().AtHome, Is.True);
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(90.0).Within(1e-9), "it reports the pole");
            Assert.That(mount.SessionEpoch, Is.EqualTo(session + 1));
            Assert.That(mount.Tracking, Is.False);
            await mount.ConfirmHomeAsync(CancellationToken.None);
        }

        [Test]
        public async Task FakeGemMount_find_home_drives_to_the_true_Home_and_clears_a_false_zero() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.PowerCycle(reportsHome: true);
            var home = mount.FindHome(new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), MoveAsker.Session, CancellationToken.None);
            for (var i = 0; i < 120 && !home.IsCompleted; i++) {
                Run(mount, clock, OneSecond);
                await Task.Yield();
            }
            Assert.That(await home.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(mount.TruePose, Is.EqualTo(GemGeometry.Home(south: false)));
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(90.0).Within(1e-9));
            Assert.That(mount.Motions.Single().Kind, Is.EqualTo(MotionKind.FindHome));
        }

        [Test]
        public void FakeGemMount_southern_site_mirrors_the_dec_axis() {
            var (mount, _) = At(-4.0, -85.0, latitudeDeg: -35.0);
            Assert.That(mount.TruePose, Is.EqualTo(new GemPose(120.0, -95.0)));
            Assert.That(mount.TrueSide, Is.EqualTo(PierSide.pierWest));
            Assert.That(mount.TruePhiDeg, Is.EqualTo(30.0).Within(1e-9));
            Assert.That(mount.GetCurrentPosition().Dec, Is.EqualTo(-85.0).Within(1e-9));
        }

        // ── stops the mount ignores ────────────────────────────────────────────────────────

        [TestCase(1)]
        [TestCase(2)]
        public async Task FakeGemMount_abort_ignored_n_ignores_exactly_n_aborts(int n) {
            var (mount, clock) = At(-4.0, 85.0);
            mount.AbortIgnored = n;
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);
            for (var i = 0; i < n; i++) {
                await mount.AbortSlewAsync("polar_align", CancellationToken.None);
                Run(mount, clock, OneSecond);
                Assert.That(mount.IsMoving, Is.True, $"abort {i + 1} of {n} is ignored");
            }
            await mount.AbortSlewAsync("polar_align", CancellationToken.None);
            Run(mount, clock, OneSecond);

            Assert.That(mount.IsMoving, Is.False);
            Assert.That(mount.Stops.Select(s => s.Honoured), Is.EqualTo(Enumerable.Repeat(false, n).Append(true)));
            Assert.That(mount.AbortCount, Is.EqualTo(n + 1));
            Assert.That(mount.AbortEpoch, Is.EqualTo(n + 1));
            Assert.That(mount.Motions.Single().TrueDeltaHaDeg, Is.EqualTo(-2.0 * n).Within(1e-6));
        }

        [Test]
        public async Task FakeGemMount_abort_ignored_counts_only_aborts_with_something_to_stop() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.AbortIgnored = 1;
            await mount.AbortSlewAsync("polar_align", CancellationToken.None); // a still mount: nothing to ignore
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);
            await mount.AbortSlewAsync("polar_align", CancellationToken.None);
            Run(mount, clock, OneSecond);

            Assert.That(mount.IsMoving, Is.True, "the one ignored abort is the one sent while the motor ran");
            Assert.That(mount.Stops.Select(s => s.Honoured), Is.EqualTo(HonouredThenIgnored));
        }

        [Test]
        public async Task FakeGemMount_fresh_read_takes_its_time_on_the_manual_clock() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.FreshReadTakes = TimeSpan.FromMilliseconds(10);
            var ha0 = mount.TruePose.MechHaDeg;
            var t0 = clock.GetUtcNow();
            await mount.MoveAxisAsync(0, 10.0, leaseMs: null, CancellationToken.None);
            for (var i = 0; i < 5; i++) {
                await mount.ReadPointingNowAsync(CancellationToken.None);
            }
            await mount.ReadSlewingNowAsync(CancellationToken.None);
            await mount.ReadNowAsync(CancellationToken.None);

            Assert.That(clock.GetUtcNow() - t0, Is.EqualTo(TimeSpan.FromMilliseconds(70)), "seven reads of 10 ms");
            Assert.That(GemGeometry.Wrap180(mount.TruePose.MechHaDeg - ha0), Is.EqualTo(-0.7).Within(1e-9), "10°/s for the 70 ms they took");
            Assert.That(mount.GetCurrentPosition(), Is.Not.Null);
            Assert.That(clock.GetUtcNow() - t0, Is.EqualTo(TimeSpan.FromMilliseconds(70)), "a cached read takes no time");
        }

        [TestCase(1)]
        [TestCase(3)]
        public async Task FakeGemMount_moveaxis_stop_ignored_n_ignores_exactly_n_stops(int n) {
            var (mount, clock) = At(-4.0, 85.0);
            mount.MoveAxisStopIgnored = n;
            mount.RunawayDegPerSec = 0.5;
            await mount.MoveAxisAsync(0, 2.0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, OneSecond);
            for (var i = 0; i < n; i++) {
                await mount.MoveAxisAsync(0, 0, leaseMs: null, CancellationToken.None);
                Run(mount, clock, OneSecond);
            }
            Assert.That(mount.IsMoving, Is.True);
            await mount.MoveAxisAsync(0, 0, leaseMs: null, CancellationToken.None);
            Run(mount, clock, OneSecond);

            Assert.That(mount.IsMoving, Is.False);
            Assert.That(mount.Stops.Select(s => s.Honoured), Is.EqualTo(Enumerable.Repeat(false, n).Append(true)));
            Assert.That(mount.Motions.Single().TrueDeltaHaDeg, Is.EqualTo(-(2.0 + (0.5 * n))).Within(1e-6), "1 s at 2°/s, then the runaway's 0.5°/s");
        }

        // ── ARA-15: the calls PA must never make ───────────────────────────────────────────

        [Test]
        public void FakeGemMount_throws_on_every_call_polar_alignment_must_never_make() {
            var (mount, _) = At(-4.0, 85.0);
            var here = mount.GetCurrentPosition();
            Assert.Throws<InvalidOperationException>(() => mount.Sync(here));
            Assert.Throws<InvalidOperationException>(() => mount.Sync(here, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => mount.MeridianFlip(here, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => mount.Action("telescope:setpierside", "1"));
            Assert.Throws<InvalidOperationException>(() => mount.SendCommandString(":MS2#"));
            Assert.Throws<InvalidOperationException>(() => mount.SendCommandBool(":MS1#"));
            Assert.Throws<InvalidOperationException>(() => mount.SendCommandBlind(":Q#"));

            Assert.That(mount.Action("motionsafety", ""), Is.EqualTo("1"), "the one Action PA may call");
            Assert.That(mount.Forbidden.Select(f => f.Member), Is.EqualTo(ForbiddenMembers));
            Assert.That(mount.CommonViolations(null, 60.0), Has.Exactly(7).Contains("forbidden call"));
        }

        [Test]
        public void FakeGemMount_without_the_motionsafety_marker_answers_not_implemented() {
            var (mount, _) = At(-4.0, 85.0);
            mount.MotionSafetyMarker = null;
            Assert.Throws<NotSupportedException>(() => mount.Action("motionsafety", ""));
            Assert.That(mount.Forbidden, Is.Empty);
        }

        // ── ARA-2 seam: one motion owner, the inhibit latch ────────────────────────────────

        [Test]
        public async Task FakeGemMount_inhibit_latch_refuses_new_motion_and_a_second_owner() {
            var (mount, _) = At(-4.0, 85.0);
            using (mount.AcquireMotion("polar_align")) {
                Assert.That(mount.MotionOwner, Is.EqualTo("polar_align"));
                Assert.Throws<InvalidOperationException>(() => mount.AcquireMotion("rest_goto"));
            }
            Assert.That(mount.MotionOwner, Is.Null);

            mount.Inhibit();
            Assert.Throws<InvalidOperationException>(() => mount.AcquireMotion("polar_align"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => mount.MoveAxisAsync(0, 1.0, 3000, CancellationToken.None));
            Assert.That(mount.SetTrackingEnabled(true), Is.False);
            Assert.That(mount.IsMoving, Is.False);
            Assert.That(mount.Motions.Single().Refused, Is.True);
        }

        [Test]
        public async Task FakeGemMount_routine_move_is_refused_while_the_pose_is_unverified() {
            var (mount, _) = At(-4.0, 85.0);
            mount.PositionUnverifiedReason = "power_cycle";
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => mount.MoveAxisAsync(0, 1.0, 3000, mount.AbortEpoch, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified), "the daemon's own refusal");
            await mount.MoveAxisAsync(0, 1.0, 3000, CancellationToken.None); // the pad's own nudge is allowed, as TelescopeService
            Assert.That(mount.IsMoving, Is.True);
        }

        // ── the common asserts ─────────────────────────────────────────────────────────────

        [Test]
        public async Task FakeGemMount_common_asserts_hold_for_a_clean_turn_and_catch_motion_after_the_end_event() {
            var (mount, clock) = At(-4.0, 85.0);
            var events = 0;
            mount.EventCount = () => events;
            await mount.MoveAxisAsync(0, 2.0, 3000, CancellationToken.None);
            Run(mount, clock, TimeSpan.FromSeconds(2));
            await mount.MoveAxisAsync(0, 0, null, CancellationToken.None);
            events = 1; // the run's end event, index 0, is published

            Assert.That(mount.CommonViolations(endEventIndex: 0, limitDeg: 60.0), Is.Empty);
            mount.AssertCommon(endEventIndex: 0, limitDeg: 60.0);

            await mount.MoveAxisAsync(0, 2.0, 3000, CancellationToken.None);
            Assert.That(mount.CommonViolations(endEventIndex: 0, limitDeg: 60.0), Has.Exactly(1).Contains("after the run's end event"));
            Assert.That(mount.CommonViolations(endEventIndex: 0, limitDeg: 20.0), Has.Some.Contains("max_true_phi"), "φ 30° at the start is past L = 20°");
        }

        [Test]
        public async Task FakeGemMount_motor_travel_since_a_stop_shows_a_mount_that_never_stopped() {
            var (mount, clock) = At(-4.0, 85.0);
            mount.MoveAxisStopIgnored = int.MaxValue;
            await mount.MoveAxisAsync(0, 2.0, 3000, CancellationToken.None);
            Run(mount, clock, OneSecond);
            await mount.MoveAxisAsync(0, 0, null, CancellationToken.None);
            var stoppedAt = mount.Now;
            Run(mount, clock, TimeSpan.FromSeconds(3));

            Assert.That(mount.MotorTravelSince(stoppedAt), Is.EqualTo(6.0).Within(1e-6), "it ran on at 2°/s, lease or not");
            Assert.That(mount.MaxTrueRaExcursionDeg(stoppedAt), Is.EqualTo(6.0).Within(1e-6));
        }
    }
}
