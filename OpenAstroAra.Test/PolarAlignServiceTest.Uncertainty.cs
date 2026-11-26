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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2072 "PR A" — each fit's uncertainty: each point's scatter (or the single-solve floor) carried through the
    /// three-point fit into a 1σ on the altitude, azimuth and total errors; the readout, the check's verdict, the log
    /// line and the session row carry it; the Check's verdict rates the error plus twice its 1σ; Done is not held by it
    /// (Joey, 2026-11-21); a single solve's floor is 0.15 px at the solving camera's pixel scale.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A guide-camera solver that answers at the hint's RA on the Dec-85° circle, with
        /// <see cref="DecOffsetArcsec"/> added to the frames it names by id, and records each answer. Each answer carries
        /// <see cref="PixelScaleArcsec"/> (null: unknown).</summary>
        private sealed class ScatterSolver : IPolarAlignFrameSolver, IMountSky {
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
            public Dictionary<string, double> DecOffsetArcsec { get; } = new(StringComparer.Ordinal);
            public double? PixelScaleArcsec { get; init; }
            public List<(string Frame, double RaDeg)> Answers { get; } = new();
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                var frame = Path.GetFileNameWithoutExtension(fitsPath);
                var ra = Sky?.Invoke().RaDeg ?? 0.0;
                lock (Answers) {
                    Answers.Add((frame, ra));
                }
                var offset = DecOffsetArcsec.TryGetValue(frame, out var arcsec) ? arcsec / 3600.0 : 0.0;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, PoleMountDecDeg + offset, PixelScaleArcsec: PixelScaleArcsec));
            }
            public double RaOf(string frame) {
                lock (Answers) {
                    return Answers.First(a => a.Frame == frame).RaDeg;
                }
            }
        }

        private static (double Alt, double Az, double Total) ExpectedSigma(double raA, double raB, double raC, double sigmaArcsec) =>
            PolarAlignGeometry.AxisErrorSigma(
                (raA, PoleMountDecDeg, sigmaArcsec), (raB, PoleMountDecDeg, sigmaArcsec), (raC, PoleMountDecDeg, sigmaArcsec),
                northernHemisphere: true, SiteLatDeg, SiteLonDeg, DateTimeOffset.UtcNow);

        [Test]
        public async Task A_scripted_scatter_at_each_point_gives_the_readout_its_fit_sigma_on_the_status_and_each_progress_event() {
            // ±2″ and 0″ in Dec at each seed point: rms 1.633″ about the mean, so the mean's 1σ per axis is
            // 1.633 / √(2·2) = 0.816″. The fit carries 0.816″ per point to the readout's σ.
            var solver = new ScatterSolver();
            foreach (var point in new[] { "seed-a", "seed-b", "seed-c" }) {
                solver.DecOffsetArcsec[point + "-1"] = 2.0;
                solver.DecOffsetArcsec[point + "-2"] = -2.0;
            }
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), ws, store: StoreWith(s => s with { FramesPerPoint = 3 }));

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var progress = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignProgress).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"), 2.0 / Math.Sqrt(6.0));
            // The status gives each σ at the 0.01′ the progress event does.
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(expected.Total, Is.GreaterThan(0.01), "the scripted scatter gives the fit a real σ");
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(expected.Total).Within(0.0051));
            Assert.That(status.AltitudeSigmaArcmin, Is.EqualTo(expected.Alt).Within(0.0051));
            Assert.That(status.AzimuthSigmaArcmin, Is.EqualTo(expected.Az).Within(0.0051));
            Assert.That(progress.GetProperty("total_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Total).Within(0.011));
            Assert.That(progress.GetProperty("altitude_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Alt).Within(0.011));
            Assert.That(progress.GetProperty("azimuth_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Az).Within(0.011));
        }

        [Test]
        public async Task A_single_frame_point_counts_a_1_arcsec_floor_in_the_fit_sigma() {
            // One frame per point (the bench store): no scatter to measure, and the solver gives no pixel scale, so each
            // point counts the 1″ fallback per axis, and the fit's log line says which floor it used.
            var solver = new ScatterSolver();
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: NewStore(), logger: logger);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"), 1.0);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(expected.Total).Within(0.0051));
            var fit = logger.Entries.Single(e => e.EventId == 4528).Message;
            Assert.That(fit, Does.EndWith("σ floor 1.00″ per axis (pixel scale unknown)"), fit);
        }

        [Test]
        public async Task A_coarse_pixel_scale_gives_a_proportionally_larger_fit_sigma_and_the_log_names_the_floor() {
            // Joey, 2026-11-21: a solve's floor is 0.15 px at the solving camera's scale. At 8″/px that is 1.2″ per axis,
            // so the fit's σ is 1.2 times the 1″ fallback's, and the log line says how the floor was reached.
            var solver = new ScatterSolver { PixelScaleArcsec = 8.0 };
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: NewStore(), logger: logger);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var (ra, rb, rc) = (solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"));
            var atScale = ExpectedSigma(ra, rb, rc, 0.15 * 8.0);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(atScale.Total / ExpectedSigma(ra, rb, rc, 1.0).Total, Is.EqualTo(1.2).Within(1e-6), "σ is linear in the floor");
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(atScale.Total).Within(0.0051));
            var fit = logger.Entries.Single(e => e.EventId == 4528).Message;
            Assert.That(fit, Does.EndWith("σ floor 0.15 px × 8.00″/px = 1.20″ per axis"), fit);
            Assert.That(fit.Split("±1.20″ (floor)").Length - 1, Is.EqualTo(3), fit);
            // Four times the scale, four times a point's floor (a mean of three solves, here).
            Assert.That(PolarAlignService.PointSigmaArcsec(new PolarAlignSolveOutcome(true, 0, 85, SigmaArcsec: 0.0, Solves: 3, PixelScaleArcsec: 8.0)),
                Is.EqualTo(4 * PolarAlignService.PointSigmaArcsec(new PolarAlignSolveOutcome(true, 0, 85, SigmaArcsec: 0.0, Solves: 3, PixelScaleArcsec: 2.0))).Within(1e-12));
        }

        [Test]
        public async Task The_seed_fit_log_line_carries_the_sigma_with_the_axis_and_the_solved_positions() {
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), new ScatterSolver(), NewPoleMount(), store: NewStore(), logger: logger);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var fit = logger.Entries.Single(e => e.EventId == 4528).Message;
            Assert.That(fit, Does.Contain("axis RA").And.Contain("(1σ)"));
            Assert.That(fit, Does.Match(@"alt -?\d+\.\d\d′ ± \d+\.\d\d′ az -?\d+\.\d\d′ ± \d+\.\d\d′, total ± \d+\.\d\d′"));
            // The three solves the fit used, each with its σ (the single-frame floor here).
            Assert.That(fit.Split("Dec 85.00000°").Length - 1, Is.EqualTo(3), fit);
            Assert.That(fit.Split("±1.00″ (floor)").Length - 1, Is.EqualTo(3), fit);
        }

        [Test]
        public async Task Done_inside_the_tolerance_completes_even_while_the_error_plus_two_sigma_is_over_it() {
            var log = new RecordingLog();
            var store = NewStore();
            using var svc = NewService(NewFake(), new MountFollowingSolver(), NewPoleMount(), store: store, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var total = status.CurrentErrorArcmin!.Value;
            var sigma = status.CurrentSigmaArcmin!.Value;
            Assert.That(sigma, Is.GreaterThan(0.01), "the single-frame floor gives the fit a σ");

            // Inside the tolerance, but not by twice its σ: Done is not held by the σ (Joey, 2026-11-21); the clients say
            // the measurement cannot confirm the alignment.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { TargetToleranceArcmin = total + sigma });
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await log.FirstRow.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(log.Rows.TryPeek(out var row) && row.Outcome == "complete", Is.True);
            // The status gives the σ at 0.01′ (#2072 review); the row keeps it unrounded.
            Assert.That(row!.FinalSigmaArcmin, Is.EqualTo(sigma).Within(0.005), "the row keeps the readout's σ");
        }

        [Test]
        public async Task A_check_verdict_carries_its_fit_sigma_on_the_result_the_event_and_the_status() {
            var solver = new ScatterSolver();
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), ws, store: NewStore());

            var status = await RunThroughDoneAsync(svc).ConfigureAwait(false);
            var checkedEvent = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignChecked).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("check-1-1"), solver.RaOf("check-2-1"), solver.RaOf("check-3-1"), 1.0);
            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.TotalSigmaArcmin, Is.EqualTo(Math.Round(expected.Total, 2)).Within(0.011));
            Assert.That(checkedEvent.GetProperty("total_sigma_arcmin").GetDouble(), Is.EqualTo(status.CheckResult.TotalSigmaArcmin));
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(status.CheckResult.TotalSigmaArcmin), "the status shows the verdict's σ");
        }

        [Test]
        public async Task Three_solves_that_agree_exactly_still_count_the_floor_over_root_three_per_point() {
            // Three frames per point and a solver that answers the same place every time: the measured scatter is 0, but
            // the point is no surer than the solver's own precision of a mean of three, 1″ / √3.
            var solver = new ScatterSolver();
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: StoreWith(s => s with { FramesPerPoint = 3 }), logger: logger);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"), 1.0 / Math.Sqrt(3.0));
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(expected.Total).Within(0.0051));
            var fit = logger.Entries.Single(e => e.EventId == 4528).Message;
            Assert.That(fit.Split("±0.58″ (floor)").Length - 1, Is.EqualTo(3), fit);
            // A measured scatter over the floor stands as measured.
            Assert.That(PolarAlignService.PointSigmaArcsec(new PolarAlignSolveOutcome(true, 0.0, 85.0, SigmaArcsec: 0.9, Solves: 3)), Is.EqualTo(0.9));
            Assert.That(PolarAlignService.PointSigmaArcsec(new PolarAlignSolveOutcome(true, 0.0, 85.0)), Is.EqualTo(1.0), "one solve: the floor");
        }

        [Test]
        public void A_point_whose_solves_run_on_in_RA_is_scatter_because_the_mount_tracks() {
            // #2170: every run turns RA with the motor and tracks while a point's frames are taken, so the field should
            // stand still. Three solves 5 s apart that run on in RA at the sidereal rate (about 6.5″ on the sky at Dec 85°
            // each time) are a real scatter, not a drift to take out.
            var t0 = new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
            var solves = Enumerable.Range(0, 3)
                .Select(i => (new PolarAlignSolveOutcome(true, 30.0 + i * 5.0 * SiderealRate, PoleMountDecDeg), t0.AddSeconds(i * 5.0)))
                .ToList();

            var (point, at, scatter) = PolarAlignService.AveragePointSolves(solves);

            Assert.That(scatter, Is.GreaterThan(4.0), "the solves disagree and the scatter says so");
            Assert.That(at, Is.EqualTo(t0.AddSeconds(5.0)), "the point stands at its frames' mean time");
            Assert.That(point.RaDegJnow, Is.EqualTo(30.0 + 5.0 * SiderealRate).Within(1e-7), "the plain mean of the solves");
            Assert.That(point.Solves, Is.EqualTo(3));
        }

        private const double SiderealRate = 360.98564736629 / 86400.0;

        [Test]
        public void The_default_settings_floor_keeps_two_sigma_over_the_default_tolerance_and_20_degree_turns_cut_it_four_times() {
            // #2072 review: the trade-off at the defaults, pinned. 1′ tolerance, 10° turns, 3 frames per point whose solves
            // agree, no pixel scale: the floor alone (1″ / √3 per point) keeps "aligned" from being confirmed however small
            // the error.
            var defaults = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto());
            var at = new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
            var floor = PolarAlignService.PointSigmaArcsec(new PolarAlignSolveOutcome(true, 0.0, PoleMountDecDeg, SigmaArcsec: 0.0, Solves: defaults.FramesPerPoint));
            double SigmaAt(double stepDeg) => PolarAlignGeometry.AxisErrorSigma(
                (30.0, PoleMountDecDeg, floor), (30.0 + stepDeg, PoleMountDecDeg, floor), (30.0 + 2 * stepDeg, PoleMountDecDeg, floor),
                northernHemisphere: true, SiteLatDeg, SiteLonDeg, at).TotalSigmaArcmin;

            var atDefault = SigmaAt(defaults.SeedRotationDeg);
            Assert.That(defaults.SeedRotationDeg, Is.EqualTo(10.0));
            Assert.That(PolarAlignService.RatedErrorArcmin(0.0, atDefault), Is.GreaterThan(defaults.TargetToleranceArcmin),
                $"a zero error ± {atDefault:F2}′ cannot be confirmed inside {defaults.TargetToleranceArcmin}′");
            // The cure the unconfirmed text names: 20° turns cut the σ about four times, and a small error is then confirmed.
            var atTwenty = SigmaAt(20.0);
            Assert.That(atDefault / atTwenty, Is.InRange(3.5, 4.5));
            Assert.That(PolarAlignService.RatedErrorArcmin(0.3, atTwenty), Is.LessThanOrEqualTo(defaults.TargetToleranceArcmin));
        }

        [Test]
        public async Task A_check_verdict_rates_the_error_plus_twice_its_sigma_so_0_2_plus_or_minus_1_3_is_not_excellent() {
            // Joey, 2026-11-21: the band rates error + 2σ, both at the 0.01′ the clients see.
            Assert.That(PolarAlignService.VerdictFor(PolarAlignService.RatedErrorArcmin(0.2, 1.3)), Is.EqualTo("very_good"));
            Assert.That(PolarAlignService.VerdictFor(PolarAlignService.RatedErrorArcmin(0.2, 0.4)), Is.EqualTo("excellent"));
            Assert.That(PolarAlignService.VerdictFor(PolarAlignService.RatedErrorArcmin(0.2, null)), Is.EqualTo("excellent"), "no σ: the error alone");

            // End to end: a single-frame check (1″ floor) whose error alone is "excellent".
            var solver = new ScatterSolver();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: NewStore());
            var r = (await RunThroughDoneAsync(svc).ConfigureAwait(false)).CheckResult!;

            Assert.That(r.TotalErrorArcmin, Is.LessThanOrEqualTo(1.0), "the error alone is in the excellent band");
            Assert.That(r.TotalErrorArcmin + 2 * r.TotalSigmaArcmin!.Value, Is.GreaterThan(1.0));
            Assert.That(r.Verdict, Is.Not.EqualTo("excellent"));
            Assert.That(r.Verdict, Is.EqualTo(PolarAlignService.VerdictFor(r.TotalErrorArcmin + 2 * r.TotalSigmaArcmin.Value)));
        }

        [Test]
        public void The_guiding_residual_disagrees_with_Polar_Align_by_their_combined_uncertainty() {
            static PolarAlignmentRecord Align(double error, double? sigma) =>
                new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, error, null, null, 0, "complete", FinalSigmaArcmin: sigma);

            // The 2026-11-19 night: ≥ 16′ ± 1.2′ against 0.2′ ± 1.1′ disagree.
            Assert.That(GuiderService.AboveAlign(16.0, 1.2, Align(0.2, 1.1)), Is.True);
            // 2.8′ apart with a combined 1σ of 0.58′: they disagree, where the old fixed 2′ margin said they did not.
            Assert.That(GuiderService.AboveAlign(3.0, 0.5, Align(0.2, 0.3)), Is.True);
            Assert.That(GuiderService.AboveAlign(3.0, 0.5, Align(0.2, null)), Is.False, "an older row keeps the 2′ margin");
            // A noisy alignment (± 2.5′) covers a 4.8′ gap that the fixed margin would have called a disagreement.
            Assert.That(GuiderService.AboveAlign(5.0, 0.5, Align(0.2, 2.5)), Is.False);
            Assert.That(GuiderService.AboveAlign(5.0, 0.5, Align(0.2, null)), Is.True);
        }
    }
}
