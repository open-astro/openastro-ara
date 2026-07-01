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

namespace OpenAstroAra.Test {

    /// <summary>
    /// NEXTGEN §2/§3 Optimal-Sub calculator — the Glover read-noise floor (t = C(ε)·R²/P, the
    /// criterion popularised by Dr. Robin Glover) + the sky-background saturation ceiling. All
    /// anchors are hand-computable from the stated model: the exact C(ε) = 1/((1+ε)²−1) floor,
    /// its "10·R²/P" folk rounding, the V zero-point sky-flux model against a SharpCap-style
    /// rig, and the collapsed-window criterion (P-independent by construction).
    /// </summary>
    [TestFixture]
    public class OptimalSubCalculatorTest {

        /// <summary>A known rig: 80 mm f/5 (400 mm), no reducer, 3.76 µm pixels, QE 0.8,
        /// Bortle 5 sky (20.0 mag/arcsec²), 100 nm effective broadband. P ≈ 1.51 e⁻/s/px.</summary>
        private static OptimalSubInputDto Rig(
            double readNoise = 3.3,
            double fullWell = 50_000,
            double ePerAdu = 0,
            double pixelUm = 3.76,
            double apertureMm = 80,
            double focalMm = 400,
            double reducer = 1.0,
            double qe = 0.8,
            double skyMag = 20.0,
            double bandwidthNm = 100,
            double tolerancePct = 5.0,
            double headroom = 0.8) =>
            new(ReadNoiseE: readNoise, FullWellE: fullWell, ElectronsPerAdu: ePerAdu, Gain: -1,
                PixelSizeUm: pixelUm, ApertureMm: apertureMm, FocalLengthMm: focalMm,
                ReducerFactor: reducer, QuantumEfficiency: qe, SkyMagPerArcsec2: skyMag,
                FilterBandwidthNm: bandwidthNm, NoiseTolerancePct: tolerancePct,
                SaturationHeadroomFraction: headroom);

        /// <summary>An input where P is trivially known: the floor formula can then be checked
        /// in isolation. This helper pins P by construction rather than hand-tuning: compute P
        /// once, then scale expectations off it.</summary>
        private static double P(OptimalSubInputDto input) =>
            OptimalSubCalculator.SkyFluxEPerSecPerPx(input);

        [Test]
        public void The_exact_glover_floor_matches_the_popularised_ten_r_squared_over_p() {
            // R = 3.3 e⁻, ε = 5% → C = 1/((1.05)²−1) = 9.7561; with P normalised out,
            // floor·P = C·R² = 106.24 e⁻ — within 3% of the folk "10·R²" = 108.9.
            var input = Rig();
            var p = P(input);
            var result = OptimalSubCalculator.Compute(input);

            Assert.That(result.FloorSec * p, Is.EqualTo(106.24).Within(0.5), "exact C(5%)·R²");
            Assert.That(result.FloorSec * p, Is.EqualTo(10.0 * 3.3 * 3.3).Within(3).Percent,
                "the popularised 10·R²/P rounding stays within 3%");
        }

        [Test]
        public void The_noise_tolerance_knob_stretches_the_floor() {
            // ε = 3% → C = 1/((1.03)²−1) = 16.420; C(3%)/C(5%) = 1.6831.
            var at5 = OptimalSubCalculator.Compute(Rig(tolerancePct: 5.0));
            var at3 = OptimalSubCalculator.Compute(Rig(tolerancePct: 3.0));

            Assert.That(at3.FloorSec * P(Rig()), Is.EqualTo(16.420 * 3.3 * 3.3).Within(0.5));
            Assert.That(at3.FloorSec / at5.FloorSec, Is.EqualTo(1.6831).Within(1e-3));
        }

        [Test]
        public void Sky_flux_for_a_sharpcap_style_rig_lands_where_the_model_says() {
            // 80 mm f/5, 3.76 µm, QE 0.8, Bortle 5, 100 nm:
            // P = 1e4 · 10⁻⁸ · 100 · π·(80/20)² · (206.265·3.76/400)² · 0.8 = 1.5117 e⁻/s/px.
            var p = P(Rig());
            Assert.That(p, Is.EqualTo(1.5117).Within(1).Percent, "regression anchor");
            Assert.That(p, Is.InRange(0.5, 5.0),
                "plausibility: a moderate suburban broadband rig sees a few e⁻/s/px");
        }

        [Test]
        public void Narrowband_scales_sky_flux_down_and_the_floor_up_by_the_bandwidth_ratio() {
            // 100 nm → 7 nm: P ÷ (100/7), floor × (100/7) — exactly (the model is linear in bandwidth).
            var broadband = OptimalSubCalculator.Compute(Rig(bandwidthNm: 100));
            var narrowband = OptimalSubCalculator.Compute(Rig(bandwidthNm: 7));

            Assert.That(narrowband.SkyFluxEPerSecPerPx,
                Is.EqualTo(broadband.SkyFluxEPerSecPerPx * 7.0 / 100.0).Within(1e-9));
            Assert.That(narrowband.FloorSec,
                Is.EqualTo(broadband.FloorSec * 100.0 / 7.0).Within(1e-6));
        }

