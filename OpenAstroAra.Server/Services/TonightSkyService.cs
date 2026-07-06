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
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>§36/§25.5 Tonight's Sky — ranks a deep-sky list by a transparent, equipment-aware
/// "worth shooting tonight" score for the active profile's site and optical train.</summary>
public interface ITonightSkyService {
    /// <summary>The objects with a visibility window tonight, ranked by <see cref="TonightSkyObjectDto.Score"/>
    /// descending, capped at <paramref name="limit"/>. As an internal convenience a non-positive
    /// <paramref name="limit"/> returns all of them — but the public <c>GET /planning/tonight</c>
    /// endpoint rejects <c>limit &lt; 1</c> with a 400, so that path only applies to direct callers.
    /// Reads the site (lat/long/horizon/twilight/Bortle) and optical train from the active profile.
    /// <para><b>Inclusion gate (slice 2):</b> an object is listed when it has a non-empty dark window
    /// tonight — above the site horizon AND the sky dark enough — anywhere in the ±12 h around
    /// <paramref name="atUtc"/>, NOT merely when it is already up at the query instant. So a not-yet-risen
    /// target with a real window later tonight is surfaced (ranked, never silently dropped); the only
    /// drops are "never up in the dark tonight". Ranking advises rather than dictates: a low/short but
    /// well-framed bright target still scores respectably and appears.</para>
    /// <para><b>Per-request overrides (slice 4a):</b> <paramref name="opticsOverride"/> swaps in a
    /// different optical train (e.g. "what if I add a reducer / use another scope") instead of the
    /// active profile's — when null the profile's optics are used, so the default behaviour is
    /// unchanged. <paramref name="mosaicTilesX"/>/<paramref name="mosaicTilesY"/> enlarge the framing
    /// FOV by that tile count per axis (clamped to ≥ 1); the default 1×1 is a single frame.</para></summary>
    IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit,
        OpticsSettingsDto? opticsOverride = null, int mosaicTilesX = 1, int mosaicTilesY = 1);
}

public sealed class TonightSkyService : ITonightSkyService {
    private readonly IProfileStore _profileStore;
    private readonly ISkyCatalogService _catalog;
    private IReadOnlyList<CatalogObject>? _candidates; // the culled candidate set, cached once the real catalog loads

    // Cull bound: OpenNGC carries ~13k objects, most far too faint to image. A magnitude floor keeps
    // the candidate set to the realistically-shootable ones (and the per-object window math affordable).
    private const double MaxCandidateMagnitude = 12.0;

    public TonightSkyService(IProfileStore profileStore, ISkyCatalogService catalog) {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<TonightSkyObjectDto> GetTonight(DateTimeOffset atUtc, int limit,
            OpticsSettingsDto? opticsOverride = null, int mosaicTilesX = 1, int mosaicTilesY = 1) =>
        Rank(BuildCandidates(), _profileStore.GetSiteSettings(),
            opticsOverride ?? _profileStore.GetOpticsSettings(), atUtc, limit, mosaicTilesX, mosaicTilesY,
            // NEXTGEN §1 filter advice inputs — fetched once per request. Advice degrades
            // gracefully: an empty filter set → no advice; unset electronics/aperture → no
            // optimal-sub figure (the advice string still flows).
            _profileStore.GetFilterSet(), _profileStore.GetCameraElectronics(),
            _profileStore.GetCustomHorizon());

    /// <summary>The candidate set: the installed OpenNGC catalog culled to the realistically-shootable
    /// objects, or the hardcoded starter <see cref="Catalog"/> when openngc-dso isn't installed (or the
    /// cull leaves nothing) — so the endpoint still returns something useful with no sky-data.</summary>
    private IReadOnlyList<CatalogObject> BuildCandidates() {
        // Cache the culled set: the magnitude floor is static, so once the real catalog is loaded the
        // filtered list never changes. The hardcoded fallback is deliberately NOT cached — openngc-dso
        // can be installed at runtime (DataManager) and SkyCatalogService re-checks the file each call,
        // so staying uncached on the fallback lets a later install take effect without a daemon restart.
        // NOTE: a catalog *update* (re-downloading a newer openngc-dso over an installed one) is NOT
        // picked up live — both this cache and SkyCatalogService._dsoEntries are process-lifetime; a
        // re-cull on sky-data change is a later DataManager-integration slice.
        var cached = Volatile.Read(ref _candidates);
        if (cached is not null) {
            return cached;
        }
        var dsos = _catalog.GetAllDsos(CancellationToken.None);
        if (dsos is null || dsos.Count == 0) {
            return Catalog;
        }
        var list = new List<CatalogObject>(dsos.Count);
        foreach (var d in dsos) {
            // Keep it simple (slice 1): a magnitude floor. Objects with no recorded magnitude are
            // dropped here — size-based inclusion is a later slice's framing-fit concern.
            if (d.Magnitude is not { } mag || mag > MaxCandidateMagnitude) {
                continue;
            }
            list.Add(new CatalogObject(
                d.Name, d.CommonName ?? d.Name, d.Type, mag, d.RaDeg, d.DecDeg,
                d.MajAxArcmin, d.MinAxArcmin, d.PosAngleDeg, d.SurfaceBrightness));
        }
        if (list.Count == 0) {
            return Catalog;   // catalog present but cull empty (unexpected) — fall back, don't cache
        }
        IReadOnlyList<CatalogObject> result = list;
        return Interlocked.CompareExchange(ref _candidates, result, null) ?? result;
    }

    /// <summary>One deep-sky object: catalog id + common name, type, magnitude, J2000 RA/Dec (deg), and
    /// the optional apparent size / position angle / surface brightness when the catalog carried them.</summary>
    internal readonly record struct CatalogObject(
        string Id, string Name, string Type, double Magnitude, double RaDeg, double DecDeg,
        double? SizeMajArcmin = null, double? SizeMinArcmin = null,
        double? PosAngleDeg = null, double? SurfaceBrightness = null);

    // A small spread-across-the-sky starter set so something worthwhile is always up. J2000 positions.
    // The full smart-culled catalog (§36.8 / §55.1) supersedes this hardcoded list later.
    // Types use the OpenNGC codes (HII/PN/SNR/…) where the object has a definite class — the NEXTGEN §1
    // emission classifier reads them, and the generic "nebula" would file textbook narrowband targets
    // (the Crab, the Ring, the Owl) as merely Mixed. "galaxy"/"cluster" stay as friendly plain names
    // (they classify as Continuum either way); the Trifid keeps generic "nebula" honestly — it's a real
    // emission + reflection mix.
    internal static readonly IReadOnlyList<CatalogObject> Catalog = new[] {
        new CatalogObject("M31", "Andromeda Galaxy", "galaxy", 3.4, 10.685, 41.269),
        new CatalogObject("M33", "Triangulum Galaxy", "galaxy", 5.7, 23.462, 30.660),
        new CatalogObject("M45", "Pleiades", "cluster", 1.6, 56.750, 24.117),
        new CatalogObject("M1", "Crab Nebula", "SNR", 8.4, 83.633, 22.014),
        new CatalogObject("M42", "Orion Nebula", "HII", 4.0, 83.822, -5.391),
        new CatalogObject("NGC2237", "Rosette Nebula", "HII", 9.0, 97.950, 5.050),
        new CatalogObject("M81", "Bode's Galaxy", "galaxy", 6.9, 148.888, 69.065),
        new CatalogObject("M97", "Owl Nebula", "PN", 9.9, 168.699, 55.019),
        new CatalogObject("M104", "Sombrero Galaxy", "galaxy", 8.0, 189.998, -11.623),
        new CatalogObject("M63", "Sunflower Galaxy", "galaxy", 8.6, 198.955, 42.029),
        new CatalogObject("M51", "Whirlpool Galaxy", "galaxy", 8.4, 202.470, 47.195),
        new CatalogObject("M101", "Pinwheel Galaxy", "galaxy", 7.9, 210.802, 54.349),
        new CatalogObject("M13", "Hercules Cluster", "cluster", 5.8, 250.423, 36.461),
        new CatalogObject("M20", "Trifid Nebula", "nebula", 6.3, 270.600, -23.030),
        new CatalogObject("M8", "Lagoon Nebula", "HII", 6.0, 270.904, -24.387),
        new CatalogObject("M16", "Eagle Nebula", "HII", 6.0, 274.700, -13.807),
        new CatalogObject("M17", "Omega Nebula", "HII", 6.0, 275.196, -16.171),
        new CatalogObject("M57", "Ring Nebula", "PN", 8.8, 283.396, 33.029),
        new CatalogObject("M27", "Dumbbell Nebula", "PN", 7.4, 299.901, 22.721),
        new CatalogObject("NGC7000", "North America Nebula", "HII", 4.0, 314.750, 44.330),
    };

    /// <summary>Pure ranking over the hardcoded starter <see cref="Catalog"/>. Retained for the no-data
    /// path and the existing unit tests; <see cref="GetTonight"/> ranks the OpenNGC candidates instead.</summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(SiteSettingsDto site, DateTimeOffset atUtc, int limit) =>
        Rank(Catalog, site, atUtc, limit);

    /// <summary>Ranking with no optical train supplied — framing is <see cref="FramingFit.Unknown"/> for
    /// every object (neutral framing weight), so the order is driven by the timing/altitude/sky terms.
    /// Convenience for callers/tests that don't exercise the equipment-aware framing path.</summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(
            IReadOnlyList<CatalogObject> catalog, SiteSettingsDto site, DateTimeOffset atUtc, int limit) =>
        Rank(catalog, site, NoOptics, atUtc, limit);

