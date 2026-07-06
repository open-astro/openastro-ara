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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// NEXTGEN §3.1 slice 3 — the star-detectability floor: composes the
/// <see cref="StarDetectability"/> limiting-magnitude solver (slice 1) with the
/// HYG-validated <see cref="StarCountModel"/> (slice 2) into the user-visible bound —
/// "does a sub of length t contain enough registerable stars to align/stack?".
///
/// <para><b>Semantics (§3.1, advise-don't-dictate):</b> <c>t_stars</c> joins the
/// sub-exposure window as a FLOOR bound — when it exceeds the Glover read-noise floor,
/// stars (not read noise) are the binding constraint and the recommendation moves up to
/// it (capped by the saturation ceiling, exactly like the Glover floor is). It never
/// gates: a sequence may still use shorter subs; this is planning advice.</para>
///
/// <para><b>Snapshot semantics:</b> seeing is the PROFILE's <c>TypicalSeeingArcsec</c>
/// at computation time and the FOV is the SINGLE-FRAME field — registration happens per
/// sub, so a mosaic-enlarged planning FOV must not inflate the star budget.</para>
///
/// <para><b>Known bias (documented in <see cref="StarCountModel"/>):</b> counts
/// extrapolated beyond the catalog's mag-9 completeness run optimistic, so this floor
/// under-warns rather than over-warns; user-facing strings label the extrapolation.</para>
/// </summary>
public static class StarDetectabilityFloor {

    /// <summary>The advisory "comfortable" registration star budget per sub. Triangle-
    /// similarity alignment is workable from ~10 well-measured centroids; dithered /
    /// field-rotated stacks with outlier rejection want margin, and solve-per-sub
    /// workflows (ASTAP guidance) want ~30 — 20 sits in the middle of that guidance
    /// band. Advisory only; it never gates.</summary>
    public const double MinRegistrationStars = 20;

    /// <summary>Upper bound of the <see cref="FloorSec"/> search. A star floor beyond an
    /// hour is not actionable planning advice (guiding/wind/satellites dominate long
    /// before) — past this the field is reported as starved instead.</summary>
    public const double MaxFloorSec = 3600;

    private const double MinSearchSec = 0.01;

    /// <summary>Predicted stars in <paramref name="fovDeg2"/> square degrees whose per-sub
    /// SNR reaches <paramref name="snrThreshold"/> in a sub of <paramref name="exposureSec"/>:
    /// the <see cref="StarCountModel"/> cumulative density at the sub's limiting magnitude,
    /// scaled by the field area.</summary>
    public static double PredictedStars(
            OptimalSubInputDto input, double exposureSec, double seeingFwhmArcsec,
            double galacticLatitudeDeg, double fovDeg2, double snrThreshold) {
        if (!(double.IsFinite(fovDeg2) && fovDeg2 > 0)) {
            throw new ArgumentOutOfRangeException(nameof(fovDeg2),
                "field of view must be a positive, finite deg² figure");
        }
        var mLim = StarDetectability.LimitingMagnitude(input, exposureSec, seeingFwhmArcsec, snrThreshold);
        return StarCountModel.CumulativeStarsPerDeg2(mLim, galacticLatitudeDeg) * fovDeg2;
    }

    /// <summary><c>t_stars</c>: the shortest sub whose predicted registration-quality
    /// (SNR ≥ <see cref="StarDetectability.RegistrationSnr"/>) star count reaches
    /// <paramref name="minStars"/> — or null when even <see cref="MaxFloorSec"/> can't
    /// (a starved field for this rig/filter). Predicted count grows monotonically with
    /// exposure (deeper m_lim → strictly more stars), so a log-domain bisection
    /// converges unconditionally.</summary>
    public static double? FloorSec(
            OptimalSubInputDto input, double seeingFwhmArcsec, double galacticLatitudeDeg,
            double fovDeg2, double minStars = MinRegistrationStars) {
        if (!(double.IsFinite(minStars) && minStars > 0)) {
            throw new ArgumentOutOfRangeException(nameof(minStars),
                "the star budget must be positive and finite");
        }
        double StarsAt(double t) => PredictedStars(
            input, t, seeingFwhmArcsec, galacticLatitudeDeg, fovDeg2, StarDetectability.RegistrationSnr);

        if (StarsAt(MaxFloorSec) < minStars) {
            return null;
        }
        if (StarsAt(MinSearchSec) >= minStars) {
            return MinSearchSec;   // effectively unconstrained — any practical sub qualifies
        }
        var lo = Math.Log(MinSearchSec);
        var hi = Math.Log(MaxFloorSec);
        for (var i = 0; i < 60; i++) {
            var mid = (lo + hi) / 2.0;
            if (StarsAt(Math.Exp(mid)) >= minStars) {
                hi = mid;
            } else {
                lo = mid;
            }
        }
        return Math.Exp(hi);
    }

