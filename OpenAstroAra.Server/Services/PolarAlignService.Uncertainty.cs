#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2072 "PR A" — how sure each three-point fit is. On 2026-11-19 the live readout said 0.2′ while guiding later said
    /// at least 16′: a readout with no uncertainty cannot tell a good fit from a noisy one. Each point of a measurement
    /// carries the 1σ of its mean from the scatter of its frames (<see cref="PolarAlignSolveOutcome.SigmaArcsec"/>); the
    /// fit carries those through to a 1σ on the altitude and azimuth errors and on the total
    /// (<see cref="PolarAlignGeometry.AxisErrorSigma"/>). The readout shows it ("0.2′ ± 1.1′"), the fit's log line carries
    /// it with the axis and the solves, and Done waits until the error plus twice its 1σ is inside the tolerance
    /// (<see cref="AlignedWithinTolerance"/>).
    /// <para>The live readout keeps the seed fit's σ: each live frame adds its own solve noise once (about 1″, against
    /// the fit's roughly 80-fold amplification), which is left out. The σ measures only what the frames of one point
    /// show: a mount that shifts between points (flexure, a clutch, the turn itself) is not in it.</para>
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The 1σ per axis given to a point of a single solve, which has no scatter to measure: a FLOOR, not a
        /// measurement. §45.16's budget: a guide-scope solve returns the frame centre to about 0.1–0.2 px, about 1″ per
        /// axis at 3–5″/px. The mount's own jitter between frames (5–30″ by the same budget) can be far larger, so a
        /// single-frame fit is at least this uncertain. A point averaged from n solves is floored at this over √n.
        /// <para>The trade-off at the defaults (1′ tolerance, 10° turns, 3 frames per point): the fit amplifies a point's
        /// noise about 80-fold along the middle point, so the floor alone gives about 0.8′ of 1σ and error + 2σ stays over
        /// 1′ however small the error; 20° turns cut the σ about four times. Pinned by
        /// <c>PolarAlignServiceTest.The_default_settings_floor_keeps_two_sigma_over_the_default_tolerance_and_20_degree_turns_cut_it_four_times</c>.</para></summary>
        internal const double SingleSolveSigmaFloorArcsec = 1.0;

        /// <summary>A fit's 1σ on its altitude, azimuth and total errors (arcminutes), and the three solves it used
        /// (each "RA … Dec … ±σ″", for the fit's log line).</summary>
        internal sealed record FitUncertainty(double AltSigmaArcmin, double AzSigmaArcmin, double TotalSigmaArcmin, string Solves);

        // The current measurement's fit uncertainty: the seed's while the live readout runs, the check's once it has a
        // verdict. Null before a fit; cleared per run (StartAsync). Guarded by _gate.
        private FitUncertainty? _fitSigma;

        /// <summary>A point's 1σ per axis (arcseconds): its mean's from the scatter, but never under the solver's own
        /// precision of a mean of that many solves, <see cref="SingleSolveSigmaFloorArcsec"/> / √n (the single-solve floor
        /// for one solve). A few solves that happen to agree (or a solver that returns the same answer each time) give a
        /// small-sample scatter far under the truth, as low as 0: the floor keeps such a point from claiming more than
        /// the solver can reach.</summary>
        internal static double PointSigmaArcsec(PolarAlignSolveOutcome point) =>
            Math.Max(point.SigmaArcsec ?? 0.0, PointSigmaFloorArcsec(point));

        /// <summary>The solver's precision of a point's mean: the single-solve floor over √(its solves).</summary>
        private static double PointSigmaFloorArcsec(PolarAlignSolveOutcome point) =>
            SingleSolveSigmaFloorArcsec / Math.Sqrt(Math.Max(1, point.Solves));

        /// <summary>The point's σ is the floor (<see cref="PointSigmaArcsec"/>), not its measured scatter.</summary>
        private static bool PointSigmaIsFloor(PolarAlignSolveOutcome point) =>
            !(point.SigmaArcsec is double measured && measured >= PointSigmaFloorArcsec(point));

        /// <summary>One point's solves averaged (<see cref="PolarAlignGeometry.MeanDirection"/>) at their mean time, with
        /// the rms scatter about the mean and the mean's 1σ from it (<see cref="PolarAlignGeometry.MeanStandardErrorArcsec"/>,
        /// floored later by <see cref="PointSigmaArcsec"/>). With <paramref name="tracking"/> off (a hand-turn run may have
        /// it off) the field runs on with the Earth's turn between frames, about 1.3″/s at Dec 85°: each solve is first
        /// moved to the mean time, as the fit moves its points, so the scatter is the solves' own and not that drift.</summary>
        internal static (PolarAlignSolveOutcome Point, DateTimeOffset At, double ScatterArcsec) AveragePointSolves(
                IReadOnlyList<(PolarAlignSolveOutcome S, DateTimeOffset At)> solves, bool tracking) {
            ArgumentNullException.ThrowIfNull(solves);
            var first = solves[0].At;
            var meanAt = first + TimeSpan.FromTicks((long)solves.Average(p => (p.At - first).Ticks));
            var (ra, dec, scatter) = PolarAlignGeometry.MeanDirection(solves.Select(p =>
                (p.S.RaDegJnow + (tracking ? 0.0 : (meanAt - p.At).TotalSeconds * SiderealDegPerSecond), p.S.DecDegJnow)).ToList());
            var sigma = PolarAlignGeometry.MeanStandardErrorArcsec(scatter, solves.Count);
            return (new PolarAlignSolveOutcome(true, ra, dec, sigma, solves.Count), meanAt, scatter);
        }

        /// <summary>The uncertainty of the three-point fit of <see cref="FitThreePointOrFail"/> (and its hand-turn twin):
        /// each point moved to the last one's time by the Earth's turn, as the fit moves it, and its 1σ carried through
        /// the fit to the axis error at the last point's time.</summary>
        /// <exception cref="RoutineFailedException"><c>axis_fit_failed</c>, as the fit itself (a fit that just
        /// succeeded on these points does not refuse 1″ away from them).</exception>
        internal static FitUncertainty FitUncertaintyFor(
                (PolarAlignSolveOutcome S, DateTimeOffset At) p1, (PolarAlignSolveOutcome S, DateTimeOffset At) p2,
                (PolarAlignSolveOutcome S, DateTimeOffset At) p3, bool north, SiteSettingsDto site) {
            ArgumentNullException.ThrowIfNull(site);
            (double RaDeg, double DecDeg, double SigmaArcsec) ToRef((PolarAlignSolveOutcome S, DateTimeOffset At) p) =>
                (p.S.RaDegJnow + (p3.At - p.At).TotalSeconds * SiderealDegPerSecond, p.S.DecDegJnow, PointSigmaArcsec(p.S));
            var (a, b, c) = (ToRef(p1), ToRef(p2), ToRef(p3));
            (double Alt, double Az, double Total) sigma;
            try {
                sigma = PolarAlignGeometry.AxisErrorSigma(a, b, c, north, site.LatitudeDeg, site.LongitudeDeg, p3.At);
            } catch (ArgumentOutOfRangeException ex) {
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
            static string Solve((PolarAlignSolveOutcome S, DateTimeOffset At) p) => string.Create(CultureInfo.InvariantCulture,
                $"RA {p.S.RaDegJnow:F5}° Dec {p.S.DecDegJnow:F5}° ±{PointSigmaArcsec(p.S):F2}″{(PointSigmaIsFloor(p.S) ? " (floor)" : "")}");
            return new FitUncertainty(sigma.Alt, sigma.Az, sigma.Total, string.Join("; ", Solve(p1), Solve(p2), Solve(p3)));
        }

        /// <summary>#2072 — "aligned": the total error plus twice its 1σ is inside the tolerance, so a noisy fit cannot
        /// claim a small error. A readout with no σ yet (none before a fit) counts σ as 0. Both are taken at the 0.01′
        /// the clients see them (<c>polar_align.progress</c> and the status round to 2 decimals): a Done the app offers
        /// on the published numbers is never refused on the unrounded ones (1e-9 absorbs the sum's binary rounding).</summary>
        internal static bool AlignedWithinTolerance(double totalArcmin, double? sigmaArcmin, double toleranceArcmin) =>
            Math.Round(totalArcmin, 2) + 2 * Math.Round(sigmaArcmin ?? 0.0, 2) <= toleranceArcmin + 1e-9;

        private static double? RoundedSigma(double? sigmaArcmin) => sigmaArcmin is double s ? Math.Round(s, 2) : null;

        /// <summary>A check verdict's σ as the status and the session row carry it; null from a result without one.</summary>
        private static FitUncertainty? CheckSigma(PolarAlignCheckResultDto result) =>
            result.TotalSigmaArcmin is double total
                ? new FitUncertainty(result.AltitudeSigmaArcmin ?? 0.0, result.AzimuthSigmaArcmin ?? 0.0, total, Solves: "")
                : null;

        /// <summary>Records the current measurement's fit uncertainty (generation-fenced like the errors).</summary>
        private void SetFitSigma(int gen, FitUncertainty sigma) {
            lock (_gate) {
                if (_generation == gen) {
                    _fitSigma = sigma;
                }
            }
        }
    }
}