    // An optical train with no usable values → FOV is NaN → framing classifies Unknown for everything.
    // Lets the no-optics overload share the one scoring path instead of branching the FOV math.
    private static readonly OpticsSettingsDto NoOptics = new(0, 0, 0, 0, 0);

    // Window scan resolution: sample altitude + sun-altitude every 5 minutes across ±12 h of atUtc.
    // 5 min is finer than the ~1-minute-per-degree-of-altitude rate near the horizon matters for an
    // "hours tonight" figure, and ±12 h always spans one whole night either side of any instant.
    private const int WindowStepMinutes = 5;
    private const int WindowHalfSpanMinutes = 12 * 60;
    // LST advances at this many degrees per solar day — the rate baked into LocalSiderealTimeDeg's GMST
    // term; reused to solve for the transit instant analytically (hour angle = 0).
    private const double SiderealDegPerDay = 360.98564736629;

    /// <summary>Ranks <paramref name="catalog"/> for <paramref name="site"/> + <paramref name="optics"/>
    /// at <paramref name="atUtc"/> by a transparent equipment-aware <see cref="TonightSkyObjectDto.Score"/>
    /// (descending, capped at <paramref name="limit"/>; catalog id breaks ties). Each returned object also
    /// carries its visibility window tonight, transit, integration + remaining hours, and framing fit.
    /// <para><b>Inclusion gate:</b> an object is listed iff it has a non-empty dark window in the ±12 h span
    /// — NOT merely "above the horizon at <paramref name="atUtc"/>". Not-yet-risen targets with a real
    /// window tonight are therefore surfaced; objects never up in the dark are the only drops.</para></summary>
    internal static IReadOnlyList<TonightSkyObjectDto> Rank(
            IReadOnlyList<CatalogObject> catalog, SiteSettingsDto site, OpticsSettingsDto optics,
            DateTimeOffset atUtc, int limit, int mosaicTilesX = 1, int mosaicTilesY = 1,
            FilterSetDto? filterSet = null, CameraElectronicsDto? electronics = null,
            CustomHorizonDto? customHorizon = null) {
        var horizon = site.DefaultHorizonAltitudeDeg;
        var lat = site.LatitudeDeg;
        var lon = site.LongitudeDeg;
        var lst0 = LocalSiderealTimeDeg(atUtc, lon);

        // The active (or per-request overridden) optical train's field of view (arcmin), enlarged by the
        // mosaic tile count per axis (clamped ≥ 1 defensively; FovArcmin also clamps). The default 1×1 is a
        // single frame; an unconfigured train yields a NaN FOV → every object frames Unknown.
        var (fovWidthArcmin, fovHeightArcmin) =
            FovArcmin(optics, Math.Max(1, mosaicTilesX), Math.Max(1, mosaicTilesY));

        // Precompute the night sample grid ONCE (it's object-independent): local sidereal time and
        // whether the sun is below the twilight threshold at each 5-min sample across ±12 h. Per object
        // we then only need its altitude at each sample (index stepsPerSide is exactly atUtc).
        var twilight = TwilightSunAltitudeDeg(site.TwilightDefinition);
        var stepsPerSide = WindowHalfSpanMinutes / WindowStepMinutes;
        var sampleCount = stepsPerSide * 2 + 1;
        var sampleUtc = new DateTimeOffset[sampleCount];
        var sampleLstDeg = new double[sampleCount];
        var sunIsDown = new bool[sampleCount];   // true = sun below the twilight threshold (dark enough)
        for (var i = 0; i < sampleCount; i++) {
            var t = atUtc.AddMinutes((i - stepsPerSide) * WindowStepMinutes);
            var lst = LocalSiderealTimeDeg(t, lon);
            var (sunRa, sunDec) = SunEquatorialDeg(t);
            var sunAlt = AltitudeFromHourAngleDeg(sunDec, lat, Mod360(lst - sunRa));
            sampleUtc[i] = t;
            sampleLstDeg[i] = lst;
            sunIsDown[i] = sunAlt < twilight;   // strictly below the twilight threshold = dark (photometric convention)
        }

        // Site terms are constant across every object and sample — compute the latitude trig and the
        // horizon threshold once. Comparing sin(alt) ≥ sin(horizon) is equivalent to alt ≥ horizon (asin
        // is monotonic over [−90,90]) and lets the per-sample loop skip the asin entirely.
        var sinLat = Math.Sin(Deg2Rad(lat));
        var cosLat = Math.Cos(Deg2Rad(lat));
        var sinHorizon = Math.Sin(Deg2Rad(horizon));

        // §36 custom terrain horizon — when the profile turns it on AND a skyline
        // was entered, "above the horizon" becomes per-azimuth. A 361-entry sin
        // lookup (one per whole azimuth degree, [360] duplicating [0] for the
        // wrap) keeps the per-sample sin-comparison trick; 1 degree of azimuth is
        // far finer than the 5-minute time grain resolves near the horizon.
        // horizonMin bounds the culmination pre-filter below: clearing the
        // skyline's LOWEST notch is necessary (not sufficient) to ever be up.
        var customPoints = site.UseCustomHorizon ? customHorizon?.Points : null;
        double[]? sinHorizonLut = null;
        var horizonMin = horizon;
        if (customPoints is { Count: > 0 }) {
            sinHorizonLut = new double[361];
            horizonMin = double.MaxValue;
            for (var azDeg = 0; azDeg <= 360; azDeg++) {
                var skyline = CustomHorizonValidator.AltitudeAtAzimuth(customPoints, azDeg);
                sinHorizonLut[azDeg] = Math.Sin(Deg2Rad(skyline));
                horizonMin = Math.Min(horizonMin, skyline);
            }
        }

        // §36.8 slice-4 moon advisory (display-only, deliberately NOT a score input — weighting is a
        // recorded follow-up for the user to tune). The moon's position per night sample is
        // object-independent, so precompute it on the same grid as the sun. "Up" uses the TRUE (0°)
        // horizon, not the site's imaging horizon: moonlight washes the sky whenever the moon is up at
        // all, trees or no trees. Illumination moves ~10%/day at the quarters — one value at atUtc
        // serves the whole request (it's rounded to a whole percent anyway).
        var moonRaDeg = new double[sampleCount];
        var moonDecDeg = new double[sampleCount];
        var moonUp = new bool[sampleCount];
        for (var i = 0; i < sampleCount; i++) {
            var (mRa, mDec) = MoonEquatorialDeg(sampleUtc[i]);
            moonRaDeg[i] = mRa;
            moonDecDeg[i] = mDec;
            moonUp[i] = AltitudeFromHourAngleDeg(mDec, lat, Mod360(sampleLstDeg[i] - mRa)) > 0.0;
        }
        var moonIlluminationPct = Math.Round(MoonIlluminatedFraction(atUtc) * 100.0);

        // NEXTGEN §1 — the per-approach Optimal-Sub floor (seconds) is object-independent (it's a
        // rig + sky + filter figure), so compute it at most once per approach across the whole
        // request. Null (no figure) unless the exposure-critical inputs are genuinely configured:
        // aperture + focal + pixel geometry AND user-entered read noise + full well — the tonight
        // list deliberately does NOT fall back to Tier-0 guesses the way /planning/optimal-sub
        // (which can *say* it assumed defaults) does. QE alone falls back to the calibrated 0.8.
        var adviceSet = filterSet ?? new FilterSetDto([]);
        var exposureConfigured = electronics is { ReadNoiseE: > 0, FullWellE: > 0 }
            && optics is { ApertureMm: > 0, FocalLengthMm: > 0, PixelSizeUm: > 0, ReducerFactor: > 0 };
        var skyMag = BortleZenithSkyMag(site.BortleClass);
        // Per-approach advice cache: the rounded floor for the DTO, plus (§3.1 slice 3) the
        // UNROUNDED recommendation + assembled input so the star-count tag can evaluate the
        // limiting magnitude at the exact advised sub (the rounded figure can be 0 s on a
        // fast broadband rig, which the m_lim solver rightly rejects).
        var adviceByApproach = new Dictionary<FilterApproach, (double? FloorSec, double RecommendedSec, OptimalSubInputDto? Input)>();
        (double? FloorSec, double RecommendedSec, OptimalSubInputDto? Input) AdviceFor(FilterApproach approach) {
            if (adviceByApproach.TryGetValue(approach, out var cached)) {
                return cached;
            }
            (double? FloorSec, double RecommendedSec, OptimalSubInputDto? Input) advice = (null, 0, null);
            if (exposureConfigured && FilterAdvice.RepresentativeFilter(adviceSet, approach) is { } filter) {
                var input = new OptimalSubInputDto(
                    ReadNoiseE: electronics!.ReadNoiseE,
                    FullWellE: electronics.FullWellE,
                    ElectronsPerAdu: Math.Max(0, electronics.ElectronsPerAdu),
                    Gain: electronics.Gain,
                    PixelSizeUm: optics.PixelSizeUm,
                    ApertureMm: optics.ApertureMm,
                    FocalLengthMm: optics.FocalLengthMm,
                    ReducerFactor: optics.ReducerFactor,
                    QuantumEfficiency: electronics.QuantumEfficiencyPeak > 0
                        ? electronics.QuantumEfficiencyPeak
                        : OptimalSubCalculator.DefaultQuantumEfficiency,
                    SkyMagPerArcsec2: skyMag,
                    FilterBandwidthNm: FilterAdvice.EffectiveBandwidthNm(filter));
                var result = OptimalSubCalculator.Compute(input);
                advice = (Math.Round(result.RecommendedSec), result.RecommendedSec, input);
            }
            adviceByApproach[approach] = advice;
            return advice;
        }

        // §3.1 slice 3 — star-detectability inputs for the "thin for registration" tag: the
        // SINGLE-FRAME field of view (registration happens per sub, so the mosaic-enlarged
        // planning FOV above must not inflate the star budget) and the profile-snapshot seeing.
        // Both advisory-degrading like everything here: unusable values → no tag, never an error.
        var (singleFrameWArcmin, singleFrameHArcmin) = FovArcmin(optics, 1, 1);
        var singleFrameFovDeg2 = singleFrameWArcmin / 60.0 * (singleFrameHArcmin / 60.0);
        var seeingArcsec = site.TypicalSeeingArcsec;
        var starTagAvailable = exposureConfigured
            && double.IsFinite(singleFrameFovDeg2) && singleFrameFovDeg2 > 0
            && double.IsFinite(seeingArcsec) && seeingArcsec > 0;
        // The registration-threshold limiting magnitude at each approach's advised sub is
        // object-independent — memoize it alongside the advice.
        var mLimByApproach = new Dictionary<FilterApproach, double?>();
        double? RegistrationMLimFor(FilterApproach approach) {
            if (mLimByApproach.TryGetValue(approach, out var cached)) {
                return cached;
            }
            double? mLim = null;
            var (_, recommendedSec, input) = AdviceFor(approach);
            if (starTagAvailable && input is not null && recommendedSec > 0) {
                mLim = StarDetectability.LimitingMagnitude(
                    input, recommendedSec, seeingArcsec, StarDetectability.RegistrationSnr);
            }
            mLimByApproach[approach] = mLim;
            return mLim;
        }

        // Score (the sort key) is the unrounded worth; id is the stable tie-break for an exact tie.
        var scored = new List<(double Score, TonightSkyObjectDto Dto)>(catalog.Count);
        var up = new bool[sampleCount];
        foreach (var o in catalog) {
            // Cheap pre-filter: an object whose geometric upper culmination never clears the site horizon
            // can never be up, so skip the costly ±12 h scan for it entirely.
            var peakAltDeg = MaxAltitudeDeg(o.DecDeg, lat);
            if (peakAltDeg < horizonMin) {
                continue;
            }

            // "Up" at a sample = object above the site horizon AND the sky dark enough (sun below twilight).
            // The object's dec trig is constant across all samples, so hoist it out; only cos(hour angle)
            // varies per sample. sin(alt) = sinδ·sinφ + cosδ·cosφ·cos H (same formula as AltitudeFromHourAngleDeg).
            var sinDec = Math.Sin(Deg2Rad(o.DecDeg));
            var cosDec = Math.Cos(Deg2Rad(o.DecDeg));
            for (var i = 0; i < sampleCount; i++) {
                var hDeg = sampleLstDeg[i] - o.RaDeg;
                var cosH = Math.Cos(Deg2Rad(hDeg));
                var sinAlt = sinDec * sinLat + cosDec * cosLat * cosH;
                if (sinHorizonLut is null) {
                    up[i] = sinAlt >= sinHorizon && sunIsDown[i];
                } else {
                    // Per-azimuth skyline: index the LUT by the object's compass
                    // bearing at this sample (rounding to 1 degree matches the
                    // LUT grain; Round(359.6) = 360 lands on the wrap entry).
                    var azDeg = AzimuthFromHourAngleDeg(o.DecDeg, lat, hDeg);
                    up[i] = sinAlt >= sinHorizonLut[(int)Math.Round(azDeg)] && sunIsDown[i];
                }
            }

            // Window tonight = the LONGEST qualifying dark run in the span, always — not the run that
            // merely brackets atUtc. An object that sets and re-rises (e.g. up 10pm–midnight then
            // 2am–6am) must report its best 4 h window even when the query falls in the shorter early run.
            var (start, end) = LongestRun(up);
            if (start < 0) {
                continue;   // no dark window tonight → never up in the dark → the only inclusion drop
            }

            var windowStart = sampleUtc[start];
            // Each "up" sample stands for its WindowStepMinutes slot, so the window's exclusive upper bound
            // is one step past the last up-sample. Without the +step the duration would count only the gaps
            // BETWEEN samples — short by one step — and a single-sample window would report 0 h.
            var windowEnd = sampleUtc[end].AddMinutes(WindowStepMinutes);
            var integrationHours = (windowEnd - windowStart).TotalHours;

            // RemainingHours = dark time still AHEAD of atUtc in this window. A window entirely in the past
            // → 0; one that hasn't started yet → its full length (all of it is ahead); one in progress →
            // from atUtc to its end. Equivalent: clamp(windowEnd − max(atUtc, windowStart)). Always ≤ the
            // integration hours, since it can at most span the whole window.
            var remainingHours = Math.Max(0.0, (windowEnd - (atUtc > windowStart ? atUtc : windowStart)).TotalHours);

            // Transit (upper culmination) nearest atUtc: hour angle H = LST − RA reaches 0. H grows at the
            // sidereal rate, so the nearest H=0 is H0 degrees in the past (if H0 ≤ 180) or 360−H0 ahead.
            var h0 = Mod360(lst0 - o.RaDeg);
            var signedDeg = h0 <= 180.0 ? -h0 : 360.0 - h0;
            var transitUtc = atUtc.AddHours(signedDeg / SiderealDegPerDay * 24.0);

            var altNow = AltitudeFromHourAngleDeg(o.DecDeg, lat, h0);
            var (score, framing, reasons) = ScoreObject(
                o, fovWidthArcmin, fovHeightArcmin, peakAltDeg, integrationHours, site.BortleClass);

            // NEXTGEN §1 — filter advice: a tag + reason only, deliberately NOT a score input
            // (the ±nudge is a recorded follow-up; existing scores must stay byte-identical).
            // The zero-point ScoreReasons tag makes the advice show in the "Why?" breakdown.
            FilterApproach? advice = null;
            string? adviceReason = null;
            double? optimalSubS = null;
            if (FilterAdvice.Advise(FilterAdvice.ClassifyEmission(o.Type), adviceSet, site.BortleClass)
                    is { } advised) {
                advice = advised.Approach;
                adviceReason = advised.Reason;
                optimalSubS = AdviceFor(advised.Approach).FloorSec;
                var reasonList = new List<string>(reasons) {
                    $"{AdviceTag(advised.Approach)} recommended (+0)",
                };

                // §3.1 slice 3 — the star-detectability tag: predicted registration-quality
                // stars in one advised sub over THIS object's field. Zero-point like the
                // filter/moon advisories (visible in "Why?", never a score input), and only
                // shown when thin — a healthy star budget isn't worth a line.
                if (RegistrationMLimFor(advised.Approach) is { } mLim && optimalSubS is { } subS) {
                    // Catalog RA/Dec are J2000 DEGREES (OpenNGC + the starter set) — the
                    // ASCOM/telescope layer trades RA in HOURS; a stray hours value here would
                    // silently yield a plausible-but-wrong galactic latitude and star count.
                    var galacticLatDeg = StarCountModel.GalacticLatitudeDeg(o.RaDeg, o.DecDeg);
                    var starsPerSub = StarCountModel.CumulativeStarsPerDeg2(mLim, galacticLatDeg)
                        * singleFrameFovDeg2;
                    if (starsPerSub < StarDetectabilityFloor.MinRegistrationStars) {
                        reasonList.Add($"~{starsPerSub:0} stars/sub at {subS:0.#} s — thin for registration (+0)");
                    }
                }
                reasons = reasonList;
            }

            // Moon context over THIS object's window: the fraction of the window with the moon up, and
            // the separation at the window midpoint (the moon moves ~0.5°/h — a few degrees across a
            // whole window, fine for a whole-degree advisory). Rides the same zero-point reason pattern
            // as the filter advice: visible in the "Why?" breakdown, never a score input.
            var moonUpCount = 0;
            for (var i = start; i <= end; i++) {
                if (moonUp[i]) moonUpCount++;
            }
            var moonUpFraction = (double)moonUpCount / (end - start + 1);
            var mid = (start + end) / 2;
            var moonSeparationDeg = AngularSeparationDeg(o.RaDeg, o.DecDeg, moonRaDeg[mid], moonDecDeg[mid]);
            reasons = new List<string>(reasons) {
                moonUpFraction > 0
                    ? $"moon {moonSeparationDeg:0}° away, {moonIlluminationPct:0}% lit (+0)"
                    : "moonless window (+0)",
            };

            scored.Add((score, new TonightSkyObjectDto(
                o.Id, o.Name, o.Type, o.Magnitude, o.RaDeg, o.DecDeg,
                Math.Round(altNow, 1), Math.Round(peakAltDeg, 1),
                o.SizeMajArcmin, o.SizeMinArcmin, o.PosAngleDeg, o.SurfaceBrightness,
                windowStart, windowEnd, transitUtc, Math.Round(integrationHours, 2),
                framing, Math.Round(score, 1), reasons, Math.Round(remainingHours, 2),
                advice, adviceReason, optimalSubS,
                Math.Round(moonSeparationDeg, 1), moonIlluminationPct,
                Math.Round(moonUpFraction, 2))));
        }
        if (scored.Count == 0) {
            return Array.Empty<TonightSkyObjectDto>();
        }
        // Highest worth first; catalog id is a stable tie-break for a genuine exact tie.
        scored.Sort((a, b) => {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.CompareOrdinal(a.Dto.Id, b.Dto.Id);
        });
        var capped = limit > 0 && scored.Count > limit ? scored.GetRange(0, limit) : scored;
        var result = new List<TonightSkyObjectDto>(capped.Count);
        foreach (var (_, dto) in capped) {
            result.Add(dto);
        }
        return result;
    }

