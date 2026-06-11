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

        /// <summary>
        /// Fit <paramref name="points"/> with a weighted parabola <c>HFR = A·x² + B·x + C</c> (weights =
        /// star counts, so noisy low-star samples pull the curve less) and return the vertex. Returns
        /// <c>null</c> when there are fewer than <see cref="MinPoints"/> distinct positions or the system is
        /// singular (e.g. all samples collinear in x). A non-null result with <see cref="FocusCurveFitResult.IsUsable"/>
        /// false means the data didn't form an upward curve — widen the sweep or fall back.
        /// </summary>
        public static FocusCurveFitResult? FitParabolic(IReadOnlyList<FocusPoint> points) {
            ArgumentNullException.ThrowIfNull(points);
            if (CountDistinctPositions(points) < MinPoints) {
                return null;
            }

            // Weighted normal equations for y = A x² + B x + C:
            //   [S4 S3 S2][A]   [T2]
            //   [S3 S2 S1][B] = [T1]      with S_k = Σ w·x^k,  T_k = Σ w·y·x^k.
            //   [S2 S1 S0][C]   [T0]
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            double t0 = 0, t1 = 0, t2 = 0;
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount); // a 0-star point still counts, just minimally
                double x = p.Position, y = p.Hfr;
                double x2 = x * x;
                s0 += w;
                s1 += w * x;
                s2 += w * x2;
                s3 += w * x2 * x;
                s4 += w * x2 * x2;
                t0 += w * y;
                t1 += w * y * x;
                t2 += w * y * x2;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }

            // Solve the 3×3 system by Cramer's rule.
            double det = Det3(s4, s3, s2, s3, s2, s1, s2, s1, s0);
            if (Math.Abs(det) < 1e-12) {
                return null; // singular — not enough independent positions
            }
            double a = Det3(t2, s3, s2, t1, s2, s1, t0, s1, s0) / det;
            double b = Det3(s4, t2, s2, s3, t1, s1, s2, t0, s0) / det;
            double c = Det3(s4, s3, t2, s3, s2, t1, s2, s1, t0) / det;

            bool upward = a > 0 && double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);
            double bestPosition = upward ? -b / (2 * a) : double.NaN;
            double predictedHfr = upward ? (c - (b * b) / (4 * a)) : double.NaN;
            // An upward parabola whose vertex dips below zero HFR is a numerical artefact of noisy/sparse
            // data, not a real focus minimum — treat it as unusable so a caller never acts on it.
            bool usable = upward && predictedHfr > 0;

            // Weighted R² against the weighted mean of y.
            double meanY = t0 / s0;
            double ssRes = 0, ssTot = 0;
            foreach (var p in points) {
                double w = Math.Max(1, p.StarCount);
                double fit = a * p.Position * p.Position + b * p.Position + c;
                ssRes += w * (p.Hfr - fit) * (p.Hfr - fit);
                ssTot += w * (p.Hfr - meanY) * (p.Hfr - meanY);
            }
            double rSquared = ssTot > 0 ? 1 - ssRes / ssTot : 0;

            return new FocusCurveFitResult {
                Method = AFCurveFitting.PARABOLIC,
                BestPosition = bestPosition,
                PredictedHfr = predictedHfr,
                RSquared = rSquared,
                IsUsable = usable,
                WithinSampledRange = usable && bestPosition >= minX && bestPosition <= maxX,
            };
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