    /// <summary>Folds the star-detectability floor into a computed Glover window per the
    /// §3.1 presentation rules: the effective floor is <c>max(read-noise floor, t_stars)</c>,
    /// the recommendation is that floor capped by the saturation ceiling, and
    /// <see cref="OptimalSubResultDto.LimitingBound"/> reports <see cref="OptimalSubBound.StarFloor"/>
    /// when stars are the binding lower bound. The reported star counts are evaluated at the
    /// FINAL recommendation so the figures describe the sub the user is being advised to take.</summary>
    public static OptimalSubResultDto Augment(
            OptimalSubResultDto window, OptimalSubInputDto input, double seeingFwhmArcsec,
            double raDeg, double decDeg, double fovDeg2) {
        // RA here is DEGREE-flavoured (J2000 degrees, matching the catalog + planning DTOs) —
        // the ASCOM/telescope layer trades RA in HOURS; a stray hours value would silently
        // produce a plausible-but-wrong galactic latitude and with it wrong star counts.
        var galacticLatDeg = StarCountModel.GalacticLatitudeDeg(raDeg, decDeg);

        var starFloorSec = FloorSec(input, seeingFwhmArcsec, galacticLatDeg, fovDeg2);

        var effectiveFloor = Math.Max(window.FloorSec, starFloorSec ?? window.FloorSec);
        var viable = effectiveFloor <= window.CeilingSec;
        var recommended = Math.Min(effectiveFloor, window.CeilingSec);
        var starsBind = starFloorSec is { } sf && sf > window.FloorSec;
        var bound = !viable ? OptimalSubBound.SaturationCeiling
            : starsBind ? OptimalSubBound.StarFloor
            : OptimalSubBound.ReadNoiseFloor;

        var detected = PredictedStars(input, recommended, seeingFwhmArcsec,
            galacticLatDeg, fovDeg2, StarDetectability.DefaultDetectionSnr);
        var registration = PredictedStars(input, recommended, seeingFwhmArcsec,
            galacticLatDeg, fovDeg2, StarDetectability.RegistrationSnr);
        var mLimAtRecommended = StarDetectability.LimitingMagnitude(
            input, recommended, seeingFwhmArcsec, StarDetectability.RegistrationSnr);

        return window with {
            Viable = viable,
            LimitingBound = bound,
            RecommendedSec = recommended,
            StarFloorSec = starFloorSec,
            StarsDetectedPerSub = detected,
            StarsRegistrationPerSub = registration,
            StarReason = BuildReason(
                window, starFloorSec, recommended, registration, starsBind, mLimAtRecommended),
        };
    }

    private static string BuildReason(
            OptimalSubResultDto gloverWindow, double? starFloorSec, double recommendedSec,
            double registrationStars, bool starsBind, double mLimAtRecommended) {
        var inv = CultureInfo.InvariantCulture;
        string text;
        if (starFloorSec is null || registrationStars < MinRegistrationStars) {
            // Even the ceiling (or the search bound) can't reach the budget — a starved field.
            text = string.Format(inv,
                "~{0:0} registration-quality stars/sub at {1:0.#} s — thin for registration (~{2:0} wanted)",
                registrationStars, recommendedSec, MinRegistrationStars);
        } else if (starsBind) {
            text = string.Format(inv,
                "star floor {0:0.#} s: the {1:0.#} s read-noise floor is star-starved — stars, not read noise, set the floor",
                recommendedSec, gloverWindow.FloorSec);
        } else {
            text = string.Format(inv,
                "~{0:0} registration-quality stars/sub at {1:0.#} s — read noise remains the binding floor",
                registrationStars, recommendedSec);
        }
        // §3.1: extrapolation beyond the HYG catalog's mag-9 completeness is labelled as such.
        return mLimAtRecommended > 9.0
            ? text + " (star counts extrapolated beyond the catalog's mag-9 depth — optimistic)"
            : text;
    }
}