    // ─── §36.8 slice 2: field-of-view, framing fit, and the transparent worth score ───
    //
    // Score weights (0–100, tunable starting values — documented in design/TONIGHT_SKY.md):
    //   Framing fit ........ 35  (dominant — the equipment-aware differentiator)
    //   Integration hours .. 25  (dark hours the window allows; saturates at 6 h)
    //   Peak altitude ...... 20  (low airmass; sin(peak alt) ≈ 1/airmass)
    //   Surface brightness . 12  (faint targets penalised under bright skies, never zeroed)
    //   Magnitude ..........  8  (brighter a touch higher)
    // Each component is graded onto a quality factor q∈[0,1] then weighted; the sum is clamped to [0,100].
    private const double FramingWeight = 35.0;
    private const double HoursWeight = 25.0;
    private const double AltitudeWeight = 20.0;
    private const double SurfaceBrightnessWeight = 12.0;
    private const double MagnitudeWeight = 8.0;

    // Framing thresholds: object major-axis ÷ the FOV's SMALLER dimension. < 0.10 is lost in the frame
    // (a ~10′ galaxy in a ~3° field at 448 mm); 0.10–0.80 fills a healthy fraction; > 0.80 overflows
    // (Orion's ~85′ in a ~27′ field at 3000 mm). The 0.80 cap leaves a margin so a "good" target isn't
    // cropped at the edges. Off-band targets keep a floor of worth (advise-don't-dictate), not zero.
    private const double FramingTooSmallRatio = 0.10;
    private const double FramingTooBigRatio = 0.80;
    private const double FramingFloorQ = 0.15;   // worst framing still keeps this fraction of the weight

