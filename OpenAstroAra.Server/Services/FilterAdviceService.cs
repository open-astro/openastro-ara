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
using System.Linq;

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>A target's emission character, derived from its catalog type. Emission-line objects
/// radiate in narrow lines (Hα/OIII/SII) that punch through light pollution through a narrowband
/// filter; continuum objects (starlight-driven) gain little from narrowband. <see cref="Mixed"/>
/// covers types that can be either (a generic "nebula", a cluster embedded in nebulosity);
/// <see cref="Unknown"/> types get NO advice — never guess.</summary>
public enum EmissionClass {
    Unknown,
    EmissionLine,
    Continuum,
    Mixed,
}

/// <summary>
/// NEXTGEN §1 — filter/emission-aware planning advice: target emission character × the user's
/// declared planning filter set × the site's Bortle → a recommended filter approach + a one-line
/// reason (with a sky-flux time-efficiency figure where meaningful). Pure static functions; the
/// guiding principle is <b>advise, don't dictate</b> — advice is a tag, never a gate, and this
/// slice deliberately leaves the 0–100 score untouched (the ±nudge is a recorded follow-up in
/// design/PORT_TODO.md).
/// </summary>
public static class FilterAdvice {

    /// <summary>The Bortle class from which broadband-only imaging of an emission target gets the
    /// sharpened "expect many hours" warning (and Mixed targets tip to narrowband/duoband).</summary>
    private const int BrightSkyBortle = 5;

    /// <summary>The Bortle class from which even broadband continuum targets get the
    /// "bright sky" caveat.</summary>
    private const int VeryBrightSkyBortle = 6;

    /// <summary>Emission character from the catalog type. Handles both OpenNGC codes (HII, EmN, PN,
    /// SNR, G/GPair/…, OCl, GCl, RfN, Neb, Cl+N) and the hardcoded starter catalog's plain names
    /// ("galaxy", "cluster", "nebula"). Anything unrecognised is <see cref="EmissionClass.Unknown"/>.</summary>
    public static EmissionClass ClassifyEmission(string? type) => type?.Trim() switch {
        // Emission-line objects: HII regions, emission nebulae, planetary nebulae, supernova remnants.
        "HII" or "EmN" or "PN" or "SNR" => EmissionClass.EmissionLine,
        // Continuum objects: galaxies, open/globular clusters, reflection nebulae.
        "G" or "GPair" or "GTrpl" or "GGroup" or "OCl" or "GCl" or "RfN" => EmissionClass.Continuum,
        "galaxy" or "cluster" => EmissionClass.Continuum,
        // Could be either: a generic nebula (emission or reflection), a cluster embedded in nebulosity.
        "Neb" or "Cl+N" or "nebula" => EmissionClass.Mixed,
        _ => EmissionClass.Unknown,
    };

