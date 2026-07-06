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

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// NEXTGEN §2/§3 — validation + assembly of a <c>GET /api/v1/planning/optimal-sub</c> request
/// into an <see cref="OptimalSubInputDto"/>. Standalone static validator, mirroring
/// <see cref="TonightSkyOverrides"/>: the endpoint's input handling stays decoupled from the
/// calculator itself.
/// <para><b>Merge order per field:</b> request override → active profile → Tier-0 generic-CMOS
/// default (<see cref="OptimalSubCalculator"/> constants). Every field that lands on a Tier-0
/// default is named (snake_case) in <c>AssumedDefaults</c> so the advice stays transparent —
/// advise, don't dictate. The imaging-train geometry (aperture / focal length / pixel size) has
/// no honest default: missing everywhere → a 400 telling the user to set up their optics.</para>
/// </summary>
/// <summary>§3.1 — the extra context the star-detectability floor needs beyond the calculator
/// input: the target's J2000 position in DEGREES (the planning-DTO convention — never RA
/// hours), the profile-snapshot seeing, and the single-frame field of view.</summary>
public sealed record StarFieldContext(
    double RaDeg, double DecDeg, double SeeingArcsec, double FovDeg2);

public static class OptimalSubOverrides {

    /// <summary>Validate + assemble the calculator input. Profile sections are fetched lazily —
    /// a fully-specified request never reads the profile. On success <c>Input</c> is non-null and
    /// <c>AssumedDefaults</c> lists the Tier-0-defaulted field names (possibly empty); on failure
    /// <c>Error</c> carries the human-readable 400 message. <c>StarField</c> is non-null iff the
    /// request supplied a target position (<c>raDeg</c>/<c>decDeg</c>) — §3.1's opt-in — AND the
    /// star inputs could be assembled; <c>StarUnavailableReason</c> carries the actionable
    /// explanation when they couldn't (unset profile sensor dimensions). The ADVISORY degrading
    /// must never fail the core window: clients pass a target whenever the sequence has one, so
    /// a 400 here would break the Glover figure for every not-fully-set-up profile. Explicit
    /// garbage (a supplied bad <c>sensorW</c>) still 400s per the validation above.</summary>
    public static (OptimalSubInputDto? Input, IReadOnlyList<string> AssumedDefaults, string? Error,
            StarFieldContext? StarField, string? StarUnavailableReason) Build(
            Func<OpticsSettingsDto> getOptics,
            Func<CameraElectronicsDto> getElectronics,
            Func<SiteSettingsDto> getSite,
            Func<FilterSetDto> getFilterSet,
            string? filter, double? bandwidthNm,
            double? readNoise, double? fullWell, double? ePerAdu, int? gain, double? qe,
            double? apertureMm, double? focalLengthMm, double? reducer, double? pixelUm,
            double? skyMag, int? bortle,
            double? noiseTolerancePct, double? headroom,
            double? raDeg = null, double? decDeg = null, double? seeingArcsec = null,
            int? sensorW = null, int? sensorH = null) {
        var assumed = new List<string>();

        // ── Per-field validation of supplied values (finite + range) ──────────────────────
        if (readNoise is { } rn && !(double.IsFinite(rn) && rn > 0)) {
            return Fail("Query parameter 'readNoise' must be a finite value > 0 (e⁻ RMS).");
        }
        if (fullWell is { } fw && !(double.IsFinite(fw) && fw > 0)) {
            return Fail("Query parameter 'fullWell' must be a finite value > 0 (e⁻).");
        }
        if (ePerAdu is { } ea && !(double.IsFinite(ea) && ea >= 0)) {
            return Fail("Query parameter 'ePerAdu' must be a finite value >= 0 (0 = unknown).");
        }
        if (gain is { } g && g < 0) {
            return Fail("Query parameter 'gain' must be >= 0.");
        }
        if (qe is { } q && !(double.IsFinite(q) && q > 0 && q <= 1.0)) {
            return Fail("Query parameter 'qe' must be a finite value in (0, 1].");
        }
        if (apertureMm is { } ap && !(double.IsFinite(ap) && ap > 0)) {
            return Fail("Query parameter 'apertureMm' must be a finite value > 0.");
        }
        if (focalLengthMm is { } fl && !(double.IsFinite(fl) && fl > 0)) {
            return Fail("Query parameter 'focalLengthMm' must be a finite value > 0.");
        }
        if (reducer is { } r && !(double.IsFinite(r) && r > 0 && r <= TonightSkyOverrides.MaxReducerFactor)) {
            return Fail($"Query parameter 'reducer' must be a finite value in (0, {TonightSkyOverrides.MaxReducerFactor}].");
        }
        if (pixelUm is { } pu && !(double.IsFinite(pu) && pu > 0)) {
            return Fail("Query parameter 'pixelUm' must be a finite value > 0.");
        }
        // Bounded to a generous physical range, not just finite: an extreme value (say 1000)
        // underflows the sky flux to 0 and the floor/ceiling to Infinity — which the JSON
        // response can't carry. Real skies run ~16 (inner city) to ~22 (pristine); [10, 30]
        // is roomy. The bortle path below is inherently bounded (1..9 → 18.0–22.0).
        if (skyMag is { } sm && !(double.IsFinite(sm) && sm is >= 10 and <= 30)) {
            return Fail("Query parameter 'skyMag' must be in [10, 30] mag/arcsec² (typical skies: ~16 inner-city to ~22 pristine).");
        }
        if (bortle is { } b && b is < 1 or > 9) {
            return Fail("Query parameter 'bortle' must be in 1..9.");
        }
        if (bandwidthNm is { } bw && !(double.IsFinite(bw) && bw > 0)) {
            return Fail("Query parameter 'bandwidthNm' must be a finite value > 0.");
        }
        if (noiseTolerancePct is { } nt && !(double.IsFinite(nt) && nt > 0 && nt <= 100)) {
            return Fail("Query parameter 'noiseTolerancePct' must be a finite value in (0, 100].");
        }
        if (headroom is { } hr && !(double.IsFinite(hr) && hr > 0 && hr <= 1.0)) {
            return Fail("Query parameter 'headroom' must be a finite value in (0, 1].");
        }

        // ── §3.1 star-detectability opt-in: a target position + its supporting fields ──────
        if ((raDeg is null) != (decDeg is null)) {
            return Fail("Supply 'raDeg' and 'decDeg' together — the star-count advice needs a full target position.");
        }
        // J2000 DEGREES by contract (matching every planning DTO). Deliberately rejects values
        // outside [0, 360) so a client that accidentally sends RA HOURS at least fails loudly
        // for RA ≥ 360°-equivalent mistakes — a stray 0–24 h value is indistinguishable from a
        // valid degree figure, hence the loud unit statement in the message.
        if (raDeg is { } ra && !(double.IsFinite(ra) && ra is >= 0 and < 360)) {
            return Fail("Query parameter 'raDeg' must be J2000 right ascension in DEGREES, in [0, 360) — not hours.");
        }
        if (decDeg is { } dec && !(double.IsFinite(dec) && Math.Abs(dec) <= 90)) {
            return Fail("Query parameter 'decDeg' must be J2000 declination in degrees, within ±90.");
        }
        if (seeingArcsec is { } se && !(double.IsFinite(se) && se > 0 && se <= 20)) {
            return Fail("Query parameter 'seeingArcsec' must be a finite FWHM in (0, 20] arcsec.");
        }
        if (sensorW is { } sw && sw < 1) {
            return Fail("Query parameter 'sensorW' must be >= 1 (pixels).");
        }
        if (sensorH is { } sh && sh < 1) {
            return Fail("Query parameter 'sensorH' must be >= 1 (pixels).");
        }
        if (raDeg is null && (seeingArcsec is not null || sensorW is not null || sensorH is not null)) {
            return Fail("'seeingArcsec', 'sensorW' and 'sensorH' only apply to the star-count advice — supply 'raDeg' and 'decDeg' with them.");
        }

        // ── Mutually exclusive pairs ───────────────────────────────────────────────────────
        if (filter is not null && bandwidthNm is not null) {
            return Fail("Supply either 'filter' (a name from your planning filter set) or 'bandwidthNm', not both.");
        }
        if (skyMag is not null && bortle is not null) {
            return Fail("Supply either 'skyMag' (mag/arcsec²) or 'bortle' (1..9), not both.");
        }

        // ── Filter passband: request bandwidth → named profile filter → broadband default ──
        double bandwidth;
        if (bandwidthNm is { } bwSupplied) {
            bandwidth = bwSupplied;
        } else if (!string.IsNullOrWhiteSpace(filter)) {
            var set = getFilterSet();
            var match = set.Filters?.FirstOrDefault(f =>
                string.Equals(f.Name?.Trim(), filter.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null) {
                var known = set.Filters is { Count: > 0 }
                    ? string.Join(", ", set.Filters.Select(f => $"'{f.Name}'"))
                    : "(none configured)";
                return Fail($"Unknown filter '{filter.Trim()}' — configured planning filters: {known}. "
                    + "Add it in Settings → Filter set, or pass 'bandwidthNm' directly.");
            }
            bandwidth = match.BandwidthNm > 0 ? match.BandwidthNm : OptimalSubCalculator.DefaultBandwidthNm(match.Kind);
        } else {
            bandwidth = OptimalSubCalculator.DefaultBroadbandBandwidthNm;
            assumed.Add("filter_bandwidth_nm");
        }

        // ── Electronics: request → profile (0/-1 = unset) → Tier-0 default ────────────────
        CameraElectronicsDto? electronics = null;
        CameraElectronicsDto Electronics() => electronics ??= getElectronics();

        double mergedReadNoise;
        if (readNoise is { } rnSupplied) {
            mergedReadNoise = rnSupplied;
        } else if (Electronics().ReadNoiseE > 0) {
            mergedReadNoise = Electronics().ReadNoiseE;
        } else {
            mergedReadNoise = OptimalSubCalculator.DefaultReadNoiseE;
            assumed.Add("read_noise_e");
        }

        double mergedFullWell;
        if (fullWell is { } fwSupplied) {
            mergedFullWell = fwSupplied;
        } else if (Electronics().FullWellE > 0) {
            mergedFullWell = Electronics().FullWellE;
        } else {
            mergedFullWell = OptimalSubCalculator.DefaultFullWellE;
            assumed.Add("full_well_e");
        }

        // e⁻/ADU is optional context (0 = unknown → the ADC-clip bound is skipped): no Tier-0
        // default exists, so an unset profile value simply flows through as 0 untagged.
        var mergedEPerAdu = ePerAdu ?? Math.Max(0, Electronics().ElectronsPerAdu);
        var mergedGain = gain ?? Electronics().Gain;

        double mergedQe;
        if (qe is { } qeSupplied) {
            mergedQe = qeSupplied;
        } else if (Electronics().QuantumEfficiencyPeak > 0) {
            mergedQe = Electronics().QuantumEfficiencyPeak;
        } else {
            mergedQe = OptimalSubCalculator.DefaultQuantumEfficiency;
            assumed.Add("quantum_efficiency");
        }

        // ── Imaging-train geometry: request → profile; NO honest default ──────────────────
        OpticsSettingsDto? optics = null;
        OpticsSettingsDto Optics() => optics ??= getOptics();

        var mergedAperture = apertureMm ?? Optics().ApertureMm;
        var mergedFocal = focalLengthMm ?? Optics().FocalLengthMm;
        var mergedPixel = pixelUm ?? Optics().PixelSizeUm;
        var mergedReducer = reducer ?? Optics().ReducerFactor;
        if (!(double.IsFinite(mergedAperture) && mergedAperture > 0
              && double.IsFinite(mergedFocal) && mergedFocal > 0
              && double.IsFinite(mergedPixel) && mergedPixel > 0)) {
            return Fail("The assembled imaging train is incomplete — aperture, focal length and pixel "
                + "size have no honest defaults. Set them up (Settings → Optics, including the new "
                + "aperture field) or supply 'apertureMm', 'focalLengthMm' and 'pixelUm' directly.");
        }
        if (!(double.IsFinite(mergedReducer) && mergedReducer > 0
              && mergedReducer <= TonightSkyOverrides.MaxReducerFactor)) {
            return Fail($"The assembled reducer factor ({mergedReducer}) is out of range — supply "
                + $"'reducer' in (0, {TonightSkyOverrides.MaxReducerFactor}] (your active profile's reducer is unset or invalid).");
        }

        // ── Sky brightness: request skyMag → request bortle → profile Bortle ──────────────
        // (Site is also the star-context seeing source below, so fetch it lazily once.)
        SiteSettingsDto? site = null;
        SiteSettingsDto Site() => site ??= getSite();
        var mergedSkyMag = skyMag
            ?? OptimalSubCalculator.SkyMagFromBortle(bortle ?? Site().BortleClass);

        var input = new OptimalSubInputDto(
            ReadNoiseE: mergedReadNoise,
            FullWellE: mergedFullWell,
            ElectronsPerAdu: mergedEPerAdu,
            Gain: mergedGain,
            PixelSizeUm: mergedPixel,
            ApertureMm: mergedAperture,
            FocalLengthMm: mergedFocal,
            ReducerFactor: mergedReducer,
            QuantumEfficiency: mergedQe,
            SkyMagPerArcsec2: mergedSkyMag,
            FilterBandwidthNm: bandwidth,
            NoiseTolerancePct: noiseTolerancePct ?? 5.0,
            SaturationHeadroomFraction: headroom ?? 0.8);

        // ── §3.1 star-field context (only when a target was supplied) ─────────────────────
        StarFieldContext? starField = null;
        string? starUnavailable = null;
        if (raDeg is { } targetRa && decDeg is { } targetDec) {
            // Sensor dimensions: request override → profile optics. Like the imaging-train
            // geometry, there is no honest default — but the star bound is an ADVISORY rider,
            // so an unset profile degrades it (with the actionable reason below) instead of
            // failing the core Glover window the caller actually asked for.
            var mergedSensorW = sensorW ?? Optics().SensorWidthPx;
            var mergedSensorH = sensorH ?? Optics().SensorHeightPx;
            if (mergedSensorW < 1 || mergedSensorH < 1) {
                starUnavailable = "Star-count advice needs your sensor dimensions (no honest default) — "
                    + "set them in Settings → Optics or supply 'sensorW' and 'sensorH'.";
                return (input, assumed, null, null, starUnavailable);
            }

            // Seeing: request override → profile site → Tier-0 typical seeing (2.5″), tagged.
            double mergedSeeing;
            if (seeingArcsec is { } seeingSupplied) {
                mergedSeeing = seeingSupplied;
            } else if (Site().TypicalSeeingArcsec > 0 && double.IsFinite(Site().TypicalSeeingArcsec)) {
                mergedSeeing = Site().TypicalSeeingArcsec;
            } else {
                mergedSeeing = 2.5;
                assumed.Add("seeing_arcsec");
            }

            // Single-frame FOV in deg² from the assembled train's plate scale — registration
            // happens per sub, so no mosaic enlargement applies here.
            var pixelScaleArcsec = 206.265 * mergedPixel / (mergedFocal * mergedReducer);
            var fovDeg2 = (mergedSensorW * pixelScaleArcsec / 3600.0)
                        * (mergedSensorH * pixelScaleArcsec / 3600.0);
            starField = new StarFieldContext(targetRa, targetDec, mergedSeeing, fovDeg2);
        }

        return (input, assumed, null, starField, starUnavailable);

        (OptimalSubInputDto?, IReadOnlyList<string>, string?, StarFieldContext?, string?) Fail(string message) =>
            (null, Array.Empty<string>(), message, null, null);
    }
}