        [Test]
        public void Bortle_darkens_the_sky_and_lengthens_the_floor() {
            Assert.That(OptimalSubCalculator.SkyMagFromBortle(1), Is.EqualTo(22.0));
            Assert.That(OptimalSubCalculator.SkyMagFromBortle(5), Is.EqualTo(20.0));
            Assert.That(OptimalSubCalculator.SkyMagFromBortle(9), Is.EqualTo(18.0));
            // Out-of-range classes clamp to the canonical 1–9 scale.
            Assert.That(OptimalSubCalculator.SkyMagFromBortle(0), Is.EqualTo(22.0));
            Assert.That(OptimalSubCalculator.SkyMagFromBortle(10), Is.EqualTo(18.0));

            // Each Bortle class is 0.5 mag → the floor stretches by 10^(0.4·0.5) ≈ 1.585/class.
            var bortle5 = OptimalSubCalculator.Compute(Rig(skyMag: OptimalSubCalculator.SkyMagFromBortle(5)));
            var bortle4 = OptimalSubCalculator.Compute(Rig(skyMag: OptimalSubCalculator.SkyMagFromBortle(4)));
            Assert.That(bortle4.FloorSec / bortle5.FloorSec, Is.EqualTo(Math.Pow(10, 0.2)).Within(1e-6));
        }

        [Test]
        public void A_wide_window_recommends_the_floor_and_names_it_as_the_bound() {
            // Force P = its computed value; FW 50 ke⁻ at 0.8 headroom → ceiling = 40 000/P,
            // orders of magnitude above the ~106 e⁻ floor budget → wide window.
            var result = OptimalSubCalculator.Compute(Rig());
            var p = P(Rig());

            Assert.That(result.CeilingSec, Is.EqualTo(0.8 * 50_000 / p).Within(1e-6));
            Assert.That(result.Viable, Is.True);
            Assert.That(result.LimitingBound, Is.EqualTo(OptimalSubBound.ReadNoiseFloor));
            Assert.That(result.RecommendedSec, Is.EqualTo(result.FloorSec).Within(1e-9),
                "past the floor buys no further read-noise gain — the floor IS the recommendation");
            Assert.That(result.FloorSec, Is.LessThan(result.CeilingSec));
        }

        [Test]
        public void A_collapsed_window_flags_the_saturation_ceiling_independent_of_sky_flux() {
            // headroom·FW < C(ε)·R² (0.8·100 = 80 e⁻ < 106.24 e⁻) collapses the window for ANY
            // sky — the criterion is P-independent by construction in the v1 background model.
            foreach (var skyMag in new[] { 22.0, 18.0 }) {
                var result = OptimalSubCalculator.Compute(Rig(fullWell: 100, skyMag: skyMag));
                Assert.That(result.Viable, Is.False, $"skyMag {skyMag}");
                Assert.That(result.LimitingBound, Is.EqualTo(OptimalSubBound.SaturationCeiling));
                Assert.That(result.RecommendedSec, Is.EqualTo(result.CeilingSec).Within(1e-9),
                    "when saturation wins, the ceiling is the best available sub");
            }
        }

        [Test]
        public void A_coarse_adc_clips_the_effective_well() {
            // e⁻/ADU 0.25 × 65 535 = 16 383.75 e⁻ < the 50 ke⁻ well → the ADC is the real ceiling.
            var clipped = OptimalSubCalculator.Compute(Rig(ePerAdu: 0.25));
            var unclipped = OptimalSubCalculator.Compute(Rig(ePerAdu: 0));
            var p = P(Rig());

            Assert.That(clipped.CeilingSec, Is.EqualTo(0.8 * 0.25 * 65_535 / p).Within(1e-6));
            Assert.That(clipped.CeilingSec, Is.LessThan(unclipped.CeilingSec));

            // A fine ADC (2 e⁻/ADU → 131 ke⁻ range) leaves the physical well in charge.
            var fine = OptimalSubCalculator.Compute(Rig(ePerAdu: 2.0));
            Assert.That(fine.CeilingSec, Is.EqualTo(unclipped.CeilingSec).Within(1e-9));
        }

        [Test]
        public void Non_physical_inputs_throw() {
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(readNoise: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(fullWell: -1)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(pixelUm: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(apertureMm: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(focalMm: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(reducer: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(qe: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(qe: 1.2)),
                "QE > 1 is non-physical and would silently inflate the sky-flux estimate");
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(bandwidthNm: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(tolerancePct: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(tolerancePct: 101)),
                "tolerating more added noise than the shot noise itself is meaningless");
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(headroom: 0)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(headroom: 1.1)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(ePerAdu: -0.1)));
            Assert.Throws<ArgumentException>(() => OptimalSubCalculator.Compute(Rig(skyMag: double.NaN)));
        }
    }
}
