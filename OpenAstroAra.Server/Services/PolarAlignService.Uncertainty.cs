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
using System.Globalization;

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
        /// single-frame fit is at least this uncertain.</summary>
        internal const double SingleSolveSigmaFloorArcsec = 1.0;

        /// <summary>A fit's 1σ on its altitude, azimuth and total errors (arcminutes), and the three solves it used
        /// (each "RA … Dec … ±σ″", for the fit's log line).</summary>
        internal sealed record FitUncertainty(double AltSigmaArcmin, double AzSigmaArcmin, double TotalSigmaArcmin, string Solves);

        // The current measurement's fit uncertainty: the seed's while the live readout runs, the check's once it has a
        // verdict. Null before a fit; cleared per run (StartAsync). Guarded by _gate.
        private FitUncertainty? _fitSigma;

        /// <summary>A point's 1σ per axis (arcseconds): its mean's from the scatter, or the single-solve floor.</summary>
        internal static double PointSigmaArcsec(PolarAlignSolveOutcome point) =>
            point.SigmaArcsec ?? SingleSolveSigmaFloorArcsec;

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
                $"RA {p.S.RaDegJnow:F5}° Dec {p.S.DecDegJnow:F5}° ±{PointSigmaArcsec(p.S):F2}″{(p.S.SigmaArcsec is null ? " (floor)" : "")}");
            return new FitUncertainty(sigma.Alt, sigma.Az, sigma.Total, string.Join("; ", Solve(p1), Solve(p2), Solve(p3)));
        }

        /// <summary>#2072 — "aligned": the total error plus twice its 1σ is inside the tolerance, so a noisy fit cannot
        /// claim a small error. A readout with no σ yet (none before a fit) counts σ as 0.</summary>
        internal static bool AlignedWithinTolerance(double totalArcmin, double? sigmaArcmin, double toleranceArcmin) =>
            totalArcmin + 2 * (sigmaArcmin ?? 0.0) <= toleranceArcmin;

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