    private const double HoursSaturationHours = 6.0;   // ≥ this many dark hours scores the full hours weight
    private const double SbContrastSpanMag = 4.0;      // a target this many mag fainter than sky → the SB floor
    private const double SbFloorQ = 0.15;              // faint-under-bright keeps this fraction, never zeroed
    private const double MagFaintFloor = 12.0;         // integrated magnitude at/above which the mag term is 0

    /// <summary>The active optical train's field of view in arcminutes (width, height). Pixel scale =
    /// 206.265·pixelµm ÷ (focalMm·reducer) arcsec/px; FOV = sensorPx·scale ÷ 60, times the mosaic tile
    /// count per axis. Returns (NaN, NaN) when the train is unconfigured (any non-positive term) so the
    /// framing classifier reports <see cref="FramingFit.Unknown"/> rather than dividing by zero.</summary>
    internal static (double WidthArcmin, double HeightArcmin) FovArcmin(
            OpticsSettingsDto optics, int mosaicTilesX, int mosaicTilesY) {
        var effectiveFocalMm = optics.FocalLengthMm * optics.ReducerFactor;
        if (effectiveFocalMm <= 0 || optics.PixelSizeUm <= 0 || optics.SensorWidthPx <= 0 || optics.SensorHeightPx <= 0) {
            return (double.NaN, double.NaN);
        }
        var pixelScaleArcsec = 206.265 * optics.PixelSizeUm / effectiveFocalMm;
        var width = optics.SensorWidthPx * pixelScaleArcsec / 60.0 * Math.Max(1, mosaicTilesX);
        var height = optics.SensorHeightPx * pixelScaleArcsec / 60.0 * Math.Max(1, mosaicTilesY);
        return (width, height);
    }

