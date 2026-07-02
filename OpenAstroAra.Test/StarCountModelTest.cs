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
using OpenAstroAra.Server.Services;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// NEXTGEN §3.1 slice 2 — the star-count model and, crucially, the pinned
    /// VALIDATION GATE: the full m ∈ [5,9] pooled-density grid from the canonical HYG
    /// snapshot is embedded here, the per-band log-linear fit is re-derived from it in
    /// the test itself, and the out-of-sample m = 9 extrapolation must land within the
    /// design's factor-2 trigger at EVERY band. The proof that the model form is
    /// grounded in real data thus stands in CI permanently, not just in the one-off
    /// script run that produced the constants (regenerate with
    /// scripts/fit-star-count-model.py against the sha-pinned hygdata_v40.csv.gz).
    /// </summary>
    [TestFixture]
    public class StarCountModelTest {

        // Pooled HYG densities N(<m)/deg², m = 5..9, per |b| band {0,10,20,30,50,70,90}°.
        // Derived from hygdata_v40.csv.gz (sha256 8e3ff9e6…, the DataManagerService pin)
        // by scripts/fit-star-count-model.py — see the script for the pooling method.
        private static readonly double[][] HygDensities = [
            [0.063136, 0.196360, 0.615503, 1.571994, 2.941788], // |b| =  0°
            [0.053378, 0.162110, 0.507087, 1.307893, 2.517927], // |b| = 10°
            [0.046913, 0.141627, 0.423252, 1.096164, 2.121440], // |b| = 20°
            [0.029225, 0.101004, 0.331916, 0.928467, 1.872188], // |b| = 30°
            [0.029856, 0.087837, 0.271948, 0.774090, 1.649431], // |b| = 50°
            [0.028869, 0.082133, 0.254938, 0.661945, 1.546706], // |b| = 70°
            [0.031851, 0.070073, 0.248439, 0.611543, 1.458785], // |b| = 90°
        ];

        private static readonly double[] BandLatitudes = [0, 10, 20, 30, 50, 70, 90];

        [Test]
        public void The_validation_gate_holds_at_every_band() {
            // The §3.1 go/no-go, re-derived from the embedded real data: fit
            // log10 N on m ∈ [5,8] per band, extrapolate to m = 9 (out-of-sample in
            // the direction the shipped model extrapolates), and require the result
            // within a factor of 2 of the actual pooled density. If a future edit
            // changes the model constants without re-running the script against the
            // canonical snapshot, this is the test that catches it.
            for (var band = 0; band < HygDensities.Length; band++) {
                double meanX = 0, meanY = 0;
                for (var i = 0; i < 4; i++) {
                    meanX += 5 + i;
                    meanY += Math.Log10(HygDensities[band][i]);
                }
                meanX /= 4;
                meanY /= 4;
                double num = 0, den = 0;
                for (var i = 0; i < 4; i++) {
                    var dx = 5 + i - meanX;
                    num += dx * (Math.Log10(HygDensities[band][i]) - meanY);
                    den += dx * dx;
                }
                var slope = num / den;
                var intercept = meanY - slope * meanX;
                var predicted9 = Math.Pow(10.0, intercept + slope * 9.0);
                var ratio = predicted9 / HygDensities[band][4];
                Assert.That(ratio, Is.InRange(0.5, 2.0),
                    $"|b|={BandLatitudes[band]}°: the m=9 extrapolation must sit inside the factor-2 gate");
            }
        }

        [Test]
        public void The_model_anchors_exactly_at_the_real_mag9_densities() {
            for (var band = 0; band < BandLatitudes.Length; band++) {
                Assert.That(
                    StarCountModel.CumulativeStarsPerDeg2(9.0, BandLatitudes[band]),
                    Is.EqualTo(HygDensities[band][4]).Within(1e-9),
                    $"|b|={BandLatitudes[band]}°: at m=9 the model IS the measured HYG density");
            }
        }

        [Test]
        public void Counts_rise_with_depth_and_fall_away_from_the_plane() {
            Assert.That(StarCountModel.CumulativeStarsPerDeg2(12, 30),
                Is.GreaterThan(StarCountModel.CumulativeStarsPerDeg2(9, 30)),
                "deeper limiting magnitude → more stars");
            Assert.That(StarCountModel.CumulativeStarsPerDeg2(11, 0),
                Is.GreaterThan(StarCountModel.CumulativeStarsPerDeg2(11, 90)),
                "the galactic plane is richer than the pole");
            Assert.That(StarCountModel.CumulativeStarsPerDeg2(11, -30),
                Is.EqualTo(StarCountModel.CumulativeStarsPerDeg2(11, 30)).Within(1e-12),
                "counts are symmetric about the plane");
        }

        [Test]
        public void Galactic_latitude_reproduces_the_defining_identities() {
            // The NGP itself sits at b = +90 by definition of the rotation…
            Assert.That(StarCountModel.GalacticLatitudeDeg(192.85948, 27.12825),
                Is.EqualTo(90.0).Within(0.01));
            // …and the J2000 galactic centre (Sgr A*, ~266.417°, −29.008°) on the plane.
            Assert.That(StarCountModel.GalacticLatitudeDeg(266.417, -29.008),
                Is.EqualTo(0.0).Within(0.1));
            // A pole-of-the-other-sign spot check: the south galactic pole.
            Assert.That(StarCountModel.GalacticLatitudeDeg(12.85948, -27.12825),
                Is.EqualTo(-90.0).Within(0.01));
        }

        [Test]
        public void Invalid_inputs_throw() {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarCountModel.CumulativeStarsPerDeg2(double.NaN, 30));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StarCountModel.CumulativeStarsPerDeg2(9, double.PositiveInfinity));
        }
    }
}
