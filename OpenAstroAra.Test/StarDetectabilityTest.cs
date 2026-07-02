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
    /// NEXTGEN §3.1 slice 1 — the m_lim(t) solver, checked against exact physical
    /// invariants rather than magic expected values: the returned magnitude plugs back
    /// into the first-principles SNR formula at exactly the threshold (non-circular —
    /// it re-derives SNR, not the quadratic), and the two asymptotic regimes deepen at
    /// their textbook rates (read-noise-limited: exactly 2.5 mag per decade of exposure;
    /// sky-limited: → 1.25 mag per decade).
    /// </summary>
    [TestFixture]
    public class StarDetectabilityTest {

        // A representative rig: 100 mm f/5, 3.76 µm pixels, QE 0.8, broadband 100 nm.
        private static OptimalSubInputDto Input(double skyMag = 20.5, double readNoise = 3.3) =>
            new(ReadNoiseE: readNoise, FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
                PixelSizeUm: 3.76, ApertureMm: 100, FocalLengthMm: 500, ReducerFactor: 1.0,
                QuantumEfficiency: 0.8, SkyMagPerArcsec2: skyMag, FilterBandwidthNm: 100);

        private const double Seeing = 2.5; // arcsec FWHM

        [Test]
        public void The_limiting_magnitude_star_sits_exactly_at_the_snr_threshold() {
            // Non-circular self-consistency: recompute SNR from first principles at m_lim
            // and it must equal the threshold to numerical precision.
            var input = Input();
            const double t = 60;
            var mLim = StarDetectability.LimitingMagnitude(input, t, Seeing);

            var apertureAreaCm2 = Math.PI * Math.Pow(input.ApertureMm / 20.0, 2);
            var signal = OptimalSubCalculator.PhotonFluxMag0PerCm2PerNm
                * Math.Pow(10.0, -0.4 * mLim)
                * input.FilterBandwidthNm * apertureAreaCm2 * input.QuantumEfficiency * t;
            var noiseVar = StarDetectability.SeeingDiscPixels(input, Seeing)
                * (OptimalSubCalculator.SkyFluxEPerSecPerPx(input) * t
                   + input.ReadNoiseE * input.ReadNoiseE);
            var snr = signal / Math.Sqrt(signal + noiseVar);

            Assert.That(snr, Is.EqualTo(StarDetectability.DefaultDetectionSnr).Within(1e-9));
        }

        [Test]
        public void Longer_exposures_reach_fainter_stars() {
            var input = Input();
            var m30 = StarDetectability.LimitingMagnitude(input, 30, Seeing);
            var m300 = StarDetectability.LimitingMagnitude(input, 300, Seeing);
            Assert.That(m300, Is.GreaterThan(m30), "fainter = numerically larger magnitude");
        }

        [Test]
        public void Read_noise_limited_regime_deepens_exactly_2p5_mag_per_decade() {
            // A hypothetically black sky (mag 40 → P ≈ 0): the noise term is the constant
            // n_pix·R², so the minimum SIGNAL is exposure-independent and the minimum FLUX
            // scales as 1/t — exactly 2.5 mag per decade. This is exact, not asymptotic.
            var input = Input(skyMag: 40, readNoise: 10);
            var m1 = StarDetectability.LimitingMagnitude(input, 10, Seeing);
            var m2 = StarDetectability.LimitingMagnitude(input, 100, Seeing);
            Assert.That(m2 - m1, Is.EqualTo(2.5).Within(1e-6));
        }

        [Test]
        public void Sky_limited_regime_approaches_1p25_mag_per_decade() {
            // Bright sky, long subs: N ∝ t dominates, S* → k·√N ∝ √t, so the flux floor
            // scales t^(−1/2) → 1.25 mag per decade (asymptotically).
            var input = Input(skyMag: 18, readNoise: 1);
            var m1 = StarDetectability.LimitingMagnitude(input, 1_000, Seeing);
            var m2 = StarDetectability.LimitingMagnitude(input, 10_000, Seeing);
            Assert.That(m2 - m1, Is.EqualTo(1.25).Within(0.02));
        }

        [Test]
        public void A_darker_sky_reaches_fainter_stars() {
            var bortle8 = StarDetectability.LimitingMagnitude(Input(skyMag: 18.5), 60, Seeing);
            var bortle2 = StarDetectability.LimitingMagnitude(Input(skyMag: 21.5), 60, Seeing);
            Assert.That(bortle2, Is.GreaterThan(bortle8));
        }

        [Test]
        public void Registration_quality_centroids_need_brighter_stars_than_bare_detection() {
            var input = Input();
            var detect = StarDetectability.LimitingMagnitude(
                input, 60, Seeing, StarDetectability.DefaultDetectionSnr);
            var register = StarDetectability.LimitingMagnitude(
                input, 60, Seeing, StarDetectability.RegistrationSnr);
            Assert.That(register, Is.LessThan(detect),
                "SNR-10 centroids come from brighter (numerically smaller mag) stars");
        }

        [Test]
        public void An_undersampled_rig_floors_the_seeing_disc_at_one_pixel() {
            // 3.76 µm on 100 mm fl → 7.76″/px: a 2.5″ seeing disc fits inside one pixel.
            var wideField = Input() with { FocalLengthMm = 100 };
            Assert.That(StarDetectability.SeeingDiscPixels(wideField, Seeing), Is.EqualTo(1.0));
            // The representative rig (1.55″/px) resolves the disc across multiple pixels.
            Assert.That(StarDetectability.SeeingDiscPixels(Input(), Seeing), Is.GreaterThan(1.0));
        }

        [Test]
        public void Invalid_inputs_throw() {
            var input = Input();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(input, 0, Seeing));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(input, 60, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(input, 60, Seeing, snrThreshold: 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(input, double.NaN, Seeing));
            // r1/r3: garbage read noise is rejected, only exactly 0 means "unset → default".
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(
                    Input(readNoise: double.PositiveInfinity), 60, Seeing));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.LimitingMagnitude(Input(readNoise: -3.3), 60, Seeing));
            Assert.DoesNotThrow(
                () => StarDetectability.LimitingMagnitude(Input(readNoise: 0), 60, Seeing),
                "exactly 0 is the DTO's unset value and takes the Tier-0 default");
            // r3: a zeroed focal length must surface as bad input from the standalone
            // helper, not silently collapse to the one-pixel floor via an Infinity scale.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarDetectability.SeeingDiscPixels(
                    Input() with { FocalLengthMm = 0 }, Seeing));
        }
    }
}
