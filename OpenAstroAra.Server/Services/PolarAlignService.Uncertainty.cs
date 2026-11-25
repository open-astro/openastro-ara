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
    /// it with the axis and the solves, and the verdict rates the error plus twice its 1σ
    /// (<see cref="VerdictFor"/>). Done is not held by the σ (Joey, 2026-11-21): the clients show "aligned" only when the
    /// error plus twice its 1σ is inside the tolerance, and otherwise say the measurement cannot confirm it.
    /// <para>The live readout keeps the seed fit's σ: each live frame adds its own solve noise once (about 1″, against
    /// the fit's roughly 80-fold amplification), which is left out. The σ measures only what the frames of one point
    /// show: a mount that shifts between points (flexure, a clutch, the turn itself) is not in it.</para>
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The solver's precision of one solve, in pixels per axis (Joey, 2026-11-21): §45.16's budget gives
        /// 0.1–0.2 px for the frame centre. The floor of a single solve is this times the frame's pixel scale
        /// (<see cref="PolarAlignSolveOutcome.PixelScaleArcsec"/>, the camera that took the point), per axis.</summary>
        internal const double SolvePrecisionPx = 0.15;

        /// <summary>The 1σ per axis given to a single solve when its pixel scale is unknown: a FLOOR, not a measurement
        /// (0.15 px at about 3–5″/px is about 0.5–0.75″; 1″ errs on the safe side). The mount's own jitter between frames
        /// (5–30″ by the same budget) can be far larger, so a single-frame fit is at least this uncertain. A point
        /// averaged from n solves is floored at its single-solve floor over √n.
        /// <para>The trade-off at the defaults (1′ tolerance, 10° turns, 3 frames per point): the fit amplifies a point's
        /// noise about 80-fold along the middle point, so this floor alone gives about 0.8′ of 1σ and error + 2σ stays
        /// over 1′ however small the error ("can't confirm"); 20° turns cut the σ about four times. Pinned by
        /// <c>PolarAlignServiceTest.The_default_settings_floor_keeps_two_sigma_over_the_default_tolerance_and_20_degree_turns_cut_it_four_times</c>.</para></summary>
        internal const double SingleSolveSigmaFloorArcsec = 1.0;

        /// <summary>One solve's floor per axis (arcseconds): <see cref="SolvePrecisionPx"/> × the frame's pixel scale, or
        /// <see cref="SingleSolveSigmaFloorArcsec"/> when the scale is unknown.</summary>
        internal static double SingleSolveFloorArcsec(double? pixelScaleArcsec) =>
            pixelScaleArcsec is double scale && double.IsFinite(scale) && scale > 0
                ? SolvePrecisionPx * scale
                : SingleSolveSigmaFloorArcsec;

        /// <summary>A fit's 1σ on its altitude, azimuth and total errors (arcminutes), the three solves it used
        /// (each "RA … Dec … ±σ″", for the fit's log line), and the single-solve floor it used and where from
        /// (<see cref="FloorText"/>).</summary>
        internal sealed record FitUncertainty(double AltSigmaArcmin, double AzSigmaArcmin, double TotalSigmaArcmin, string Solves,
            string Floor = "");

        // The current measurement's fit uncertainty: the seed's while the live readout runs, the verify measurement's once it has a
        // verdict. Null before a fit; cleared per run (StartAsync). Guarded by _gate.
        private FitUncertainty? _fitSigma;

        /// <summary>A point's 1σ per axis (arcseconds): its mean's from the scatter, but never under the solver's own
        /// precision of a mean of that many solves, <see cref="SingleSolveFloorArcsec"/> / √n (the single-solve floor
        /// for one solve). A few solves that happen to agree (or a solver that returns the same answer each time) give a
        /// small-sample scatter far under the truth, as low as 0: the floor keeps such a point from claiming more than
        /// the solver can reach.</summary>
        internal static double PointSigmaArcsec(PolarAlignSolveOutcome point) =>
            Math.Max(point.SigmaArcsec ?? 0.0, PointSigmaFloorArcsec(point));

        /// <summary>The solver's precision of a point's mean: the single-solve floor at its pixel scale over √(its solves).</summary>
        private static double PointSigmaFloorArcsec(PolarAlignSolveOutcome point) =>
            SingleSolveFloorArcsec(point.PixelScaleArcsec) / Math.Sqrt(Math.Max(1, point.Solves));

        /// <summary>The single-solve floor a fit's points used, for its log line: "0.15 px × 4.00″/px = 0.60″ per axis",
        /// or "1.00″ per axis (pixel scale unknown)".</summary>
        private static string FloorText(IEnumerable<PolarAlignSolveOutcome> points) =>
            string.Join(" / ", points.Select(p => p.PixelScaleArcsec is double scale && double.IsFinite(scale) && scale > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{SolvePrecisionPx:F2} px × {scale:F2}″/px = {SingleSolveFloorArcsec(scale):F2}″ per axis")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{SingleSolveSigmaFloorArcsec:F2}″ per axis (pixel scale unknown)"))
                .Distinct(StringComparer.Ordinal));

        /// <summary>The point's σ is the floor (<see cref="PointSigmaArcsec"/>), not its measured scatter.</summary>
        private static bool PointSigmaIsFloor(PolarAlignSolveOutcome point) =>
            !(point.SigmaArcsec is double measured && measured >= PointSigmaFloorArcsec(point));

        /// <summary>One point's solves averaged (<see cref="PolarAlignGeometry.MeanDirection"/>) at their mean time, with
        /// the rms scatter about the mean and the mean's 1σ from it (<see cref="PolarAlignGeometry.MeanStandardErrorArcsec"/>,
        /// floored later by <see cref="PointSigmaArcsec"/>). The mount tracks while a point's frames are taken, so the
        /// field stands still and the solves are averaged as they are.</summary>
        internal static (PolarAlignSolveOutcome Point, DateTimeOffset At, double ScatterArcsec) AveragePointSolves(
                IReadOnlyList<(PolarAlignSolveOutcome S, DateTimeOffset At)> solves) {
            ArgumentNullException.ThrowIfNull(solves);
            var first = solves[0].At;
            var meanAt = first + TimeSpan.FromTicks((long)solves.Average(p => (p.At - first).Ticks));
            var (ra, dec, scatter) = PolarAlignGeometry.MeanDirection(solves.Select(p =>
                (p.S.RaDegJnow, p.S.DecDegJnow)).ToList());
            var sigma = PolarAlignGeometry.MeanStandardErrorArcsec(scatter, solves.Count);
            // The point's frames come from one camera: its pixel scale sets the point's floor.
            var scale = solves.Select(p => p.S.PixelScaleArcsec).FirstOrDefault(v => v is double x && double.IsFinite(x) && x > 0);
            return (new PolarAlignSolveOutcome(true, ra, dec, SigmaArcsec: sigma, Solves: solves.Count, PixelScaleArcsec: scale), meanAt, scatter);
        }

        /// <summary>The uncertainty of the three-point fit of <see cref="FitThreePointOrFail"/>:
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
            return new FitUncertainty(sigma.Alt, sigma.Az, sigma.Total, string.Join("; ", Solve(p1), Solve(p2), Solve(p3)),
                FloorText([p1.S, p2.S, p3.S]));
        }

        /// <summary>#2072 — what the verdict rates (Joey, 2026-11-21): the total error plus twice its 1σ, so a noisy
        /// fit cannot read "excellent". A result with no σ counts σ as 0. Both are taken at the 0.01′ the clients see
        /// them, so the band the app shows beside "0.2′ ± 1.3′" is the daemon's.</summary>
        internal static double RatedErrorArcmin(double totalArcmin, double? sigmaArcmin) =>
            Math.Round(totalArcmin, 2) + 2 * Math.Round(sigmaArcmin ?? 0.0, 2);

        private static double? RoundedSigma(double? sigmaArcmin) => sigmaArcmin is double s ? Math.Round(s, 2) : null;

        /// <summary>A verdict's σ as the status and the session row carry it; null from a result without one.</summary>
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
