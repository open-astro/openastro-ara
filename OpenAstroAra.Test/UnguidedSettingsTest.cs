#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors
    This file is part of OpenAstro Ara (forked from N.I.N.A.).
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1264 — Imaging → Unguided: the profile section, its write-boundary validation and the
    /// prior's tolerance basis.</summary>
    [TestFixture]
    public class UnguidedSettingsTest {

        private static readonly double[] Ascending = { 5, 20, 80 };
        private static readonly double[] Descending = { 80, 5 };
        private static readonly double[] WithZero = { 0, 10 };

        [Test]
        public void Defaults_are_the_shipped_behaviour_and_back_fill_an_older_profile() {
            var d = new UnguidedSettingsDto();
            Assert.That(d.GuardEnabled, Is.True);
            Assert.That(d.EccentricityTolerance, Is.EqualTo(0.5));
            Assert.That(d.GoodEccentricity, Is.EqualTo(0.3));
            Assert.That((d.StepDownAfter, d.StepUpAfter), Is.EqualTo((2, 10)));
            Assert.That(d.EffectiveProbeRungsSeconds, Is.EqualTo(new[] { 10.0, 30, 60, 120 }));
            Assert.That(d.RecentreDriftArcmin, Is.EqualTo(10));
            Assert.That(d.PriorToleranceBasis, Is.EqualTo(UnguidedPriorToleranceBases.Max));

            var normalized = ProfileSnapshotNormalizer.Normalize(ProfileSnapshotNormalizer.Defaults with { Unguided = null });
            Assert.That(normalized.Unguided, Is.Not.Null, "a pre-#1264 profile.json back-fills the section");
            Assert.That(normalized.Unguided!.StepUpAfter, Is.EqualTo(10));
        }

        [Test]
        public void The_store_round_trips_the_section() {
            var store = new InMemoryProfileStore();
            Assert.That(store.GetUnguidedSettings().GuardEnabled, Is.True);
            store.PutUnguidedSettings(new UnguidedSettingsDto(GuardEnabled: false, ProbeRungsSeconds: Ascending, PriorToleranceBasis: UnguidedPriorToleranceBases.Seeing));
            var back = store.GetUnguidedSettings();
            Assert.That(back.GuardEnabled, Is.False);
            Assert.That(back.EffectiveProbeRungsSeconds, Is.EqualTo(Ascending));
            Assert.That(back.PriorToleranceBasis, Is.EqualTo("seeing"));
        }

        private static string Reject(UnguidedSettingsDto body) {
            var result = ProfileEndpoints.PutUnguidedSettings(body, new InMemoryProfileStore());
            Assert.That(result, Is.InstanceOf<ProblemHttpResult>(), "expected a 400");
            return ((ProblemHttpResult)result).ProblemDetails.Detail ?? string.Empty;
        }

        [Test]
        public void The_write_boundary_rejects_what_would_break_the_guard_or_the_probe() {
            Assert.That(Reject(new UnguidedSettingsDto(EccentricityTolerance: 0)), Does.Contain("eccentricity_tolerance"));
            Assert.That(Reject(new UnguidedSettingsDto(EccentricityTolerance: 1.5)), Does.Contain("eccentricity_tolerance"));
            Assert.That(Reject(new UnguidedSettingsDto(GoodEccentricity: 0.5)), Does.Contain("good_eccentricity"), "the round bar must sit below the trailed bar");
            Assert.That(Reject(new UnguidedSettingsDto(StepDownAfter: 0)), Does.Contain("step_down_after"));
            Assert.That(Reject(new UnguidedSettingsDto(ProbeRungsSeconds: Descending)), Does.Contain("ascending"));
            Assert.That(Reject(new UnguidedSettingsDto(ProbeRungsSeconds: WithZero)), Does.Contain("positive"));
            Assert.That(Reject(new UnguidedSettingsDto(RecentreDriftArcmin: 0)), Does.Contain("recentre_drift_arcmin"));
            Assert.That(Reject(new UnguidedSettingsDto(RecentreSolveEveryN: 0)), Does.Contain("recentre_solve_every_n"));
            Assert.That(Reject(new UnguidedSettingsDto(PriorToleranceBasis: "inches")), Does.Contain("prior_tolerance_basis"));

            var store = new InMemoryProfileStore();
            var ok = ProfileEndpoints.PutUnguidedSettings(new UnguidedSettingsDto(EccentricityTolerance: 0.6, GoodEccentricity: 0.35, ProbeRungsSeconds: Ascending), store);
            Assert.That(ok, Is.InstanceOf<Ok<UnguidedSettingsDto>>());
            Assert.That(store.GetUnguidedSettings().EccentricityTolerance, Is.EqualTo(0.6));
        }

        [Test]
        public void The_tolerance_basis_picks_the_pixel_the_seeing_or_the_larger() {
            Assert.That(MountSpecSheet.Tolerance(2.3, 3.0, UnguidedPriorToleranceBases.Max), Is.EqualTo(2.3));
            Assert.That(MountSpecSheet.Tolerance(0.8, 3.0, UnguidedPriorToleranceBases.Max), Is.EqualTo(1.5));
            Assert.That(MountSpecSheet.Tolerance(2.3, 3.0, UnguidedPriorToleranceBases.Pixel), Is.EqualTo(2.3));
            Assert.That(MountSpecSheet.Tolerance(2.3, 3.0, UnguidedPriorToleranceBases.Seeing), Is.EqualTo(1.5));
            Assert.That(MountSpecSheet.Tolerance(null, 3.0, UnguidedPriorToleranceBases.Pixel), Is.Null, "the basis needs its figure");
            Assert.That(MountSpecSheet.Tolerance(2.3, null, UnguidedPriorToleranceBases.Seeing), Is.Null);
        }

        private sealed class FixedSpecs : IMountSpecSheetSource {
            public MountSpecSheetDto? CurrentMountSpecs { get; set; }
        }

        private sealed class NoFrames : IAnalysisFrameSource {
            public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) =>
                throw new InvalidOperationException("the prior never captures");
        }

        [Test]
        public async Task The_prior_honours_the_profile_tolerance_basis() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(new OpticsSettingsDto(447, 1.0, 6248, 4176, 3.76, 91)); // 1.735"/px
            store.PutSiteSettings(store.GetSiteSettings() with { TypicalSeeingArcsec = 4.0 }); // half = 2.0"
            var specs = new FixedSpecs { CurrentMountSpecs = MountSpecSheet.Parse("{\"model\":\"EQM-35 Pro\",\"worm_period_s\":638,\"periodic_error_pp_arcsec\":25}") };
            var svc = new TrackingProbeService(store, new NoFrames(), specs: specs);

            var (max, _) = await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None);
            store.PutUnguidedSettings(new UnguidedSettingsDto(PriorToleranceBasis: UnguidedPriorToleranceBases.Pixel));
            var (pixel, _) = await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None);
            Assert.That(pixel, Is.Not.Null);
            Assert.That(max, Is.Not.Null.And.GreaterThan(pixel!.Value), "the larger tolerance (2.0\" seeing) allows a longer prior than one 1.7\" pixel");
        }
    }
}
