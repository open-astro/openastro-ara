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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.8 — <see cref="FocusCurveFit.FitParabolic"/>. Synthetic V-curves (HFR = a·(x−x₀)² + c) give
    /// exact, deterministic expectations: the fit must recover the vertex, score R²≈1 on clean data, weight
    /// by star count, and refuse to report a focus minimum on degenerate / non-U data.
    /// </summary>
    [TestFixture]
    public class FocusCurveFitTest {

        // Sample a perfect parabola HFR = a(x-x0)^2 + c at evenly spaced positions.
        private static List<FocusPoint> Parabola(double a, double x0, double c, double from, double to, double step, int stars = 100) {
            var pts = new List<FocusPoint>();
            for (double x = from; x <= to + 1e-9; x += step) {
                pts.Add(new FocusPoint(x, a * (x - x0) * (x - x0) + c, stars));
            }
            return pts;
        }

        [Test]
        public void FitParabolic_recovers_the_vertex_of_a_clean_curve() {
            // Min at 500, HFR 2.0 there, rising to 7.0 at the ±100 edges.
            var pts = Parabola(a: 0.0005, x0: 500, c: 2.0, from: 400, to: 600, step: 25);
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.Method, Is.EqualTo(AFCurveFitting.PARABOLIC));
            Assert.That(fit.BestPosition, Is.EqualTo(500).Within(0.5));
            Assert.That(fit.PredictedHfr, Is.EqualTo(2.0).Within(0.01));
            Assert.That(fit.RSquared, Is.GreaterThan(0.999));
            Assert.That(fit.WithinSampledRange, Is.True);
        }

        [Test]
        public void FitParabolic_flags_an_extrapolated_minimum_outside_the_sweep() {
            // True vertex at 600, but we only sampled the rising left arm 400..520 — the min is beyond the
            // sampled max, so it must be flagged as an extrapolation (re-centre + re-run).
            var pts = Parabola(a: 0.0005, x0: 600, c: 2.0, from: 400, to: 520, step: 20);
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.BestPosition, Is.EqualTo(600).Within(1.0));
            Assert.That(fit.WithinSampledRange, Is.False);
        }

        [Test]
        public void FitParabolic_rejects_a_downward_curve_as_unusable() {
            // A ∩ shape has no focus minimum — IsUsable must be false (don't hand back a bogus position).
            var pts = Parabola(a: -0.0005, x0: 500, c: 10.0, from: 400, to: 600, step: 25);
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.False);
            Assert.That(double.IsNaN(fit.BestPosition), Is.True);
        }

        [Test]
        public void FitParabolic_scores_low_R2_on_non_parabolic_scatter() {
            // Zig-zag data that no parabola fits well → R² well below the §59.8 0.85 fallback threshold.
            var pts = new List<FocusPoint> {
                new(1, 5, 100), new(2, 2, 100), new(3, 6, 100), new(4, 1, 100), new(5, 7, 100),
                new(6, 2, 100), new(7, 6, 100), new(8, 3, 100), new(9, 5, 100),
            };
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.RSquared, Is.LessThan(0.85));
        }

        [Test]
        public void FitParabolic_weights_by_star_count_so_a_low_star_outlier_barely_moves_the_vertex() {
            // A clean curve (200 stars/point) plus one wildly off, single-star outlier off to one side.
            var pts = Parabola(a: 0.0005, x0: 500, c: 2.0, from: 400, to: 600, step: 25, stars: 200);
            pts.Add(new FocusPoint(440, 80, 1)); // garbage HFR, but only 1 star → tiny weight
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.BestPosition, Is.EqualTo(500).Within(10)); // outlier barely perturbs it
        }

        [Test]
        public void FitParabolic_rejects_a_vertex_with_negative_predicted_hfr() {
            // An upward parabola whose vertex dips below 0 HFR is a numerical artefact — must be unusable.
            var pts = Parabola(a: 0.0005, x0: 500, c: -5.0, from: 400, to: 600, step: 25);
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.PredictedHfr, Is.LessThan(0));
            Assert.That(fit.IsUsable, Is.False);
            Assert.That(fit.WithinSampledRange, Is.False);
            // BestPosition must be NaN whenever the fit isn't usable — same contract as the downward case,
            // so a caller that skips the IsUsable guard can't act on a plausible-looking-but-invalid position.
            Assert.That(double.IsNaN(fit.BestPosition), Is.True);
        }

        [Test]
        public void FitParabolic_is_accurate_at_large_focuser_positions() {
            // SCT / R&P focusers sit at ~50000 steps, where raw x⁴ ≈ 10²⁰ would wreck precision — the
            // pre-centred solve must still recover the vertex exactly.
            var pts = Parabola(a: 0.0008, x0: 48000, c: 2.5, from: 47000, to: 49000, step: 250);
            var fit = FocusCurveFit.FitParabolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.BestPosition, Is.EqualTo(48000).Within(1.0));
            Assert.That(fit.PredictedHfr, Is.EqualTo(2.5).Within(0.05));
            Assert.That(fit.RSquared, Is.GreaterThan(0.999));
            Assert.That(fit.WithinSampledRange, Is.True);
        }

        [Test]
        public void FitParabolic_returns_null_below_min_points() {
            var pts = new List<FocusPoint> { new(100, 5, 100), new(200, 3, 100) }; // only 2
            Assert.That(FocusCurveFit.FitParabolic(pts), Is.Null);
        }

        [Test]
        public void FitParabolic_returns_null_when_all_samples_share_one_position() {
            // Distinct y but one x → can't define a parabola (singular system).
            var pts = new List<FocusPoint> { new(500, 5, 100), new(500, 3, 100), new(500, 4, 100) };
            Assert.That(FocusCurveFit.FitParabolic(pts), Is.Null);
        }

        [Test]
        public void FitParabolic_throws_on_null_points() {
            Assert.Throws<ArgumentNullException>(() => FocusCurveFit.FitParabolic(null!));
        }

        // Sample a focus hyperbola HFR = sqrt(a^2 + b^2 (x-x0)^2): minimum HFR = a at x = x0.
        private static List<FocusPoint> Hyperbola(double a, double b, double x0, double from, double to, double step, int stars = 100) {
            var pts = new List<FocusPoint>();
            for (double x = from; x <= to + 1e-9; x += step) {
                double d = x - x0;
                pts.Add(new FocusPoint(x, Math.Sqrt(a * a + b * b * d * d), stars));
            }
            return pts;
        }

        [Test]
        public void FitHyperbolic_recovers_the_vertex_of_a_clean_hyperbola() {
            var pts = Hyperbola(a: 1.5, b: 0.05, x0: 500, from: 400, to: 600, step: 25);
            var fit = FocusCurveFit.FitHyperbolic(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.Method, Is.EqualTo(AFCurveFitting.HYPERBOLIC));
            Assert.That(fit.BestPosition, Is.EqualTo(500).Within(0.5));
            Assert.That(fit.PredictedHfr, Is.EqualTo(1.5).Within(0.02));
            Assert.That(fit.RSquared, Is.GreaterThan(0.999));
            Assert.That(fit.WithinSampledRange, Is.True);
        }

        [Test]
        public void FitHyperbolic_returns_null_below_min_points() {
            var pts = new List<FocusPoint> { new(100, 5, 100), new(200, 3, 100) };
            Assert.That(FocusCurveFit.FitHyperbolic(pts), Is.Null);
        }

        [Test]
        public void FitBest_uses_the_parabola_on_a_clean_parabolic_curve() {
            var pts = Parabola(a: 0.0005, x0: 500, c: 2.0, from: 400, to: 600, step: 25);
            var fit = FocusCurveFit.FitBest(pts);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.IsUsable, Is.True);
            Assert.That(fit.Method, Is.EqualTo(AFCurveFitting.PARABOLIC)); // good parabola ⇒ no fallback
            Assert.That(fit.BestPosition, Is.EqualTo(500).Within(0.5));
        }

        [Test]
        public void FitHyperbolic_models_a_sharp_v_better_than_a_parabola() {
            // The reason §59.8 keeps a hyperbolic fallback: on a sharp V (nearly straight arms) the hyperbola
            // is the correct model and the parabola is a compromise — so the hyperbola scores a higher R².
            var pts = Hyperbola(a: 0.5, b: 2.0, x0: 500, from: 450, to: 550, step: 10);
            var hyper = FocusCurveFit.FitHyperbolic(pts);
            var parab = FocusCurveFit.FitParabolic(pts);

            Assert.That(hyper!.IsUsable, Is.True);
            Assert.That(parab!.IsUsable, Is.True);
            Assert.That(hyper.RSquared, Is.GreaterThan(parab.RSquared));
            Assert.That(hyper.RSquared, Is.GreaterThan(0.999)); // hyperbola fits its own data essentially exactly
        }

        [Test]
        public void FitBest_keeps_a_good_parabola_even_on_hyperbolic_data() {
            // A clean symmetric hyperbola is still fit by a parabola at R² ≈ 0.93 (> the 0.85 threshold), so
            // FitBest keeps the cheaper parabola — and by symmetry its vertex is still the true centre.
            var pts = Hyperbola(a: 1.5, b: 0.05, x0: 500, from: 400, to: 600, step: 25);
            var best = FocusCurveFit.FitBest(pts);

            Assert.That(best, Is.Not.Null);
            Assert.That(best!.IsUsable, Is.True);
            Assert.That(best.Method, Is.EqualTo(AFCurveFitting.PARABOLIC));
            Assert.That(best.BestPosition, Is.EqualTo(500).Within(2.0));
        }

        [Test]
        public void FitBest_returns_null_when_nothing_can_be_fit() {
            var pts = new List<FocusPoint> { new(500, 5, 100), new(500, 4, 100) }; // one position, 2 points
            Assert.That(FocusCurveFit.FitBest(pts), Is.Null);
        }
    }
}
