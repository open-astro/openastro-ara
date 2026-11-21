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
    /// line and the session row carry it; the rough step's Done waits until the error plus twice its 1σ is inside the
    /// tolerance.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A guide-camera solver that answers at the hint's RA on the Dec-85° circle, with
        /// <see cref="DecOffsetArcsec"/> added to the frames it names by id, and records each answer.</summary>
        private sealed class ScatterSolver : IPolarAlignFrameSolver {
            public Dictionary<string, double> DecOffsetArcsec { get; } = new(StringComparer.Ordinal);
            public List<(string Frame, double RaDeg)> Answers { get; } = new();
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                var frame = Path.GetFileNameWithoutExtension(fitsPath);
                var ra = hintRa ?? 0.0;
                lock (Answers) {
                    Answers.Add((frame, ra));
                }
                var offset = DecOffsetArcsec.TryGetValue(frame, out var arcsec) ? arcsec / 3600.0 : 0.0;
                return Task.FromResult(new PolarAlignSolveOutcome(true, ra, PoleMountDecDeg + offset));
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

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var progress = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignProgress).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"), 2.0 / Math.Sqrt(6.0));
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(expected.Total, Is.GreaterThan(0.01), "the scripted scatter gives the fit a real σ");
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(expected.Total).Within(2).Percent);
            Assert.That(status.AltitudeSigmaArcmin, Is.EqualTo(expected.Alt).Within(2).Percent);
            Assert.That(status.AzimuthSigmaArcmin, Is.EqualTo(expected.Az).Within(2).Percent);
            Assert.That(progress.GetProperty("total_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Total).Within(0.011));
            Assert.That(progress.GetProperty("altitude_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Alt).Within(0.011));
            Assert.That(progress.GetProperty("azimuth_sigma_arcmin").GetDouble(), Is.EqualTo(expected.Az).Within(0.011));
        }

        [Test]
        public async Task A_single_frame_point_counts_a_1_arcsec_floor_in_the_fit_sigma() {
            // One frame per point (the bench store): no scatter to measure, so each point counts 1″ per axis.
            var solver = new ScatterSolver();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("seed-a-1"), solver.RaOf("seed-b-1"), solver.RaOf("seed-c-1"), 1.0);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(expected.Total).Within(2).Percent);
        }

        [Test]
        public async Task The_seed_fit_log_line_carries_the_sigma_with_the_axis_and_the_solved_positions() {
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), new ScatterSolver(), NewPoleMount(), store: NewStore(), logger: logger);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
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
        public async Task Rough_Done_is_held_while_the_error_plus_two_sigma_is_over_the_tolerance_and_allowed_once_inside() {
            var log = new RecordingLog();
            var store = NewStore();
            using var svc = NewService(NewFake(), new HintFollowingSolver(), NewPoleMount(), store: store, log: log, mainCamera: new FakeMainCamera());

            await svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(status.Stage, Is.EqualTo(PolarAlignStages.Coarse));
            var total = status.CurrentErrorArcmin!.Value;
            var sigma = status.CurrentSigmaArcmin!.Value;
            Assert.That(sigma, Is.GreaterThan(0.01), "the single-frame floor gives the fit a σ");

            // Inside the tolerance, but not by twice its σ: held.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { TargetToleranceArcmin = total + sigma });
            await AssertThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("adjusting"), "still running");
            Assert.That(log.Rows, Is.Empty, "no complete row while the error plus 2σ is outside the tolerance");

            // Error + 2σ inside: Done completes.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { TargetToleranceArcmin = total + 2 * sigma + 0.01 });
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
            Assert.That(log.Rows.TryPeek(out var row) && row.Outcome == "complete", Is.True);
            Assert.That(row!.FinalSigmaArcmin, Is.EqualTo(sigma).Within(1e-9), "the row keeps the readout's σ");
        }

        [Test]
        public async Task A_check_verdict_carries_its_fit_sigma_on_the_result_the_event_and_the_status() {
            var solver = new ScatterSolver();
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), ws, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);
            var checkedEvent = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignChecked).ConfigureAwait(false);

            var expected = ExpectedSigma(solver.RaOf("check-1-1"), solver.RaOf("check-2-1"), solver.RaOf("check-3-1"), 1.0);
            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.TotalSigmaArcmin, Is.EqualTo(Math.Round(expected.Total, 2)).Within(0.011));
            Assert.That(checkedEvent.GetProperty("total_sigma_arcmin").GetDouble(), Is.EqualTo(status.CheckResult.TotalSigmaArcmin));
            Assert.That(status.CurrentSigmaArcmin, Is.EqualTo(status.CheckResult.TotalSigmaArcmin), "the status shows the verdict's σ");
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
