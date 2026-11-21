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

    /// <summary>#2068 (ARA-18) — the pre-flight evaluator's table: it never throws, the checks later PRs fill read
    /// <c>unknown</c>, the mount clock check is #2087's gate, and the clearance is per mount.</summary>
    [TestFixture]
    public class PolarAlignPreflightEvaluatorTest {

        private const string MountId = "a1b2c3d4-mount";

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
                plan: _ => Task.FromException<PolarAlignStartPlanDto>(boom)) with { AlignerPresent = () => throw boom };

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
                Assert.That(result.FirstFailure, Is.SameAs(boom), "a start throws the first failed read, as it did before the list");
            });

            var noMount = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(mount: () => throw boom), CancellationToken.None).ConfigureAwait(false);
            Assert.That(Check(noMount, PolarAlignPreflightChecks.MountConnected).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
            Assert.That(noMount.FirstFailure, Is.SameAs(boom));
            Assert.That(noMount.Dto.MountKey, Is.Null);
        }

        [Test]
        public async Task The_checks_later_changes_fill_read_unknown_and_never_refuse() {
            var result = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(), CancellationToken.None).ConfigureAwait(false);

            var later = new[] {
                PolarAlignPreflightChecks.SiteLongitudeLst, PolarAlignPreflightChecks.MountType, PolarAlignPreflightChecks.CanMoveAxis,
                PolarAlignPreflightChecks.MotionSafety, PolarAlignPreflightChecks.PierSide, PolarAlignPreflightChecks.PoseEnvelope,
                PolarAlignPreflightChecks.StartCwAngle, PolarAlignPreflightChecks.TurnStep,
            };
            Assert.Multiple(() => {
                foreach (var id in later) {
                    Assert.That(Check(result, id).Status, Is.EqualTo(PolarAlignPreflightStatuses.Unknown), id);
                    Assert.That(Check(result, id).Detail, Does.Contain("#20"), id + " names the issue that fills it");
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
            var otherMount = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed("another-mount", maxCwAngleDeg: 45));
            var thisMount = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto().Confirmed(MountId, maxCwAngleDeg: 50));

            var notConfirmed = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(settings: () => otherMount), CancellationToken.None).ConfigureAwait(false);
            var confirmed = await PolarAlignPreflightEvaluator.EvaluateAsync(Inputs(settings: () => thisMount), CancellationToken.None).ConfigureAwait(false);

            Assert.Multiple(() => {
                Assert.That(notConfirmed.Dto.MountKey, Is.EqualTo(MountId), "keyed by the mount's Alpaca UniqueID");
                Assert.That(Check(notConfirmed, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail),
                    "another mount's confirmation is not this one's");
                var refusal = notConfirmed.FirstFailure as RefusalException;
                Assert.That(refusal?.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
                Assert.That(refusal?.Parameters["max_cw_angle_deg"], Is.EqualTo(PolarAlignSettingsClamp.DefaultMaxCwAngleDeg));
                Assert.That(notConfirmed.Dto.Planned.MaxCwAngleDeg, Is.EqualTo(PolarAlignSettingsClamp.DefaultMaxCwAngleDeg));
                Assert.That(Check(confirmed, PolarAlignPreflightChecks.ClearanceConfirmed).Status, Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(confirmed.Dto.Planned.MaxCwAngleDeg, Is.EqualTo(50.0), "this mount's own limit");
                Assert.That(confirmed.Dto.Ready, Is.True);
            });
        }
    }
}