    /// <summary>Classifies an object's apparent major axis against the FOV's smaller dimension into a
    /// <see cref="FramingFit"/>. Unknown when the object has no recorded size or the FOV is unavailable.</summary>
    internal static FramingFit ClassifyFraming(double? sizeMajArcmin, double fovWidthArcmin, double fovHeightArcmin) {
        if (sizeMajArcmin is not { } maj || maj <= 0 || double.IsNaN(fovWidthArcmin) || double.IsNaN(fovHeightArcmin)) {
            return FramingFit.Unknown;
        }
        var minFov = Math.Min(fovWidthArcmin, fovHeightArcmin);
        if (minFov <= 0) {
            return FramingFit.Unknown;
        }
        var ratio = maj / minFov;
        if (ratio < FramingTooSmallRatio) {
            return FramingFit.TooSmall;
        }
        return ratio > FramingTooBigRatio ? FramingFit.TooBig : FramingFit.Good;
    }

    /// <summary>The transparent 0–100 worth score, its framing classification, and the short component
    /// reason tags (each carries its rounded point contribution so the UI/tests can explain "why 90 / why
    /// 40"). See the weight/threshold constants above and <c>design/TONIGHT_SKY.md</c>.</summary>
    private static (double Score, FramingFit Framing, IReadOnlyList<string> Reasons) ScoreObject(
            CatalogObject o, double fovWidthArcmin, double fovHeightArcmin,
            double peakAltDeg, double integrationHours, int bortleClass) {
        var reasons = new List<string>(5);

        // 1. Framing fit (dominant). Good = full; off-band targets are graded down by how far out of band
        //    they are but kept above FramingFloorQ so a well-but-not-perfectly-framed target still ranks.
        var framing = ClassifyFraming(o.SizeMajArcmin, fovWidthArcmin, fovHeightArcmin);
        double framingQ;
        string framingTag;
        if (framing == FramingFit.Unknown) {
            framingQ = 0.5;                       // neutral — no size to judge, neither rewarded nor punished
            framingTag = "size unknown";
        } else {
            var ratio = o.SizeMajArcmin!.Value / Math.Min(fovWidthArcmin, fovHeightArcmin);
            switch (framing) {
                case FramingFit.Good:
                    framingQ = 1.0;
                    framingTag = "fills the frame";
                    break;
                case FramingFit.TooSmall:
                    framingQ = Math.Max(FramingFloorQ, ratio / FramingTooSmallRatio);   // → 1 as it approaches the band
                    framingTag = "small in frame";
                    break;
                default:   // TooBig
                    framingQ = Math.Max(FramingFloorQ, FramingTooBigRatio / ratio);     // → 1 as it approaches the band
                    framingTag = "overflows the frame";
                    break;
            }
        }
        var framingScore = FramingWeight * framingQ;
        reasons.Add($"{framingTag} (+{framingScore:0})");

        // 2. Integration hours — linear ramp, saturating at HoursSaturationHours.
        var hoursScore = HoursWeight * Math.Clamp(integrationHours / HoursSaturationHours, 0.0, 1.0);
        reasons.Add($"{integrationHours:0.#} h dark window (+{hoursScore:0})");

        // 3. Peak altitude / airmass — sin(peak alt) tracks 1/airmass (full overhead, ~0.5 at 30°, 0 at the
        //    horizon). Uses the geometric transit altitude, the best the object reaches from this latitude.
        var altScore = AltitudeWeight * Math.Max(0.0, Math.Sin(Deg2Rad(peakAltDeg)));
        reasons.Add($"peak {peakAltDeg:0}° (+{altScore:0})");

        // 4. Surface brightness vs sky — a faint diffuse target loses contrast under a bright (high-Bortle)
        //    sky. Compare the object's mag/arcsec² to an approximate Bortle zenith sky brightness; a target
        //    brighter than the sky is full, fainter ramps down to a floor (penalised, never zeroed).
        double sbQ;
        string sbTag;
        if (o.SurfaceBrightness is { } sb) {
            var contrastMag = BortleZenithSkyMag(bortleClass) - sb;   // > 0 → target surface brighter than sky
            sbQ = Math.Clamp((contrastMag + SbContrastSpanMag) / SbContrastSpanMag, SbFloorQ, 1.0);
            sbTag = contrastMag >= 0 ? $"bright for Bortle {bortleClass} sky" : $"faint for Bortle {bortleClass} sky";
        } else {
            sbQ = 0.5;
            sbTag = "surface brightness unknown";
        }
        var sbScore = SurfaceBrightnessWeight * sbQ;
        reasons.Add($"{sbTag} (+{sbScore:0})");

        // 5. Integrated magnitude — brighter objects nudged up; saturates dark at MagFaintFloor.
        var magScore = MagnitudeWeight * Math.Clamp((MagFaintFloor - o.Magnitude) / MagFaintFloor, 0.0, 1.0);
        reasons.Add($"mag {o.Magnitude:0.#} (+{magScore:0})");

        var score = Math.Clamp(framingScore + hoursScore + altScore + sbScore + magScore, 0.0, 100.0);
        return (score, framing, reasons);
    }

