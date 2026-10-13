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
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Sequencer.Utility {

    /// <summary>
    /// #1264 — the exposure ladder an unguided rig climbs: the rungs the <c>TrackingProbe</c> tries at
    /// run start and the steps the <c>UnguidedExposureGuard</c> moves between during the run. Pure.
    /// </summary>
    public static class UnguidedExposureLadder {

        /// <summary>The default rungs, in seconds (issue #1264).</summary>
        public static readonly IReadOnlyList<double> DefaultRungs = new[] { 10.0, 30.0, 60.0, 120.0 };

        /// <summary>The shipped "trailed above this median eccentricity" bar (#1264): the probe's pass rule,
        /// the guard's step-down and the request default all share it.</summary>
        public const double DefaultEccentricityTolerance = 0.5;

        /// <summary>
        /// The rungs to try: <paramref name="rungs"/> (or the defaults) sorted, distinct, positive, and
        /// capped at the sky-limited / planned length <paramref name="maxExposureSeconds"/> (ignored when
        /// ≤ 0) and at twice the spec-sheet prior <paramref name="priorSeconds"/> (ignored when null or
        /// ≤ 0). The lowest rung always survives the caps — a probe needs a first measurement — but
        /// never exceeds the planned exposure itself when that is shorter than every rung (then the
        /// planned exposure is the only rung, unless a positive prior below it is inserted ahead of it,
        /// see below). When the caps leave a single rung and a positive prior sits below it, the prior
        /// becomes the first rung ([prior, cap]): a 5 s floor would otherwise give [10], make every
        /// probe inconclusive and hand the run the prior even when that one frame was clean. (The guard
        /// builds its own ladder with no prior, so this insertion never reaches it.) A
        /// cap ABOVE the ladder's top — the planned exposure, or 2 × the prior when that is lower —
        /// becomes the final rung (review of #1318): a mount that holds the top rung cleanly must get
        /// the chance to pass the planned 300 s, not have its plan cut to 120 s with nothing to step
        /// back up to.
        /// </summary>
        public static IReadOnlyList<double> Build(IReadOnlyList<double>? rungs, double maxExposureSeconds, double? priorSeconds) {
            var source = (rungs is { Count: > 0 } ? rungs : DefaultRungs)
                .Where(r => r > 0 && !double.IsNaN(r) && !double.IsInfinity(r))
                .Distinct()
                .OrderBy(r => r)
                .ToList();
            if (source.Count == 0) {
                source = DefaultRungs.ToList();
            }
            var cap = double.PositiveInfinity;
            if (maxExposureSeconds > 0) {
                cap = Math.Min(cap, maxExposureSeconds);
            }
            if (priorSeconds is double prior && prior > 0) {
                cap = Math.Min(cap, 2 * prior);
            }
            var kept = source.Where(r => r <= cap).ToList();
            if (!double.IsPositiveInfinity(cap) && kept.Count > 0 && cap > kept[^1] + 1e-9) {
                // The cap itself (the planned sub, or 2 × the prior, whichever is lower) is the final rung.
                kept.Add(cap);
            }
            if (kept.Count == 0) {
                // Everything is above the cap: measure at the cap itself (the planned exposure, or
                // twice the prior) rather than at nothing.
                kept.Add(double.IsPositiveInfinity(cap) ? source[0] : cap);
            }
            // A prior short enough that only one rung fits under 2 × prior (a 5 s floor gives [10])
            // would make every probe inconclusive and hand the run the prior even when that one
            // frame was clean. The prior itself becomes the first rung: [prior, 2 × prior].
            if (kept.Count == 1 && priorSeconds is double p && p > 0 && p < kept[0] - 1e-9) {
                kept.Insert(0, p);
            }
            return kept;
        }

        /// <summary>The next rung below <paramref name="current"/>, or <paramref name="current"/> when it
        /// is already at (or below) the lowest rung.</summary>
        public static double StepDown(IReadOnlyList<double> rungs, double current) {
            ArgumentNullException.ThrowIfNull(rungs);
            var below = rungs.Where(r => r < current).DefaultIfEmpty(double.NaN).Max();
            return double.IsNaN(below) ? current : below;
        }

        /// <summary>The next rung above <paramref name="current"/> that does not exceed
        /// <paramref name="maxExposureSeconds"/> (ignored when ≤ 0), or <paramref name="current"/> when
        /// there is none.</summary>
        public static double StepUp(IReadOnlyList<double> rungs, double current, double maxExposureSeconds) {
            ArgumentNullException.ThrowIfNull(rungs);
            var above = rungs.Where(r => r > current && (maxExposureSeconds <= 0 || r <= maxExposureSeconds))
                .DefaultIfEmpty(double.NaN).Min();
            return double.IsNaN(above) ? current : above;
        }
    }
}
