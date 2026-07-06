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
    /// NEXTGEN §3.1 slice 3 — the star-detectability floor composer: predicted stars per sub
    /// (m_lim × the count model × FOV), the t_stars bisection, and the Augment fold into the
    /// sub-exposure window (floor semantics, bound reporting, reason strings).
    /// </summary>
    [TestFixture]
    public class StarDetectabilityFloorTest {

        private const double Seeing = 2.5;

        // The known rig from the slice-1 tests: 80 mm f/5, IMX571-class electronics, Bortle 5 sky.
        private static OptimalSubInputDto Rig(double bandwidthNm = 100, double skyMag = 20.0) => new(
            ReadNoiseE: 3.3, FullWellE: 50_000, ElectronsPerAdu: 0.78, Gain: 100,
            PixelSizeUm: 3.76, ApertureMm: 80, FocalLengthMm: 400, ReducerFactor: 1.0,
            QuantumEfficiency: 0.8, SkyMagPerArcsec2: skyMag, FilterBandwidthNm: bandwidthNm);

        // The full-sensor FOV of that rig (6248×4176 @ 1.939″/px) ≈ 7.57 deg².
        private const double FullSensorFovDeg2 = 7.57;

        [Test]
        public void Predicted_stars_grow_monotonically_with_exposure() {
            var input = Rig();
            var t30 = StarDetectabilityFloor.PredictedStars(input, 30, Seeing, 30, FullSensorFovDeg2,
                StarDetectability.RegistrationSnr);
            var t60 = StarDetectabilityFloor.PredictedStars(input, 60, Seeing, 30, FullSensorFovDeg2,
                StarDetectability.RegistrationSnr);
            var t120 = StarDetectabilityFloor.PredictedStars(input, 120, Seeing, 30, FullSensorFovDeg2,
                StarDetectability.RegistrationSnr);
            Assert.That(t60, Is.GreaterThan(t30), "longer subs reach fainter stars → strictly more of them");
            Assert.That(t120, Is.GreaterThan(t60));
        }

        [Test]
        public void The_galactic_plane_offers_more_stars_than_the_pole() {
            var input = Rig();
            var plane = StarDetectabilityFloor.PredictedStars(input, 30, Seeing, 0, FullSensorFovDeg2,
                StarDetectability.RegistrationSnr);
            var pole = StarDetectabilityFloor.PredictedStars(input, 30, Seeing, 90, FullSensorFovDeg2,
                StarDetectability.RegistrationSnr);
            Assert.That(plane, Is.GreaterThan(pole));
        }

        [Test]
        public void The_floor_delivers_the_requested_star_budget() {
            var input = Rig(bandwidthNm: 7);   // narrowband — the case the feature exists for
            var floor = StarDetectabilityFloor.FloorSec(input, Seeing, 90, 0.5);
            Assert.That(floor, Is.Not.Null);
            var starsAtFloor = StarDetectabilityFloor.PredictedStars(
                input, floor!.Value, Seeing, 90, 0.5, StarDetectability.RegistrationSnr);
            Assert.That(starsAtFloor, Is.EqualTo(StarDetectabilityFloor.MinRegistrationStars).Within(0.01),
                "the bisection converges onto the budget from above");
            // And just below the floor the budget is NOT met — it really is the minimum.
            var justUnder = StarDetectabilityFloor.PredictedStars(
                input, floor.Value * 0.99, Seeing, 90, 0.5, StarDetectability.RegistrationSnr);
            Assert.That(justUnder, Is.LessThan(StarDetectabilityFloor.MinRegistrationStars));
        }

        [Test]
        public void A_narrowband_floor_sits_far_above_the_broadband_floor() {
            var broadband = StarDetectabilityFloor.FloorSec(Rig(bandwidthNm: 100), Seeing, 30, 1.0);
            var narrowband = StarDetectabilityFloor.FloorSec(Rig(bandwidthNm: 7), Seeing, 30, 1.0);
            Assert.That(broadband, Is.Not.Null);
            Assert.That(narrowband, Is.Not.Null);
            Assert.That(narrowband, Is.GreaterThan(broadband),
                "a 7 nm filter throws away most starlight — the same star budget needs a longer sub");
        }

        [Test]
        public void A_starved_field_reports_null_instead_of_an_absurd_floor() {
            // A few-arcmin field at the galactic pole can't reach 20 registration stars within
            // the 1 h search bound — the caller words that honestly instead of advising 3 h subs.
            var floor = StarDetectabilityFloor.FloorSec(Rig(bandwidthNm: 3), Seeing, 90, 0.0005);
            Assert.That(floor, Is.Null);
        }

        [Test]
        public void Predicted_stars_reject_a_non_positive_fov() {
            Assert.Throws<ArgumentOutOfRangeException>(() => StarDetectabilityFloor.PredictedStars(
                Rig(), 30, Seeing, 30, 0, StarDetectability.RegistrationSnr));
            Assert.Throws<ArgumentOutOfRangeException>(() => StarDetectabilityFloor.PredictedStars(
                Rig(), 30, Seeing, 30, double.NaN, StarDetectability.RegistrationSnr));
        }

        // ── Augment: folding the star floor into the window ──────────────────────────────────

        [Test]
        public void When_stars_bind_the_recommendation_moves_up_and_the_bound_says_so() {
            // Bright sky + broadband → a sub-second Glover floor; a small high-latitude field
            // needs far longer than that to collect 20 registration-quality stars.
            var input = Rig(bandwidthNm: 100, skyMag: 18.0);
            var window = OptimalSubCalculator.Compute(input);
            var augmented = StarDetectabilityFloor.Augment(window, input, Seeing,
                raDeg: 192.85948, decDeg: 27.12825, fovDeg2: 0.005);   // the NGP itself (b = +90°), a ~5′ field

            Assert.That(augmented.StarFloorSec, Is.Not.Null);
            Assert.That(augmented.StarFloorSec, Is.GreaterThan(window.FloorSec),
                "precondition — this scenario exists to exercise the binding case");
            Assert.That(augmented.LimitingBound, Is.EqualTo(OptimalSubBound.StarFloor));
            Assert.That(augmented.RecommendedSec, Is.EqualTo(
                Math.Min(augmented.StarFloorSec!.Value, window.CeilingSec)).Within(1e-9));
            Assert.That(augmented.FloorSec, Is.EqualTo(window.FloorSec),
                "the Glover floor stays reported as-is — the star floor is a separate figure");
            Assert.That(augmented.StarReason, Does.Contain("stars, not read noise"));
        }

        [Test]
        public void When_read_noise_binds_the_window_is_unchanged_and_the_counts_still_flow() {
            // Narrowband on a dark sky → an enormous Glover floor; a wide field on the galactic
            // plane reaches its star budget long before that.
            var input = Rig(bandwidthNm: 7);
            var window = OptimalSubCalculator.Compute(input);
            var augmented = StarDetectabilityFloor.Augment(window, input, Seeing,
                raDeg: 83.822, decDeg: -5.391, fovDeg2: FullSensorFovDeg2);   // M42, near the plane

            Assert.That(augmented.StarFloorSec, Is.Not.Null);
            Assert.That(augmented.StarFloorSec, Is.LessThan(window.FloorSec),
                "precondition — this scenario exercises the non-binding case");
            Assert.That(augmented.LimitingBound, Is.EqualTo(window.LimitingBound));
            Assert.That(augmented.RecommendedSec, Is.EqualTo(window.RecommendedSec).Within(1e-9));
            Assert.That(augmented.Viable, Is.EqualTo(window.Viable));
            Assert.That(augmented.StarsRegistrationPerSub,
                Is.GreaterThan(StarDetectabilityFloor.MinRegistrationStars));
            Assert.That(augmented.StarsDetectedPerSub, Is.GreaterThan(augmented.StarsRegistrationPerSub),
                "SNR-5 detection always sees more stars than SNR-10 registration");
            Assert.That(augmented.StarReason, Does.Contain("read noise remains the binding floor"));
        }

        [Test]
        public void A_ceiling_capped_starved_window_reports_thin_honestly() {
            // Squash the ceiling below the star floor: the recommendation is the ceiling, the
            // window is not viable, and the reason owns up to the thin star field.
            var input = Rig(bandwidthNm: 100, skyMag: 18.0) with { SaturationHeadroomFraction = 0.001 };
            var window = OptimalSubCalculator.Compute(input);
            var augmented = StarDetectabilityFloor.Augment(window, input, Seeing,
                raDeg: 192.85948, decDeg: 27.12825, fovDeg2: 0.005);

            Assert.That(augmented.StarFloorSec, Is.Not.Null.And.GreaterThan(window.CeilingSec),
                "precondition — the star floor must exceed the squashed ceiling");
            Assert.That(augmented.Viable, Is.False);
            Assert.That(augmented.LimitingBound, Is.EqualTo(OptimalSubBound.SaturationCeiling));
            Assert.That(augmented.RecommendedSec, Is.EqualTo(window.CeilingSec).Within(1e-9));
            Assert.That(augmented.StarReason, Does.Contain("thin for registration"));
        }

        [Test]
        public void Extrapolated_counts_carry_the_mag9_label() {
            // Any real rig's registration m_lim at its recommendation is far beyond mag 9 — the
            // §3.1 honesty label must be present.
            var input = Rig(bandwidthNm: 7);
            var window = OptimalSubCalculator.Compute(input);
            var augmented = StarDetectabilityFloor.Augment(window, input, Seeing,
                raDeg: 83.822, decDeg: -5.391, fovDeg2: FullSensorFovDeg2);
            Assert.That(augmented.StarReason, Does.Contain("mag-9"));
        }

        [Test]
        public void Augment_rejects_an_out_of_range_declination() {
            var input = Rig();
            var window = OptimalSubCalculator.Compute(input);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                StarDetectabilityFloor.Augment(window, input, Seeing, 10, 95, 1.0));
        }
    }
}
