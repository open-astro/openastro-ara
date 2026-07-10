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

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>§59.4 — the optical design the user declares once in the wizard (§59.14). Drives which
    /// features Smart Focus trusts: an obstructed scope defocuses into donuts whose diameter is linear in
    /// defocus (a better magnitude key than HFR), a refractor has no donut at all, and
    /// <see cref="Other"/> means "assume nothing" — HFR-only magnitude, no side classification, exactly
    /// the pre-§59.4 behavior.</summary>
    public enum TelescopeType {
        Refractor,
        Sct,
        Mak,
        Rc,
        Newtonian,
        Other,
    }

    /// <summary>
    /// §59.4 — the per-telescope-type feature policy. Pure lookup tables; both the inverse map (magnitude
    /// key) and the side classifier (candidate feature set) consult it so a type change reshapes Smart
    /// Focus consistently. Kept in the Image layer beside its consumers — the daemon's settings DTO stores
    /// only the wire string and parses at the consumption site.
    /// </summary>
    public static class FocusFeatureProfile {

        /// <summary>Parse the §59.13 wire string (<c>refractor|sct|mak|rc|newtonian|other</c>).
        /// Null/unknown → <see cref="TelescopeType.Other"/> — the assume-nothing default, so a bad or
        /// future wire value can never crash or change behavior beyond "no §59.4 upgrades".</summary>
        public static TelescopeType Parse(string? wire) => wire switch {
            "refractor" => TelescopeType.Refractor,
            "sct" => TelescopeType.Sct,
            "mak" => TelescopeType.Mak,
            "rc" => TelescopeType.Rc,
            "newtonian" => TelescopeType.Newtonian,
            _ => TelescopeType.Other,
        };

        /// <summary>Obstructed designs image defocus as a donut whose OUTER diameter grows linearly with
        /// distance from focus — a straighter magnitude key than HFR where it holds (the inverse map still
        /// verifies monotonicity per calibration and falls back to HFR when the data disagrees).</summary>
        public static bool PrefersDonutMagnitudeKey(TelescopeType type) =>
            type is TelescopeType.Sct or TelescopeType.Mak or TelescopeType.Rc or TelescopeType.Newtonian;

        // §59.4 per-type side-classifier candidate sets (feature names as FocusSideClassifier registers
        // them). Refractors carry no donut, so donut-derived features would only ever qualify off noise;
        // obstructed scopes' side signature lives in the ring/shadow shape + skew. FWHM/donut sizes are
        // NOT side candidates on their respective types — they're (near-)symmetric magnitude proxies, and
        // arm-separating on them usually means the sweep was skewed, not the optics.
        private static readonly string[] RefractorSideFeatures =
            ["fwhm", "roundness", "peak_to_background", "radial_skew"];

        private static readonly string[] ObstructedSideFeatures =
            ["ring_thickness", "donut_shadow_depth", "radial_skew", "roundness", "peak_to_background"];

        /// <summary>The feature names the §59.3 side classifier may qualify for this type; empty for
        /// <see cref="TelescopeType.Other"/> (side classification disabled — direction stays heuristic).</summary>
        public static IReadOnlyList<string> SideFeatureNames(TelescopeType type) => type switch {
            TelescopeType.Refractor => RefractorSideFeatures,
            TelescopeType.Sct or TelescopeType.Mak or TelescopeType.Rc or TelescopeType.Newtonian => ObstructedSideFeatures,
            _ => Array.Empty<string>(),
        };
    }
}