    /// <summary>The recommended approach + one-line reason, or null when there is nothing honest to
    /// say: the filter set is empty (we don't know what the user owns) or the emission character is
    /// unknown. The reason embeds the sky-flux ratio (linear in passband — the
    /// <see cref="OptimalSubCalculator"/> P model) when a narrowband/duoband path is recommended.</summary>
    public static (FilterApproach Approach, string Reason)? Advise(
            EmissionClass emission, FilterSetDto filterSet, int bortleClass) {
        var filters = filterSet.Filters;
        if (filters is null || filters.Count == 0 || emission == EmissionClass.Unknown) {
            return null;
        }

        var monoNb = filters.Where(f => f.Kind is FilterKind.Ha or FilterKind.Oiii or FilterKind.Sii).ToList();
        var duo = filters.Where(f => f.Kind is FilterKind.Duo or FilterKind.Tri).ToList();
        var hasBroadband = filters.Any(f =>
            f.Kind is FilterKind.L or FilterKind.R or FilterKind.G or FilterKind.B or FilterKind.Osc);

        switch (emission) {
            case EmissionClass.EmissionLine when monoNb.Count > 0:
                return (FilterApproach.Narrowband,
                    $"Emission-line target — narrowband ({FilterNames(monoNb)}) sees "
                    + $"{SkyRatio(monoNb)}× less sky glow than broadband, efficient even under a Bortle {bortleClass} sky.");
            case EmissionClass.EmissionLine when duo.Count > 0:
                return (FilterApproach.Duoband,
                    $"Emission-line target — OSC + {FilterNames(duo)} cuts the sky glow "
                    + $"~{SkyRatio(duo)}× per Bayer channel vs unfiltered broadband.");
            case EmissionClass.EmissionLine:
                // Broadband-only kit. Honest about the cost; sharper under a bright sky, where
                // narrowband would earn its keep the most.
                return (FilterApproach.Broadband,
                    bortleClass >= BrightSkyBortle
                        ? $"Emission-line target with broadband-only filters under a Bortle {bortleClass} sky — "
                          + "expect many hours of integration; a dual-band filter would cut this dramatically."
                        : "Emission-line target with broadband-only filters — workable under your dark sky, "
                          + "but a narrowband/dual-band filter would still cut the integration needed.");

            case EmissionClass.Continuum: {
                var reason = "Broadband continuum target (starlight) — narrowband would starve it; shoot "
                    + (hasBroadband ? FilterNames(BroadbandOf(filterSet)) : "broadband");
                if (bortleClass >= VeryBrightSkyBortle) {
                    reason += $". Faint under a Bortle {bortleClass} sky — plan generous integration or a darker site";
                }
                return (FilterApproach.Broadband, reason + ".");
            }

            case EmissionClass.Mixed when bortleClass >= BrightSkyBortle && monoNb.Count > 0:
                return (FilterApproach.Narrowband,
                    $"Mixed emission/continuum target under a Bortle {bortleClass} sky — lead with narrowband "
                    + $"({FilterNames(monoNb)}) for the emission structure.");
            case EmissionClass.Mixed when bortleClass >= BrightSkyBortle && duo.Count > 0:
                return (FilterApproach.Duoband,
                    $"Mixed emission/continuum target under a Bortle {bortleClass} sky — OSC + "
                    + $"{FilterNames(duo)} favours the emission structure.");
            case EmissionClass.Mixed:
                return (FilterApproach.Broadband,
                    "Mixed emission/continuum target — broadband captures both components"
                    + (monoNb.Count > 0 || duo.Count > 0 ? "; add narrowband subs for the emission detail" : "")
                    + ".");

            default:
                return null;
        }
    }

    /// <summary>The user's filter that best represents an approach — what the Optimal-Sub figure is
    /// computed for. Narrowband prefers Hα (the deepest common line); broadband prefers L, then OSC.
    /// Null when the set has no filter of that approach.</summary>
    public static PlanningFilterDto? RepresentativeFilter(FilterSetDto filterSet, FilterApproach approach) {
        var filters = filterSet.Filters;
        if (filters is null || filters.Count == 0) {
            return null;
        }
        return approach switch {
            FilterApproach.Narrowband =>
                filters.FirstOrDefault(f => f.Kind == FilterKind.Ha)
                ?? filters.FirstOrDefault(f => f.Kind is FilterKind.Oiii or FilterKind.Sii),
            FilterApproach.Duoband =>
                filters.FirstOrDefault(f => f.Kind == FilterKind.Duo)
                ?? filters.FirstOrDefault(f => f.Kind == FilterKind.Tri),
            FilterApproach.Broadband =>
                filters.FirstOrDefault(f => f.Kind == FilterKind.L)
                ?? filters.FirstOrDefault(f => f.Kind == FilterKind.Osc)
                ?? filters.FirstOrDefault(f =>
                    f.Kind is FilterKind.R or FilterKind.G or FilterKind.B),
            _ => null,
        };
    }

    /// <summary>A filter entry's effective passband: its own bandwidth, or its kind's default.</summary>
    public static double EffectiveBandwidthNm(PlanningFilterDto filter) =>
        filter.BandwidthNm > 0 ? filter.BandwidthNm : OptimalSubCalculator.DefaultBandwidthNm(filter.Kind);

    /// <summary>Sky-flux advantage vs effective broadband, from the P model's linearity in passband
    /// (NEXTGEN §2): broadband 100 nm ÷ the narrowest of the user's line filters. "~14" for 7 nm.</summary>
    private static string SkyRatio(System.Collections.Generic.IReadOnlyList<PlanningFilterDto> nb) {
        var narrowest = nb.Min(EffectiveBandwidthNm);
        var ratio = OptimalSubCalculator.DefaultBroadbandBandwidthNm / Math.Max(narrowest, 0.1);
        return $"~{Math.Round(ratio):0}";
    }

    private static System.Collections.Generic.List<PlanningFilterDto> BroadbandOf(FilterSetDto set) =>
        set.Filters.Where(f =>
            f.Kind is FilterKind.L or FilterKind.R or FilterKind.G or FilterKind.B or FilterKind.Osc).ToList();

    private static string FilterNames(System.Collections.Generic.IReadOnlyList<PlanningFilterDto> filters) =>
        string.Join("/", filters.Select(f => f.Name).Take(3));
}
