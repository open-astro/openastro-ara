#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2068 (ARA-18) — the pre-flight evaluator's table: it never throws, the mount clock check is #2087's gate, and the clearance is per mount.</summary>
    [TestFixture]
    public class PolarAlignPreflightEvaluatorTest {

        private const string MountId = "a1b2c3d4-mount";

        /// <summary>The placeholder rows #2065 would have filled; #2065 is closed (NINA parity, Joey 2026-11-21).</summary>
        private static readonly string[] DroppedRows = ["motionsafety", "pier_side", "pose_envelope", "start_cw_angle", "turn_step"];

        private static readonly PolarAlignStartPlanDto Plan = new(PolarAlignStages.Fine, PolarAlignDirections.East, 10.0, 2, 20.0);

        private static PolarAlignPreflightInputs Inputs(
                Func<TelescopeInfo?>? mount = null,
                Func<SiteSettingsDto>? site = null,
                Func<PolarAlignSettingsDto>? settings = null,
                Func<string?>? clock = null,
                Func<CancellationToken, Task<string?>>? pose = null,
                Func<CancellationToken, Task<PolarAlignStartPlanDto>>? plan = null) {
            var store = new InMemoryProfileStore();
            var siteSettings = store.GetSiteSettings() with { LatitudeDeg = 52.0, LongitudeDeg = 4.0 };
            return new PolarAlignPreflightInputs(
                PolarAlignStages.Fine,
                mount ?? (() => new TelescopeInfo { Connected = true, DeviceId = MountId, Name = "Bench GEM" }),
                site ?? (() => siteSettings),
                settings ?? (() => PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed(MountId))),
                clock ?? (() => null),
                pose ?? (_ => Task.FromResult<string?>(null)),
                plan ?? (_ => Task.FromResult(Plan)),
                AlignerPresent: null);
        }

        private static PolarAlignPreflightCheckDto Check(PolarAlignPreflightResult r, string id) => r.Dto.Checks.Single(c => c.Id == id);

        [Test]
        public async Task Every_input_that_throws_becomes_a_check_status_and_never_an_exception() {
            var boom = new InvalidOperationException("boom");
            var inputs = Inputs(
                site: () => throw boom,
                settings: () => throw boom,
                clock: () => throw boom,
                pose: _ => throw boom,
                plan: _ => Task.FromException<PolarAlignStartPlanDto>(boom)) with {
                    AlignerPresent = () => throw boom, AlignmentMode = () => throw boom, CanMovePrimaryAxis = () => throw boom,
                };

            var result = await PolarAlignPreflightEvaluator.EvaluateAsync(inputs, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Dto.Checks.Select(c => c.Id), Is.EqualTo(PolarAlignPreflightChecks.All));
            Assert.Multiple(() => {
                Assert.That(result.Dto.Ready, Is.False);
                Assert.That(Check(result, PolarAlignPreflightChecks.SiteLatitude).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(result, PolarAlignPreflightChecks.MountClockOrSite).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(result, PolarAlignPreflightChecks.PoseVerified).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(result, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(result, PolarAlignPreflightChecks.StartPointing).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(result, PolarAlignPreflightChecks.Aligner).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(Check(result, PolarAlignPreflightChecks.MountType).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(Check(result, PolarAlignPreflightChecks.CanMoveAxis).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(result.FirstFailure, Is.SameAs(boom), "a start throws the first failed read, as it did before the list");
            });

            var noMount = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(mount: () => throw boom), CancellationToken.None).ConfigureAwait(false);
            Assert.That(Check(noMount, PolarAlignPreflightChecks.MountConnected).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
            Assert.That(noMount.FirstFailure, Is.SameAs(boom));
            Assert.That(noMount.Dto.MountKey, Is.Null);
        }

        [Test]
        public async Task The_mount_limits_note_warns_and_never_refuses() {
            var result = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(), CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                foreach (var dropped in DroppedRows) {
                    Assert.That(result.Dto.Checks.Select(c => c.Id), Does.Not.Contain(dropped));
                }
                Assert.That(Check(result, PolarAlignPreflightChecks.MountLimits).Status, Is.EqualTo(PolarAlignPreflightStatuses.Warn));
                Assert.That(result.Dto.Ready, Is.True, "unknown and warn checks never refuse");
                Assert.That(result.FirstFailure, Is.Null);
                Assert.That(result.Dto.Planned.StartCwAngleDeg, Is.Null);
                Assert.That(result.Dto.Planned.MinAltitudeDeg, Is.Null);
                Assert.That(result.Dto.Planned.Direction, Is.EqualTo(PolarAlignDirections.East));
                Assert.That(result.Dto.Planned.MaxSweepDeg, Is.EqualTo(20.0));
            });
        }

        [Test]
        public async Task The_polar_aligner_row_passes_with_its_motors_on_because_its_stop_is_checked() {
            PolarAlignSettingsDto Motors(bool auto) =>
                PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto(PolarAlignerAuto: auto).Confirmed(MountId));

            var none = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(), CancellationToken.None).ConfigureAwait(false);
            var off = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs(settings: () => Motors(false)) with { AlignerPresent = () => true }, CancellationToken.None).ConfigureAwait(false);
            var on = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs(settings: () => Motors(true)) with { AlignerPresent = () => true }, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(Check(none, PolarAlignPreflightChecks.Aligner).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(none, PolarAlignPreflightChecks.Aligner).Detail, Does.Contain("No Polar Aligner"));
                Assert.That(Check(off, PolarAlignPreflightChecks.Aligner).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(off, PolarAlignPreflightChecks.Aligner).Detail, Does.Contain("motors are off"));
                Assert.That(Check(on, PolarAlignPreflightChecks.Aligner).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok),
                    "#2115 verified the stop and capped the travel: the motors-on row is real");
                Assert.That(Check(on, PolarAlignPreflightChecks.Aligner).Detail,
                    Is.EqualTo("The Polar Aligner's Stop is checked and its turns are capped."));
                Assert.That(on.Dto.Ready, Is.True);
            });
        }

        [Test]
        public async Task A_hand_turn_says_the_polar_aligner_motors_are_off_even_with_auto_on() {
            var settings = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto(PolarAlignerAuto: true).Confirmed(MountId));

            var handTurn = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs(settings: () => settings) with { AlignerPresent = () => true, HandTurn = true }, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(Check(handTurn, PolarAlignPreflightChecks.Aligner).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(handTurn, PolarAlignPreflightChecks.Aligner).Detail,
                    Is.EqualTo("The Polar Aligner's motors are off: you turn the bolts."),
                    "a hand-turn run never drives the aligner's motors, so the row must not claim its Stop");
            });
        }

        [Test]
        public async Task No_check_says_a_later_issue_will_fill_it() {
            var ready = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(), CancellationToken.None).ConfigureAwait(false);
            var aligner = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with {
                AlignerPresent = () => true, AlignmentMode = () => ASCOM.Common.DeviceInterfaces.AlignmentMode.Polar, CanMovePrimaryAxis = () => true,
            }, CancellationToken.None).ConfigureAwait(false);
            var noMount = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs(mount: () => new TelescopeInfo { Connected = false }), CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                foreach (var check in ready.Dto.Checks.Concat(aligner.Dto.Checks).Concat(noMount.Dto.Checks)) {
                    Assert.That(check.Detail, Does.Not.Match("#20[0-9][0-9]|lands|comes with|opt-in"), check.Id);
                }
                Assert.That(Check(ready, PolarAlignPreflightChecks.MountClockOrSite).Detail,
                    Is.EqualTo("The mount's clock is set from the synced rig clock and checked."));
                Assert.That(Check(aligner, PolarAlignPreflightChecks.MountType).Detail, Is.EqualTo("An equatorial (fork or polar) mount."),
                    "a fork mount is simply allowed");
            });
        }

        [Test]
        public async Task The_mount_clock_check_is_the_mount_clock_gates_answer() {
            const string closed = "Mount clock not verified: the rig clock has not been synced from the app or a GPS.";

            var refused = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(clock: () => closed), CancellationToken.None).ConfigureAwait(false);
            var open = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(), CancellationToken.None).ConfigureAwait(false);
            var noGate = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with { ClockRefusal = null }, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(Check(refused, PolarAlignPreflightChecks.MountClockOrSite).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(Check(refused, PolarAlignPreflightChecks.MountClockOrSite).Detail, Is.EqualTo(closed));
                Assert.That((refused.FirstFailure as RefusalException)?.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
                Assert.That(Check(open, PolarAlignPreflightChecks.MountClockOrSite).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(noGate, PolarAlignPreflightChecks.MountClockOrSite).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
            });
        }

        [Test]
        public async Task The_clearance_check_reads_the_connected_mounts_own_entry() {
            var otherMount = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed("another-mount", reachDeg: 45));
            var thisMount = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed(MountId, reachDeg: 45));

            var notConfirmed = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(settings: () => otherMount), CancellationToken.None).ConfigureAwait(false);
            var confirmed = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(settings: () => thisMount), CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(notConfirmed.Dto.MountKey, Is.EqualTo(MountId), "keyed by the mount's Alpaca UniqueID");
                Assert.That(Check(notConfirmed, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail),
                    "another mount's confirmation is not this one's");
                var refusal = notConfirmed.FirstFailure as RefusalException;
                Assert.That(refusal?.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
                Assert.That(refusal?.Parameters["reach_deg"], Is.EqualTo(20.0), "the plan's two 10° turns");
                Assert.That(Check(confirmed, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(confirmed.Dto.Ready, Is.True);
            });
        }

        // #2165 — the confirmation is for a reach: the plan's turns plus the 0.5° direction test when it is owed. A
        // smaller or equal reach stays confirmed; a bigger one (a larger turn size, a test now owed) asks again.
        [Test]
        public async Task A_confirmed_reach_covers_a_smaller_or_equal_run_and_a_bigger_one_asks_again() {
            var withTest = Plan with { DirectionTestDeg = 0.5 };
            async Task<PolarAlignPreflightResult> Run(double confirmedDeg, PolarAlignStartPlanDto plan, bool handTurn = false) =>
                await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(
                    settings: () => PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed(MountId, reachDeg: confirmedDeg)),
                    plan: _ => Task.FromResult(plan)) with { HandTurn = handTurn }, CancellationToken.None).ConfigureAwait(false);

            var equal = await Run(20.5, withTest).ConfigureAwait(false);
            var larger = await Run(60.5, withTest).ConfigureAwait(false);
            var smaller = await Run(20.0, withTest).ConfigureAwait(false);
            var smallerHand = await Run(20.0, withTest, handTurn: true).ConfigureAwait(false);
            var noTest = await Run(20.0, Plan).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(equal.Dto.Planned.ReachDeg, Is.EqualTo(20.5), "the turns plus the owed test");
                Assert.That(noTest.Dto.Planned.ReachDeg, Is.EqualTo(20.0), "no test owed, no test counted");
                Assert.That(Check(equal, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(equal, PolarAlignPreflightChecks.ClearanceConfirmed).Detail, Does.Contain("up to 20.5° each way"));
                Assert.That(Check(larger, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(noTest, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(smaller, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail),
                    "a bigger reach than the one confirmed asks again");
                Assert.That((smaller.FirstFailure as RefusalException)?.Parameters["reach_deg"], Is.EqualTo(20.5));
                Assert.That(Check(smallerHand, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Warn),
                    "a hand-turn start only warns");
            });
        }

        // #2165 — with no plan (the start pointing fails) the reach is still the stage's two turns plus the test the
        // daemon owes, never less.
        [Test]
        public async Task Without_a_plan_the_reach_is_the_stage_turns_plus_the_owed_direction_test() {
            var settings = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto(SeedRotationDeg: 5.0).Confirmed(MountId, reachDeg: 10.0));
            var noPlan = Inputs(settings: () => settings, plan: _ => Task.FromException<PolarAlignStartPlanDto>(new InvalidOperationException("no reading")));

            var owed = await PolarAlignPreflightEvaluator.EvaluateAsync(noPlan with { DirectionTestOwedDeg = () => 0.5 }, CancellationToken.None).ConfigureAwait(false);
            var saved = await PolarAlignPreflightEvaluator.EvaluateAsync(noPlan with { DirectionTestOwedDeg = () => 0.0 }, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(owed.Dto.Planned.ReachDeg, Is.EqualTo(10.5));
                Assert.That(Check(owed, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(saved.Dto.Planned.ReachDeg, Is.EqualTo(10.0));
                Assert.That(Check(saved, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
            });
        }

        private static RefusalCause? CauseOf(PolarAlignPreflightResult r) => (r.FirstFailure as RefusalException)?.Cause;

        // #2083 — Polar Align steers by the hour angle: no usable site longitude, the unset site at 0°, 0°, or no local
        // sidereal time, and it refuses rather than guess.
        [Test]
        public async Task A_site_without_a_longitude_or_sidereal_time_or_at_0_0_refuses() {
            var store = new InMemoryProfileStore();
            SiteSettingsDto SiteAt(double lat, double lon) => store.GetSiteSettings() with { LatitudeDeg = lat, LongitudeDeg = lon };
            TelescopeInfo MountWithLst(double lst) => new() { Connected = true, DeviceId = MountId, Name = "Bench GEM", SiderealTime = lst };

            var noLongitude = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(site: () => SiteAt(52.0, double.NaN)), CancellationToken.None).ConfigureAwait(false);
            var unsetSite = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(site: () => SiteAt(0.0, 0.0)), CancellationToken.None).ConfigureAwait(false);
            var noLst = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(mount: () => MountWithLst(double.NaN)), CancellationToken.None).ConfigureAwait(false);
            var greenwich = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(site: () => SiteAt(51.5, 0.0), mount: () => MountWithLst(3.0)), CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                foreach (var (name, r) in new[] { ("no longitude", noLongitude), ("0°, 0°", unsetSite), ("no LST", noLst) }) {
                    Assert.That(Check(r, PolarAlignPreflightChecks.SiteLongitudeLst).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail), name);
                    Assert.That(r.Dto.Ready, Is.False, name);
                }
                Assert.That(CauseOf(noLongitude), Is.EqualTo(RefusalCatalog.PolarAlignSiteNotSet));
                Assert.That(CauseOf(noLst), Is.EqualTo(RefusalCatalog.PolarAlignSiteNotSet));
                Assert.That(CauseOf(unsetSite), Is.EqualTo(RefusalCatalog.PolarAlignSiteNotSet), "0°, 0° refuses as an unset site, not a bad latitude");
                Assert.That(Check(unsetSite, PolarAlignPreflightChecks.SiteLongitudeLst).Detail, Does.Contain("never set"));
                Assert.That(Check(greenwich, PolarAlignPreflightChecks.SiteLongitudeLst).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok),
                    "longitude 0° with a real latitude is a real site");
                Assert.That(greenwich.Dto.Ready, Is.True);
            });
        }

        // #2083 — on an alt-az mount MoveAxis(Primary) turns azimuth, not RA.
        [Test]
        public async Task An_alt_az_mount_refuses_and_an_equatorial_one_passes() {
            async Task<PolarAlignPreflightResult> With(AlignmentMode? mode) =>
                await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with { AlignmentMode = () => mode }, CancellationToken.None).ConfigureAwait(false);

            var altAz = await With(AlignmentMode.AltAz).ConfigureAwait(false);
            var altAzByHand = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs() with { AlignmentMode = () => AlignmentMode.AltAz, HandTurn = true }, CancellationToken.None).ConfigureAwait(false);
            var gem = await With(AlignmentMode.GermanPolar).ConfigureAwait(false);
            var fork = await With(AlignmentMode.Polar).ConfigureAwait(false);
            var unknown = await With(null).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(Check(altAz, PolarAlignPreflightChecks.MountType).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(CauseOf(altAz), Is.EqualTo(RefusalCatalog.PolarAlignAltAzMount));
                Assert.That(CauseOf(altAzByHand), Is.EqualTo(RefusalCatalog.PolarAlignAltAzMount), "turning by hand has no RA axis to turn either");
                Assert.That(Check(gem, PolarAlignPreflightChecks.MountType).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(fork, PolarAlignPreflightChecks.MountType).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(unknown, PolarAlignPreflightChecks.MountType).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(unknown.Dto.Ready, Is.True, "a mount that does not report its type is not refused");
            });
        }

        // #2083 — a mount that cannot turn its RA axis by rate gets no motor start; Turn RA by hand moves nothing.
        [Test]
        public async Task A_mount_that_cannot_move_its_RA_axis_refuses_a_motor_start_and_warns_a_hand_turn() {
            var motor = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with { CanMovePrimaryAxis = () => false }, CancellationToken.None).ConfigureAwait(false);
            var byHand = await PolarAlignPreflightEvaluator.EvaluateAsync(
                Inputs() with { CanMovePrimaryAxis = () => false, HandTurn = true }, CancellationToken.None).ConfigureAwait(false);
            var can = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with { CanMovePrimaryAxis = () => true }, CancellationToken.None).ConfigureAwait(false);
            var unknown = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs() with { CanMovePrimaryAxis = () => null }, CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(Check(motor, PolarAlignPreflightChecks.CanMoveAxis).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(CauseOf(motor), Is.EqualTo(RefusalCatalog.PolarAlignCannotMoveAxis));
                Assert.That(Check(byHand, PolarAlignPreflightChecks.CanMoveAxis).Status, Is.EqualTo(PolarAlignPreflightStatuses.Warn));
                Assert.That(byHand.Dto.Ready, Is.True);
                Assert.That(Check(can, PolarAlignPreflightChecks.CanMoveAxis).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(Check(unknown, PolarAlignPreflightChecks.CanMoveAxis).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(unknown.Dto.Ready, Is.True);
            });
        }
    }
}