    /// <summary>An approximate zenith sky surface brightness (mag/arcsec²) for a Bortle class —
    /// delegates to <see cref="OptimalSubCalculator.SkyMagFromBortle"/> so scoring and exposure
    /// planning share one sky model.</summary>
    private static double BortleZenithSkyMag(int bortleClass) =>
        OptimalSubCalculator.SkyMagFromBortle(bortleClass);

    /// <summary>The short "Why?"-breakdown label for a filter approach.</summary>
    private static string AdviceTag(FilterApproach approach) => approach switch {
        FilterApproach.Narrowband => "narrowband",
        FilterApproach.Duoband => "OSC + dual-band",
        _ => "broadband",
    };

    /// <summary>The longest contiguous run of <c>true</c> in <paramref name="flags"/> as inclusive
    /// (start, end) sample indices, or (−1, −1) when none is set.</summary>
    private static (int Start, int End) LongestRun(bool[] flags) {
        int bestStart = -1, bestEnd = -1, curStart = -1;
        for (var i = 0; i < flags.Length; i++) {
            if (flags[i] && curStart < 0) {
                curStart = i;   // a run begins
            }
            // Evaluate a run only when it ENDS (a false sample, or the final index) — one comparison per
            // run, not per element. Strict `>` keeps the EARLIEST of equal-length runs by design, so a
            // user can start sooner.
            var runEnds = curStart >= 0 && (!flags[i] || i == flags.Length - 1);
            if (runEnds) {
                var curEnd = flags[i] ? i : i - 1;
                if (bestStart < 0 || curEnd - curStart > bestEnd - bestStart) {
                    bestStart = curStart;
                    bestEnd = curEnd;
                }
                curStart = -1;
            }
        }
        return (bestStart, bestEnd);
    }

