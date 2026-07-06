#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// NEXTGEN §2/§3 — assembly of a <c>GET /planning/optimal-sub</c> request into calculator
    /// input: the request → profile → Tier-0-default merge order per field, the transparency
    /// contract (every Tier-0-defaulted field named in <c>assumed_defaults</c>), filter-name
    /// resolution against the planning filter set, the XOR pairs, and the no-honest-default
    /// geometry rule.
    /// </summary>
    [TestFixture]
    public class OptimalSubOverridesTest {

        private static readonly string[] OnlyBandwidthAssumed = ["filter_bandwidth_nm"];
        private static readonly string[] AllTier0Assumed =
            ["filter_bandwidth_nm", "read_noise_e", "full_well_e", "quantum_efficiency"];

        private static OpticsSettingsDto ConfiguredOptics() => new(
            FocalLengthMm: 400, ReducerFactor: 1.0,
            SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76, ApertureMm: 80);

        private static OpticsSettingsDto UnsetOptics() => new(
            FocalLengthMm: 0, ReducerFactor: 1.0, SensorWidthPx: 0, SensorHeightPx: 0, PixelSizeUm: 0);

        private static CameraElectronicsDto ConfiguredElectronics() => new(
            SensorName: "IMX571", ReadNoiseE: 3.3, FullWellE: 50_000,
            ElectronsPerAdu: 0.78, Gain: 100, QuantumEfficiencyPeak: 0.85, AutoCaptured: true);

        private static SiteSettingsDto Site(int bortle = 5) => new(
            SiteName: "Test", LatitudeDeg: 45, LongitudeDeg: -122, ElevationM: 0, TimeZone: "UTC",
            UseCustomHorizon: false, DefaultHorizonAltitudeDeg: 20, BortleClass: bortle,
            TypicalSeeingArcsec: 2.5, TwilightDefinition: "astronomical");

        private static FilterSetDto Filters() => new(Filters: [
            new PlanningFilterDto(Name: "Ha 3nm", Kind: FilterKind.Ha, BandwidthNm: 3),
            new PlanningFilterDto(Name: "L", Kind: FilterKind.L),   // bandwidth 0 → kind default
        ]);

        /// <summary>Build with everything defaulting to a fully-configured profile; individual
        /// arguments override the request side. Returns the full §3.1-aware tuple — the
        /// 3-element <see cref="Build"/> wrapper serves the pre-existing tests.</summary>
        private static (OptimalSubInputDto? Input, IReadOnlyList<string> Assumed, string? Error,
                StarFieldContext? StarField, string? StarUnavailableReason) BuildFull(
                OpticsSettingsDto? optics = null, CameraElectronicsDto? electronics = null,
                SiteSettingsDto? site = null, FilterSetDto? filterSet = null,
                string? filter = null, double? bandwidthNm = null,
                double? readNoise = null, double? fullWell = null, double? ePerAdu = null,
                int? gain = null, double? qe = null,
                double? apertureMm = null, double? focalLengthMm = null, double? reducer = null,
                double? pixelUm = null, double? skyMag = null, int? bortle = null,
                double? noiseTolerancePct = null, double? headroom = null,
                double? raDeg = null, double? decDeg = null, double? seeingArcsec = null,
                int? sensorW = null, int? sensorH = null) =>
            OptimalSubOverrides.Build(
                () => optics ?? ConfiguredOptics(),
                () => electronics ?? ConfiguredElectronics(),
                () => site ?? Site(),
                () => filterSet ?? Filters(),
                filter, bandwidthNm, readNoise, fullWell, ePerAdu, gain, qe,
                apertureMm, focalLengthMm, reducer, pixelUm, skyMag, bortle,
                noiseTolerancePct, headroom,
                raDeg, decDeg, seeingArcsec, sensorW, sensorH);

        private static (OptimalSubInputDto? Input, IReadOnlyList<string> Assumed, string? Error) Build(
                OpticsSettingsDto? optics = null, CameraElectronicsDto? electronics = null,
                SiteSettingsDto? site = null, FilterSetDto? filterSet = null,
                string? filter = null, double? bandwidthNm = null,
                double? readNoise = null, double? fullWell = null, double? ePerAdu = null,
                int? gain = null, double? qe = null,
                double? apertureMm = null, double? focalLengthMm = null, double? reducer = null,
                double? pixelUm = null, double? skyMag = null, int? bortle = null,
                double? noiseTolerancePct = null, double? headroom = null) {
            var (input, assumed, error, _, _) = BuildFull(
                optics, electronics, site, filterSet, filter, bandwidthNm,
                readNoise, fullWell, ePerAdu, gain, qe,
                apertureMm, focalLengthMm, reducer, pixelUm, skyMag, bortle,
                noiseTolerancePct, headroom);
            return (input, assumed, error);
        }

        [Test]
        public void A_configured_profile_assembles_with_no_assumed_defaults_except_the_filter() {
            var (input, assumed, error) = Build();
            Assert.That(error, Is.Null);
            Assert.That(input!.ReadNoiseE, Is.EqualTo(3.3));
            Assert.That(input.FullWellE, Is.EqualTo(50_000));
            Assert.That(input.ElectronsPerAdu, Is.EqualTo(0.78));
            Assert.That(input.QuantumEfficiency, Is.EqualTo(0.85));
            Assert.That(input.ApertureMm, Is.EqualTo(80));
            Assert.That(input.FocalLengthMm, Is.EqualTo(400));
            Assert.That(input.PixelSizeUm, Is.EqualTo(3.76));
            Assert.That(input.SkyMagPerArcsec2, Is.EqualTo(20.0), "Bortle 5 site → 20.0 mag/arcsec²");
            Assert.That(input.FilterBandwidthNm, Is.EqualTo(OptimalSubCalculator.DefaultBroadbandBandwidthNm));
            Assert.That(assumed, Is.EqualTo(OnlyBandwidthAssumed),
                "no filter named → only the passband is an assumption");
        }

        [Test]
        public void An_unset_profile_falls_back_to_tier0_defaults_and_names_every_one() {
            var (input, assumed, error) = Build(
                electronics: new CameraElectronicsDto(),
                apertureMm: 80, focalLengthMm: 400, pixelUm: 3.76);   // geometry supplied by request
            Assert.That(error, Is.Null);
            Assert.That(input!.ReadNoiseE, Is.EqualTo(OptimalSubCalculator.DefaultReadNoiseE));
            Assert.That(input.FullWellE, Is.EqualTo(OptimalSubCalculator.DefaultFullWellE));
            Assert.That(input.QuantumEfficiency, Is.EqualTo(OptimalSubCalculator.DefaultQuantumEfficiency));
            Assert.That(assumed, Is.EquivalentTo(AllTier0Assumed),
                "transparency: every Tier-0-defaulted field is named");
        }

        [Test]
        public void Request_overrides_win_over_the_profile() {
            var (input, assumed, error) = Build(
                readNoise: 1.5, fullWell: 100_000, qe: 0.9, bortle: 8, bandwidthNm: 7,
                noiseTolerancePct: 3, headroom: 0.5);
            Assert.That(error, Is.Null);
            Assert.That(input!.ReadNoiseE, Is.EqualTo(1.5));
            Assert.That(input.FullWellE, Is.EqualTo(100_000));
            Assert.That(input.QuantumEfficiency, Is.EqualTo(0.9));
            Assert.That(input.SkyMagPerArcsec2, Is.EqualTo(OptimalSubCalculator.SkyMagFromBortle(8)));
            Assert.That(input.FilterBandwidthNm, Is.EqualTo(7));
            Assert.That(input.NoiseTolerancePct, Is.EqualTo(3));
            Assert.That(input.SaturationHeadroomFraction, Is.EqualTo(0.5));
            Assert.That(assumed, Is.Empty, "nothing was defaulted");
        }

        [Test]
        public void A_named_filter_resolves_case_insensitively_with_its_own_bandwidth() {
            var (input, _, error) = Build(filter: "ha 3NM");
            Assert.That(error, Is.Null);
            Assert.That(input!.FilterBandwidthNm, Is.EqualTo(3), "the entry's explicit bandwidth wins");
        }

        [Test]
        public void A_named_filter_with_zero_bandwidth_uses_its_kinds_default() {
            var (input, _, error) = Build(filter: "L");
            Assert.That(error, Is.Null);
            Assert.That(input!.FilterBandwidthNm,
                Is.EqualTo(OptimalSubCalculator.DefaultBandwidthNm(FilterKind.L)));
        }

        [Test]
        public void An_unknown_filter_is_a_400_listing_the_configured_names() {
            var (input, _, error) = Build(filter: "OIII");
            Assert.That(input, Is.Null);
            Assert.That(error, Does.Contain("Unknown filter 'OIII'").And.Contain("'Ha 3nm'").And.Contain("'L'"));
        }

        [Test]
        public void The_xor_pairs_reject_both_sides_supplied() {
            Assert.That(Build(filter: "L", bandwidthNm: 7).Error, Does.Contain("not both"));
            Assert.That(Build(skyMag: 20, bortle: 5).Error, Does.Contain("not both"));
        }

        [Test]
        public void Missing_geometry_has_no_honest_default_and_is_a_400() {
            var (input, _, error) = Build(optics: UnsetOptics());
            Assert.That(input, Is.Null);
            Assert.That(error, Does.Contain("no honest defaults"));
            // Supplying the geometry in the request rescues it.
            Assert.That(Build(optics: UnsetOptics(), apertureMm: 80, focalLengthMm: 400, pixelUm: 3.76).Error,
                Is.Null);
        }

        [Test]
        public void Supplied_values_are_validated_before_any_merge() {
            Assert.That(Build(readNoise: 0).Error, Does.Contain("readNoise"));
            Assert.That(Build(fullWell: -1).Error, Does.Contain("fullWell"));
            Assert.That(Build(ePerAdu: double.NaN).Error, Does.Contain("ePerAdu"));
            Assert.That(Build(gain: -2).Error, Does.Contain("gain"));
            Assert.That(Build(qe: 1.2).Error, Does.Contain("qe"));
            Assert.That(Build(apertureMm: 0).Error, Does.Contain("apertureMm"));
            Assert.That(Build(focalLengthMm: double.PositiveInfinity).Error, Does.Contain("focalLengthMm"));
            Assert.That(Build(reducer: 11).Error, Does.Contain("reducer"));
            Assert.That(Build(pixelUm: 0).Error, Does.Contain("pixelUm"));
            Assert.That(Build(skyMag: double.NaN).Error, Does.Contain("skyMag"));
            Assert.That(Build(skyMag: 1000).Error, Does.Contain("skyMag"),
                "an absurd sky mag would underflow P to 0 → Infinity seconds on the wire");
            Assert.That(Build(skyMag: 5).Error, Does.Contain("skyMag"));
            Assert.That(Build(bortle: 0).Error, Does.Contain("bortle"));
            Assert.That(Build(bandwidthNm: 0).Error, Does.Contain("bandwidthNm"));
            Assert.That(Build(noiseTolerancePct: 101).Error, Does.Contain("noiseTolerancePct"));
            Assert.That(Build(headroom: 1.5).Error, Does.Contain("headroom"));
        }

        [Test]
        public void An_out_of_range_profile_reducer_is_rejected_when_merged() {
            var badReducer = ConfiguredOptics() with { ReducerFactor = 0 };
            Assert.That(Build(optics: badReducer).Error, Does.Contain("reducer factor"));
        }

        [Test]
        public void The_assembled_input_computes_the_slice1_anchor() {
            // End-to-end sanity: the configured profile above is the calculator test's known rig
            // except QE (0.85 vs 0.8) — the assembled input must produce a Compute() that agrees
            // with a directly-constructed input, proving no field is lost or transposed.
            var (input, _, error) = Build(qe: 0.8, bandwidthNm: 100, readNoise: 3.3);
            Assert.That(error, Is.Null);
            var viaOverrides = OptimalSubCalculator.Compute(input!);
            var direct = OptimalSubCalculator.Compute(new OptimalSubInputDto(
                ReadNoiseE: 3.3, FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
                PixelSizeUm: 3.76, ApertureMm: 80, FocalLengthMm: 400, ReducerFactor: 1.0,
                QuantumEfficiency: 0.8, SkyMagPerArcsec2: 20.0, FilterBandwidthNm: 100));
            Assert.That(viaOverrides.FloorSec, Is.EqualTo(direct.FloorSec).Within(1e-9));
            Assert.That(viaOverrides.CeilingSec, Is.EqualTo(direct.CeilingSec).Within(1e-9));
        }

        // ── §3.1 star-detectability opt-in (raDeg/decDeg + seeing + sensor dims) ────────────

        [Test]
        public void No_target_position_means_no_star_context() {
            var (_, _, error, starField, starUnavailable) = BuildFull();
            Assert.That(error, Is.Null);
            Assert.That(starField, Is.Null, "§3.1 is opt-in — absent target → pure Glover window");
            Assert.That(starUnavailable, Is.Null);
        }

        [Test]
        public void A_target_position_assembles_the_star_context_from_the_profile() {
            var (_, assumed, error, starField, _) = BuildFull(raDeg: 83.822, decDeg: -5.391);
            Assert.That(error, Is.Null);
            Assert.That(starField, Is.Not.Null);
            Assert.That(starField!.RaDeg, Is.EqualTo(83.822));
            Assert.That(starField.DecDeg, Is.EqualTo(-5.391));
            Assert.That(starField.SeeingArcsec, Is.EqualTo(2.5), "profile site seeing");
            // 6248×4176 @ 206.265·3.76/400 ≈ 1.9389″/px → ~3.365° × ~2.249° ≈ 7.57 deg².
            Assert.That(starField.FovDeg2, Is.EqualTo(7.57).Within(0.01));
            Assert.That(assumed, Does.Not.Contain("seeing_arcsec"),
                "the profile carried a real seeing figure — nothing was assumed");
        }

        [Test]
        public void Star_context_overrides_win_and_an_unset_site_seeing_is_tier0_tagged() {
            var (_, assumed, error, starField, _) = BuildFull(
                site: Site() with { TypicalSeeingArcsec = 0 },
                raDeg: 10, decDeg: 20, sensorW: 1000, sensorH: 800);
            Assert.That(error, Is.Null);
            Assert.That(starField!.SeeingArcsec, Is.EqualTo(2.5), "Tier-0 typical seeing");
            Assert.That(assumed, Does.Contain("seeing_arcsec"), "transparency: the default is named");
            // Overridden sensor dims shrink the FOV proportionally: (1000·800)/(6248·4176) of full.
            Assert.That(starField.FovDeg2, Is.EqualTo(7.57 * 1000.0 * 800 / (6248.0 * 4176)).Within(0.01));

            var (_, _, _, seeingOverridden, _) = BuildFull(raDeg: 10, decDeg: 20, seeingArcsec: 4.0);
            Assert.That(seeingOverridden!.SeeingArcsec, Is.EqualTo(4.0), "request seeing wins");
        }

        [Test]
        public void A_target_without_sensor_dimensions_degrades_the_advisory_not_the_window() {
            // The advisory is a rider: clients pass a target whenever the sequence has one, so an
            // unset profile must NOT fail the Glover window — it explains itself via star_reason.
            var noSensor = ConfiguredOptics() with { SensorWidthPx = 0, SensorHeightPx = 0 };
            var (input, _, error, starField, starUnavailable) =
                BuildFull(optics: noSensor, raDeg: 10, decDeg: 20);
            Assert.That(error, Is.Null, "the core window must still answer");
            Assert.That(input, Is.Not.Null);
            Assert.That(starField, Is.Null);
            Assert.That(starUnavailable, Does.Contain("sensor dimensions"),
                "the degrade explains exactly what to configure");

            var (_, _, rescued, rescuedField, rescuedReason) = BuildFull(
                optics: noSensor, raDeg: 10, decDeg: 20, sensorW: 1000, sensorH: 800);
            Assert.That(rescued, Is.Null, "request-side dims rescue an unset profile");
            Assert.That(rescuedField, Is.Not.Null);
            Assert.That(rescuedReason, Is.Null);
        }

        [Test]
        public void Star_parameters_are_validated_loudly() {
            Assert.That(BuildFull(raDeg: 10).Error, Does.Contain("together"),
                "raDeg without decDeg is half a position");
            Assert.That(BuildFull(decDeg: 10).Error, Does.Contain("together"));
            Assert.That(BuildFull(raDeg: 400, decDeg: 10).Error, Does.Contain("DEGREES"),
                "out-of-range RA names the unit trap explicitly");
            Assert.That(BuildFull(raDeg: 10, decDeg: 95).Error, Does.Contain("decDeg"));
            Assert.That(BuildFull(raDeg: 10, decDeg: 20, seeingArcsec: 0).Error,
                Does.Contain("seeingArcsec"));
            Assert.That(BuildFull(raDeg: 10, decDeg: 20, sensorW: 0).Error, Does.Contain("sensorW"));
            Assert.That(BuildFull(raDeg: 10, decDeg: 20, sensorH: -5).Error, Does.Contain("sensorH"));
            Assert.That(BuildFull(seeingArcsec: 2.0).Error, Does.Contain("raDeg"),
                "star-only knobs without a target are meaningless — rejected, not ignored");
            Assert.That(BuildFull(sensorW: 1000).Error, Does.Contain("raDeg"));
        }
    }
}
