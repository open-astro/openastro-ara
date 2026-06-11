#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Core.Model {

    /// <summary>One sampled point on a §59 autofocus V-curve: the focuser position and the HFR measured
    /// there, plus the star count used to weight the fit (more stars ⇒ a more trustworthy HFR).</summary>
    public readonly record struct FocusPoint(double Position, double Hfr, int StarCount);

    /// <summary>Result of fitting a §59 autofocus curve to a set of <see cref="FocusPoint"/>s.</summary>
    public sealed class FocusCurveFitResult {
        /// <summary>The focuser position of the curve minimum (predicted best focus).</summary>
        public double BestPosition { get; init; }
        /// <summary>The HFR the fit predicts at <see cref="BestPosition"/>. Always &gt; 0 when
        /// <see cref="IsUsable"/> is true; a fit whose vertex falls below zero (noisy/sparse data) is
        /// physically impossible and reported as not usable.</summary>
        public double PredictedHfr { get; init; }
        /// <summary>Weighted coefficient of determination (≤ 1; higher = tighter fit). Goes negative when the
        /// parabola fits worse than a flat line through the mean — itself a strong fall-back signal, so do NOT
        /// clamp to [0,1]. §59.8 falls back to hyperbolic when the parabolic R² &lt; 0.85.</summary>
        public double RSquared { get; init; }
        /// <summary>Which model produced this fit.</summary>
        public AFCurveFitting Method { get; init; }
        /// <summary>True when the fit is a real focus minimum: an upward (U-shaped) curve whose vertex is
        /// finite. A downward or degenerate fit (no minimum) is <c>false</c> — the caller should widen the
        /// sweep or fall back rather than trust <see cref="BestPosition"/>.</summary>
        public bool IsUsable { get; init; }
        /// <summary>True when <see cref="BestPosition"/> lies within the sampled position range; false means
        /// the minimum is an extrapolation beyond the sweep and the curve should be re-centred + re-run.</summary>
        public bool WithinSampledRange { get; init; }
    }

    /// <summary>
    /// §59.8 autofocus curve fitting. A defocused image's HFR traces a V (or U) against focuser position;
    /// the minimum is best focus. This fits that curve and returns the predicted best position + a quality
    /// score (R²). Pure math — no hardware, no image dependency — so it's fully unit-testable against
    /// synthetic V-curves and runs identically on every target.
    ///
    /// Parabolic (weighted least-squares, closed-form) is implemented here — NINA's default and the
    /// workhorse for well-behaved curves. Hyperbolic + trendline fits (§59.8 fallbacks for unusual star
    /// profiles, which need nonlinear optimisation) are a follow-up sub-PR; see PORT_TODO.
    /// </summary>
    public static class FocusCurveFit {

        /// <summary>Minimum distinct positions needed to fit a parabola.</summary>
        public const int MinPoints = 3;

        /// <summary>§59.8: below this parabolic R², <see cref="FitBest"/> falls back to a hyperbolic fit.</summary>
        public const double HyperbolicFallbackRSquared = 0.85;

        /// <summary>
        /// §59.8 best-of fit: fit both models and apply <see cref="SelectBest"/>. Returns <c>null</c> only
        /// when neither model can be fit at all. NOTE: empirically the parabola wins for essentially all
        /// well-behaved focus curves — a clean symmetric curve fits a parabola at R² ≈ 0.93 (above the
        /// fallback threshold), and the kinds of deviation that drop the parabola below it (asymmetry, a
        /// near-zero deep centre) also make the hyperbola unusable. So the hyperbolic branch is a faithful
        /// §59.8 safety net rather than a common path; the hyperbola's real use is as a user-selectable
        /// method via <see cref="FitHyperbolic"/> directly.
        /// </summary>
        public static FocusCurveFitResult? FitBest(IReadOnlyList<FocusPoint> points) =>
            SelectBest(FitParabolic(points), FitHyperbolic(points));

        /// <summary>
        /// §59.8 selection rule over two already-computed fits: keep the parabola when it's a usable minimum
        /// with R² ≥ <see cref="HyperbolicFallbackRSquared"/>; otherwise fall back to the hyperbola, returning
        /// whichever usable fit scores higher (parabola wins ties). Returns the parabola (possibly itself
        /// unusable/null) when the hyperbola isn't usable. Pure function — no data required to exercise every
        /// branch, including the threshold direction.
        /// </summary>
        public static FocusCurveFitResult? SelectBest(FocusCurveFitResult? parabola, FocusCurveFitResult? hyperbola) {
            if (parabola is { IsUsable: true } && parabola.RSquared >= HyperbolicFallbackRSquared) {
                return parabola; // a good parabola is the cheapest sufficient answer
            }
            if (hyperbola is not { IsUsable: true }) {
                return parabola; // hyperbola unusable → best we have is the parabola (possibly itself unusable)
            }
            if (parabola is not { IsUsable: true }) {
                return hyperbola;
            }
            return hyperbola.RSquared > parabola.RSquared ? hyperbola : parabola;
        }

        /// <summary>
        /// Fit <paramref name="points"/> with a weighted parabola <c>HFR = A·x² + B·x + C</c> (weights =
        /// star counts, so noisy low-star samples pull the curve less) and return the vertex. Returns
        /// <c>null</c> when there are fewer than <see cref="MinPoints"/> distinct positions or the system is
        /// singular (e.g. all samples collinear in x). A non-null result with <see cref="FocusCurveFitResult.IsUsable"/>
        /// false means the data didn't form an upward curve — widen the sweep or fall back.
        /// </summary>
        public static FocusCurveFitResult? FitParabolic(IReadOnlyList<FocusPoint> points) {
            ArgumentNullException.ThrowIfNull(points);
            var sol = SolveCentredParabola(points);
            if (sol is null) {
                return null;
            }
            var s = sol.Value;
            bool upward = s.A > 0 && double.IsFinite(s.A) && double.IsFinite(s.B) && double.IsFinite(s.C);
            double predictedHfr = upward ? (s.C - (s.B * s.B) / (4 * s.A)) : double.NaN;
            // Usable only when it's a real focus minimum: an upward curve whose vertex HFR is positive. A
            // downward curve OR a vertex that dips below zero HFR (a numerical artefact of noisy/sparse data)
            // is not usable — and in BOTH cases BestPosition is NaN, so a caller skipping the guard can't act
            // on a plausible-looking-but-invalid position.
            bool usable = upward && predictedHfr > 0;
            double bestPosition = usable ? s.XBar - s.B / (2 * s.A) : double.NaN;
            double rSquared = WeightedRSquared(points, p => {
                double x = p.Position - s.XBar;
                return s.A * x * x + s.B * x + s.C; // the parabola value IS the predicted HFR
            });

            return new FocusCurveFitResult {
                Method = AFCurveFitting.PARABOLIC,
                BestPosition = bestPosition,
                PredictedHfr = predictedHfr,
                RSquared = rSquared,
                IsUsable = usable,
                WithinSampledRange = usable && bestPosition >= s.MinX && bestPosition <= s.MaxX,
            };
        }

        /// <summary>
        /// Fit <paramref name="points"/> with a focus hyperbola <c>HFR = √(a² + b²·(x − c)²)</c> (§59.8
        /// fallback for unusual star profiles). The trick: squaring gives <c>HFR² = b²·x² − 2b²c·x +
        /// (a² + b²c²)</c>, a parabola in HFR² — so this reuses the same conditioned solve on
        /// <c>(position, HFR²)</c>, reads the vertex as best focus, and takes √ of the vertex value as the
        /// predicted minimum HFR. R² is reported back in HFR units so it's directly comparable with
        /// <see cref="FitParabolic"/>. Same null / <see cref="FocusCurveFitResult.IsUsable"/> contract.
        /// </summary>
        public static FocusCurveFitResult? FitHyperbolic(IReadOnlyList<FocusPoint> points) {
            ArgumentNullException.ThrowIfNull(points);
            var squared = new List<FocusPoint>(points.Count);
            foreach (var p in points) {
                squared.Add(new FocusPoint(p.Position, p.Hfr * p.Hfr, p.StarCount));
            }
            var sol = SolveCentredParabola(squared);
            if (sol is null) {
                return null;
            }
            var s = sol.Value;
            bool upward = s.A > 0 && double.IsFinite(s.A) && double.IsFinite(s.B) && double.IsFinite(s.C);
            double minHfrSq = upward ? (s.C - (s.B * s.B) / (4 * s.A)) : double.NaN; // = a², the squared min HFR
            bool usable = upward && minHfrSq > 0;
            double bestPosition = usable ? s.XBar - s.B / (2 * s.A) : double.NaN;
            double predictedHfr = usable ? Math.Sqrt(minHfrSq) : double.NaN;
            // R² in HFR units: the fitted hyperbola is √(the fitted HFR² parabola), clamped at 0 inside the √.
            double rSquared = WeightedRSquared(points, p => {
                double x = p.Position - s.XBar;
                return Math.Sqrt(Math.Max(0, s.A * x * x + s.B * x + s.C));
            });

            return new FocusCurveFitResult {
                Method = AFCurveFitting.HYPERBOLIC,
                BestPosition = bestPosition,
                PredictedHfr = predictedHfr,
                RSquared = rSquared,
                IsUsable = usable,
                WithinSampledRange = usable && bestPosition >= s.MinX && bestPosition <= s.MaxX,
            };
        }

        /// <summary>Centred parabola coefficients <c>y = A·(x−XBar)² + B·(x−XBar) + C</c> plus the sampled
        /// position range. Shared by the parabolic + hyperbolic fits (the hyperbola fits this on HFR²).</summary>
        private readonly record struct ParabolaSolution(double A, double B, double C, double XBar, double MinX, double MaxX);

        // Solve the weighted normal equations for a parabola, pre-centring positions for numerical
        // conditioning. Returns null for too-few-distinct-positions or a (relatively) singular system; the
        // caller decides usability from the sign of A and the vertex. No upward/sign judgement here.
        private static ParabolaSolution? SolveCentredParabola(IReadOnlyList<FocusPoint> points) {
            if (CountDistinctPositions(points) < MinPoints) {
                return null;
            }

            // Pre-centre positions on their weighted mean before fitting. Raw focuser positions (5k-50k steps
            // on SCT / R&P focusers) make x⁴ ≈ 10²⁰, which both destroys double precision in the normal
            // equations and inflates the determinant so far that a scale-fixed singularity test can never
            // fire. Solving in centred coords x' = x − x̄ keeps every moment well-scaled; the vertex shifts
            // back by x̄ at the end. (Weighting: a 0-star point still counts, just minimally.)
            double sw = 0, swx = 0;
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount);
                sw += w;
                swx += w * p.Position;
                if (p.Position < minX) minX = p.Position;
                if (p.Position > maxX) maxX = p.Position;
            }
            double xBar = swx / sw;

            // Weighted normal equations for y = A·x'² + B·x' + C (x' centred):
            //   [S4 S3 S2][A]   [T2]
            //   [S3 S2 S1][B] = [T1]      with S_k = Σ w·x'^k,  T_k = Σ w·y·x'^k.
            //   [S2 S1 S0][C]   [T0]
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            double t0 = 0, t1 = 0, t2 = 0;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount);
                double x = p.Position - xBar, y = p.Hfr;
                double x2 = x * x;
                s0 += w;
                s1 += w * x;
                s2 += w * x2;
                s3 += w * x2 * x;
                s4 += w * x2 * x2;
                t0 += w * y;
                t1 += w * y * x;
                t2 += w * y * x2;
            }

            // Solve the 3×3 system by Cramer's rule. Singularity test is RELATIVE — abs(det) against the
            // Hadamard scale of the diagonal (s4·s2·s0), an upper bound on |det| — so it's scale-invariant and
            // fires for a genuinely near-singular sweep (effectively < 3 distinct positions) at any focuser
            // step magnitude, not just small ones.
            double det = Det3(s4, s3, s2, s3, s2, s1, s2, s1, s0);
            double scale = s4 * s2 * s0;
            if (scale <= 0 || Math.Abs(det) < 1e-10 * scale) {
                return null;
            }
            double a = Det3(t2, s3, s2, t1, s2, s1, t0, s1, s0) / det;
            double b = Det3(s4, t2, s2, s3, t1, s1, s2, t0, s0) / det;
            double c = Det3(s4, s3, t2, s3, s2, t1, s2, s1, t0) / det;
            return new ParabolaSolution(a, b, c, xBar, minX, maxX);
        }

        // Weighted R² of a fit against the data, both in HFR units: 1 − Σw(hfr − model)² / Σw(hfr − meanHfr)².
        // `model` maps a point to its predicted HFR (the parabola value directly; the hyperbola's √ of it),
        // so parabolic + hyperbolic R² are reported in the same units and are directly comparable. Returns 0
        // for a degenerate (flat-HFR) data set.
        private static double WeightedRSquared(IReadOnlyList<FocusPoint> points, Func<FocusPoint, double> model) {
            double sw = 0, swy = 0;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount);
                sw += w;
                swy += w * p.Hfr;
            }
            double meanY = swy / sw;
            double ssRes = 0, ssTot = 0;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount);
                double fit = model(p);
                ssRes += w * (p.Hfr - fit) * (p.Hfr - fit);
                ssTot += w * (p.Hfr - meanY) * (p.Hfr - meanY);
            }
            return ssTot > 0 ? 1 - ssRes / ssTot : 0;
        }

        private static int CountDistinctPositions(IReadOnlyList<FocusPoint> points) {
            // Exact equality is fine for integer focuser step positions (the normal case). If the live sweep
            // ever feeds computed positions (start + i·step), round/quantize before fitting to avoid counting
            // logically-identical positions as distinct.
            var seen = new HashSet<double>();
            foreach (var p in points) {
                seen.Add(p.Position);
            }
            return seen.Count;
        }

        // Determinant of a 3×3 given row-major.
        private static double Det3(
            double a, double b, double c,
            double d, double e, double f,
            double g, double h, double i) =>
            a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }
}