    /// <summary>Sun altitude below which the sky is "dark" for the given twilight definition:
    /// civil −6°, nautical −12°, astronomical −18°. Unrecognised → astronomical (the darkest, safest
    /// default for imaging).</summary>
    private static double TwilightSunAltitudeDeg(string? twilightDefinition) =>
        (twilightDefinition?.Trim().ToLowerInvariant()) switch {
            "civil" => -6.0,
            "nautical" => -12.0,
            "astronomical" => -18.0,
            _ => -18.0,
        };

    /// <summary>The sun's apparent equatorial coordinates (RA/Dec, degrees) at <paramref name="atUtc"/>,
    /// via Meeus, <i>Astronomical Algorithms</i> ch. 25 "low accuracy" solar position (≈0.01° — far finer
    /// than a twilight threshold needs): geometric mean longitude + equation of centre → true ecliptic
    /// longitude, then rotate by the obliquity of the ecliptic to equatorial. The sun's ecliptic latitude
    /// is taken as 0.</summary>
    internal static (double RaDeg, double DecDeg) SunEquatorialDeg(DateTimeOffset atUtc) {
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var t = (jd - 2451545.0) / 36525.0;                               // Julian centuries since J2000.0
        // Reduce L0 to [0,360) per Meeus §25 before the trig: L0 grows ~36000°/century, and runtimes
        // with weaker range reduction than glibc (Windows' Cody–Waite, WASM) lose precision on the raw
        // large argument far from J2000.
        var l0 = Mod360(280.46646 + 36000.76983 * t + 0.0003032 * t * t); // geometric mean longitude (deg)
        var m = Deg2Rad(Mod360(357.52911 + 35999.05029 * t - 0.0001537 * t * t)); // mean anomaly (rad)
        var c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
              + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
              + 0.000289 * Math.Sin(3 * m);                               // equation of centre (deg)
        var lambda = Deg2Rad(l0 + c);                                     // true ecliptic longitude (rad)
        var eps = Deg2Rad(23.439);                                        // mean obliquity (low-precision)
        var ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        var dec = Math.Asin(Math.Clamp(Math.Sin(eps) * Math.Sin(lambda), -1.0, 1.0));
        return (Mod360(Rad2Deg(ra)), Rad2Deg(dec));
    }

    /// <summary>The moon's apparent equatorial coordinates (RA/Dec, degrees) at <paramref name="atUtc"/>,
    /// via the Astronomical Almanac low-precision lunar series (the truncation Meeus ch. 47 is built
    /// from): mean longitude plus the six largest longitude terms and the four largest latitude terms,
    /// rotated by the mean obliquity to equatorial. Accuracy ≈ 0.3° — an advisory separation figure,
    /// not an ephemeris. Geocentric: topocentric parallax can shift the moon up to ~1°, immaterial for
    /// a whole-degree advisory readout.</summary>
    internal static (double RaDeg, double DecDeg) MoonEquatorialDeg(DateTimeOffset atUtc) {
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var t = (jd - 2451545.0) / 36525.0;                               // Julian centuries since J2000.0
        // Ecliptic longitude λ / latitude β (deg). Every trig argument goes through SinDeg's Mod360,
        // matching SunEquatorialDeg's range-reduction discipline (the raw arguments grow ~481000°/century).
        var lambda = Mod360(218.32 + 481267.881 * t)
            + 6.29 * SinDeg(135.0 + 477198.87 * t)
            - 1.27 * SinDeg(259.3 - 413335.36 * t)
            + 0.66 * SinDeg(235.7 + 890534.22 * t)
            + 0.21 * SinDeg(269.9 + 954397.74 * t)
            - 0.19 * SinDeg(357.5 + 35999.05 * t)
            - 0.11 * SinDeg(186.5 + 966404.03 * t);
        var beta = 5.13 * SinDeg(93.3 + 483202.02 * t)
            + 0.28 * SinDeg(228.2 + 960400.89 * t)
            - 0.28 * SinDeg(318.3 + 6003.15 * t)
            - 0.17 * SinDeg(217.6 - 407332.21 * t);
        var l = Deg2Rad(lambda);
        var b = Deg2Rad(beta);
        var eps = Deg2Rad(23.439);                                        // mean obliquity (low-precision)
        // Full ecliptic→equatorial rotation — the moon has real ecliptic latitude (up to ±5.1°),
        // so the sun's β=0 shortcut doesn't apply here.
        var x = Math.Cos(b) * Math.Cos(l);
        var y = Math.Cos(eps) * Math.Cos(b) * Math.Sin(l) - Math.Sin(eps) * Math.Sin(b);
        var z = Math.Sin(eps) * Math.Cos(b) * Math.Sin(l) + Math.Cos(eps) * Math.Sin(b);
        var ra = Math.Atan2(y, x);
        var dec = Math.Asin(Math.Clamp(z, -1.0, 1.0));
        return (Mod360(Rad2Deg(ra)), Rad2Deg(dec));
    }

    /// <summary>The fraction of the moon's disc that is illuminated at <paramref name="atUtc"/>, 0–1.
    /// From the sun–moon elongation ψ: with the sun treated as infinitely distant the phase angle is
    /// 180° − ψ, so k = (1 + cos(180° − ψ))/2 = (1 − cos ψ)/2 (Meeus ch. 48; the finite-distance
    /// correction moves k by well under a percent).</summary>
    internal static double MoonIlluminatedFraction(DateTimeOffset atUtc) {
        var (sunRa, sunDec) = SunEquatorialDeg(atUtc);
        var (moonRa, moonDec) = MoonEquatorialDeg(atUtc);
        var psi = Deg2Rad(AngularSeparationDeg(sunRa, sunDec, moonRa, moonDec));
        return (1.0 - Math.Cos(psi)) / 2.0;
    }

    /// <summary>Great-circle separation (degrees) between two equatorial positions:
    /// cos ψ = sin δ₁ sin δ₂ + cos δ₁ cos δ₂ cos Δα. Fine for advisory readouts (the acos form
    /// loses precision only below ~0.1°, far under a whole-degree display).</summary>
    internal static double AngularSeparationDeg(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
        var d1 = Deg2Rad(dec1Deg);
        var d2 = Deg2Rad(dec2Deg);
        var cosSep = Math.Sin(d1) * Math.Sin(d2)
                   + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(Deg2Rad(ra1Deg - ra2Deg));
        return Rad2Deg(Math.Acos(Math.Clamp(cosSep, -1.0, 1.0)));
    }

    /// <summary>Local apparent sidereal time in degrees (Meeus low-precision GMST + east-positive
    /// longitude). Arcminute-level accuracy — far finer than altitude ranking needs.</summary>
    internal static double LocalSiderealTimeDeg(DateTimeOffset atUtc, double longitudeDeg) {
        // Julian Date of the instant (Unix epoch JD = 2440587.5).
        var jd = atUtc.ToUnixTimeMilliseconds() / 86400000.0 + 2440587.5;
        var d = jd - 2451545.0; // days from J2000.0
        // Reduce GMST to [0,360) before combining, matching SunEquatorialDeg's L0 handling — the raw
        // value grows ~361°/day, so keep the same range-reduction discipline across the file.
        var gmst = Mod360(280.46061837 + 360.98564736629 * d);
        return Mod360(gmst + longitudeDeg);
    }

    /// <summary>Altitude (deg) of an object at hour angle <paramref name="hourAngleDeg"/> seen from
    /// <paramref name="latDeg"/>: sin(alt) = sin δ sin φ + cos δ cos φ cos H.</summary>
    internal static double AltitudeFromHourAngleDeg(double decDeg, double latDeg, double hourAngleDeg) {
        var dec = Deg2Rad(decDeg);
        var lat = Deg2Rad(latDeg);
        var h = Deg2Rad(hourAngleDeg);
        var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(h);
        return Rad2Deg(Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)));
    }

    /// <summary>Compass azimuth (deg from north, increasing eastward, [0,360)) of an object at hour
    /// angle <paramref name="hourAngleDeg"/> seen from <paramref name="latDeg"/>:
    /// az = atan2(−sin H · cos δ, sin δ · cos φ − cos δ · sin φ · cos H). The horizontal companion of
    /// <see cref="AltitudeFromHourAngleDeg"/>, used to index the §36 custom-horizon skyline.</summary>
    internal static double AzimuthFromHourAngleDeg(double decDeg, double latDeg, double hourAngleDeg) {
        var dec = Deg2Rad(decDeg);
        var lat = Deg2Rad(latDeg);
        var h = Deg2Rad(hourAngleDeg);
        var az = Math.Atan2(
            -Math.Sin(h) * Math.Cos(dec),
            Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(h));
        return Mod360(Rad2Deg(az));
    }

    /// <summary>The equatorial (J2000) coordinates of the point at horizontal altitude/azimuth
    /// <paramref name="altDeg"/>/<paramref name="azDeg"/> (azimuth measured from north, increasing
    /// eastward) seen from <paramref name="latDeg"/> at local sidereal time <paramref name="lstDeg"/>.
    /// The inverse of <see cref="AltitudeFromHourAngleDeg"/>; used to draw the local horizon as a curve
    /// on the equatorial atlas. At a geographic pole (cos φ ≈ 0) or for a point at a celestial pole
    /// (cos δ ≈ 0) the hour angle is degenerate, so it is pinned to 0 rather than dividing by zero.</summary>
    internal static (double RaDeg, double DecDeg) EquatorialFromAltAz(
            double altDeg, double azDeg, double latDeg, double lstDeg) {
        var alt = Deg2Rad(altDeg);
        var az = Deg2Rad(azDeg);
        var lat = Deg2Rad(latDeg);
        var sinDec = Math.Sin(alt) * Math.Sin(lat) + Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az);
        var dec = Math.Asin(Math.Clamp(sinDec, -1.0, 1.0));
        var cosLat = Math.Cos(lat);
        var cosDec = Math.Cos(dec);
        double hourAngleDeg;
        // 1e-6 ≈ 0.2 arcsec from a pole — finer than any overlay resolves, and well clear of the
        // ~1e8-magnitude sinH/cosH regime where one term could overflow before Atan2 normalises them.
        if (Math.Abs(cosLat) < 1e-6 || Math.Abs(cosDec) < 1e-6) {
            hourAngleDeg = 0.0;
        } else {
            var sinH = -Math.Sin(az) * Math.Cos(alt) / cosDec;
            var cosH = (Math.Sin(alt) - Math.Sin(dec) * Math.Sin(lat)) / (cosDec * cosLat);
            hourAngleDeg = Rad2Deg(Math.Atan2(sinH, cosH));
        }
        return (Mod360(lstDeg - hourAngleDeg), Rad2Deg(dec));
    }

    /// <summary>The object's highest possible altitude from this latitude — geometric upper culmination
    /// on the meridian: 90 − |φ − δ|, clamped to [−90, 90]. Ignores atmospheric refraction and the
    /// lower-transit altitude of circumpolar objects; sufficient as a ranking hint, not a precise rise.</summary>
    internal static double MaxAltitudeDeg(double decDeg, double latDeg) =>
        Math.Clamp(90.0 - Math.Abs(latDeg - decDeg), -90.0, 90.0);

    private static double Mod360(double x) {
        x %= 360.0;
        return x < 0 ? x + 360.0 : x;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    /// <summary>sin of an angle given in degrees, range-reduced first (see the Mod360 notes above —
    /// the lunar series' raw arguments are huge).</summary>
    private static double SinDeg(double deg) => Math.Sin(Deg2Rad(Mod360(deg)));
}
