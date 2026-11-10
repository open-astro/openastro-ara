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

    /// <summary>#1311 — the polar alignment residual fitted from a guided run's Dec drift.</summary>
    [TestFixture]
    public class PaResidualEstimatorTest {

        private const double RatePxPerSec = 4.0;   // calibration Dec rate
        private const double ScaleArcsec = 2.0;    // guide camera ″/px
        private const double FrameSec = 2.0;

        /// <summary>
        /// A guided Dec axis: the mount drifts the star at <paramref name="driftPxPerSec"/>, the guider
        /// reads the offset with Gaussian centroid noise and corrects 70 % of it the PHD2 way (a positive
        /// offset gets a South pulse, negative duration). Each pulse moves the star before the next frame.
        /// </summary>
        private static void Guide(PaResidualEstimator estimator, double driftPxPerSec, int frames, double noisePx,
                uint seed = 1311, double startSec = 0, double startOffsetPx = 0) {
            var noise = new Noise(seed);
            var star = startOffsetPx;
            for (var k = 0; k < frames; k++) {
                var t = startSec + k * FrameSec;
                var raw = star + noise.Gaussian() * noisePx;
                var durationMs = -0.7 * raw / (RatePxPerSec / 1000.0); // signed: + North, − South
                estimator.Add(t, raw, durationMs);
                star += durationMs * RatePxPerSec / 1000.0 + driftPxPerSec * FrameSec;
            }
        }

        // Deterministic Gaussian centroid noise (Box–Muller over a tiny LCG: System.Random trips CA5394,
        // and this is test-only pseudo-noise), reproducible across runs and platforms.
        private sealed class Noise(uint seed) {
            private uint _state = seed;
            private double Next() {
                _state = (_state * 1664525u) + 1013904223u;
                return (_state >> 8) / (double)(1 << 24);
            }
            public double Gaussian() => Math.Sqrt(-2.0 * Math.Log(1.0 - Next())) * Math.Cos(2.0 * Math.PI * (1.0 - Next()));
        }

        [Test]
        public void The_drift_to_error_factor_is_one_over_the_sidereal_rate() {
            // ε = v / ω: 1″/min of Dec drift is 3.809′ of polar axis error; PHD2's 3.8197 uses a solar day.
            var factor = PaResidualEstimator.ArcminPerArcsecPerMin;
            Assert.That(factor, Is.EqualTo(3.8093).Within(0.0005));
            Assert.That(factor / 3.8197, Is.EqualTo(1).Within(0.003));
        }

        [Test]
        public void A_guided_run_recovers_the_drift_the_corrections_absorbed() {
            var estimator = new PaResidualEstimator();
            const double drift = 0.02; // px/s → 0.02 × 60 × 2 = 2.4″/min → 9.14′
            Guide(estimator, drift, frames: 150, noisePx: 0.3);

            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec);

            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.Value.DriftArcsecPerMin, Is.EqualTo(2.4).Within(0.1));
            Assert.That(fit.Value.PaErrorMinArcmin, Is.EqualTo(2.4 * PaResidualEstimator.ArcminPerArcsecPerMin).Within(0.4));
            Assert.That(fit.Value.UncertaintyArcmin, Is.LessThan(0.5));
            Assert.That(fit.Value.Frames, Is.EqualTo(150));
            Assert.That(fit.Value.SampleSeconds, Is.EqualTo(149 * FrameSec).Within(1e-9));
        }

        [Test]
        public void The_star_offsets_alone_show_almost_none_of_it() {
            // Why the corrections are added back: a well-guided star barely moves, so fitting its
            // offset (corrections left out = zero pulse) would report a near-perfect alignment.
            var guided = new PaResidualEstimator();
            var noise = new Noise(7);
            var star = 0.0;
            for (var k = 0; k < 150; k++) {
                var raw = star + noise.Gaussian() * 0.3;
                var durationMs = -0.7 * raw / (RatePxPerSec / 1000.0);
                guided.Add(k * FrameSec, raw, 0); // pulse withheld from the estimator
                star += durationMs * RatePxPerSec / 1000.0 + 0.02 * FrameSec;
            }

            var fit = guided.Fit(RatePxPerSec, ScaleArcsec);

            Assert.That(fit!.Value.DriftArcsecPerMin, Is.LessThan(0.5), "the offsets hide the 2.4″/min drift");
        }

        [Test]
        public void A_drift_south_reads_as_the_same_error() {
            var estimator = new PaResidualEstimator();
            Guide(estimator, -0.01, frames: 150, noisePx: 0.2);

            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec)!.Value;

            Assert.That(fit.DriftArcsecPerMin, Is.EqualTo(-1.2).Within(0.1));
            Assert.That(fit.PaErrorMinArcmin, Is.EqualTo(1.2 * PaResidualEstimator.ArcminPerArcsecPerMin).Within(0.4));
        }

        [Test]
        public void A_dither_between_segments_does_not_read_as_drift() {
            // The dither moves the lock position 6 px: each segment gets its own intercept.
            var estimator = new PaResidualEstimator();
            Guide(estimator, 0.01, frames: 60, noisePx: 0.2, seed: 1);
            estimator.Break();
            Guide(estimator, 0.01, frames: 60, noisePx: 0.2, seed: 2, startSec: 200, startOffsetPx: 6);

            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec)!.Value;

            Assert.That(fit.DriftArcsecPerMin, Is.EqualTo(1.2).Within(0.15));
            Assert.That(fit.SampleSeconds, Is.EqualTo(2 * 59 * FrameSec).Within(1e-9), "the settle gap is not sampled");
        }

        [Test]
        public void Good_alignment_reads_near_zero_with_an_honest_uncertainty() {
            var estimator = new PaResidualEstimator();
            Guide(estimator, 0, frames: 150, noisePx: 0.4);

            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec)!.Value;

            Assert.That(fit.PaErrorMinArcmin, Is.LessThan(3 * fit.UncertaintyArcmin + 0.05));
            Assert.That(fit.UncertaintyArcmin, Is.GreaterThan(0));
        }

        [Test]
        public void A_lost_star_closes_the_segment_and_short_segments_are_left_out() {
            var estimator = new PaResidualEstimator();
            estimator.Add(0, 0.1, 0);
            estimator.Add(2, 0.2, 0);
            estimator.Add(4, double.NaN, 0); // lost star
            Guide(estimator, 0.01, frames: 30, noisePx: 0.1, startSec: 10);

            Assert.That(estimator.Frames, Is.EqualTo(30), "the 2-frame segment before the loss carries no slope");
        }

        [Test]
        public void No_fit_without_a_rate_a_scale_or_enough_frames() {
            var estimator = new PaResidualEstimator();
            Guide(estimator, 0.01, frames: 4, noisePx: 0.1);
            Assert.That(estimator.Fit(RatePxPerSec, ScaleArcsec), Is.Null, "4 frames leave too few degrees of freedom");

            Guide(estimator, 0.01, frames: 50, noisePx: 0.1, startSec: 100);
            Assert.That(estimator.Fit(0, ScaleArcsec), Is.Null);
            Assert.That(estimator.Fit(RatePxPerSec, 0), Is.Null);
            Assert.That(estimator.Fit(RatePxPerSec, ScaleArcsec), Is.Not.Null);
        }
    }

    /// <summary>#1311 — when a guided run is sampled for the polar alignment residual.</summary>
    [TestFixture]
    public class PaResidualTrackerTest {

        private static PaResidualTracker NewTracker() => new(targetSeconds: 60, minSeconds: 20, progressEverySeconds: 10);

        private static PaResidualAction Frames(PaResidualTracker tracker, double fromSec, double toSec) {
            var last = PaResidualAction.None;
            for (var t = fromSec; t <= toSec; t += 2) {
                var action = tracker.OnStep(t, 0.1, 0);
                if (action != PaResidualAction.None) {
                    last = action;
                }
            }
            return last;
        }

        [Test]
        public void A_run_measures_its_first_target_seconds_then_stops_sampling() {
            var tracker = NewTracker();
            Assert.That(tracker.OnMarker("guiding_started"), Is.EqualTo(PaResidualAction.Started));
            Assert.That(tracker.OnStep(0, 0.1, 0), Is.EqualTo(PaResidualAction.None));
            Assert.That(Frames(tracker, 2, 10), Is.EqualTo(PaResidualAction.Progress));
            Assert.That(Frames(tracker, 12, 60), Is.EqualTo(PaResidualAction.Complete));
            Assert.That(tracker.Measuring, Is.False);
            Assert.That(Frames(tracker, 62, 200), Is.EqualTo(PaResidualAction.None), "one measurement per guided run");

            Assert.That(tracker.OnMarker("guiding_started"), Is.EqualTo(PaResidualAction.Started), "the next run measures again");
        }

        [Test]
        public void Frames_during_a_settle_or_a_pause_are_not_samples() {
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 10);
            tracker.OnMarker("dithered");
            tracker.OnMarker("settling");
            Frames(tracker, 12, 40);
            tracker.OnMarker("settle_done");
            Frames(tracker, 42, 52);
            tracker.OnMarker("paused");
            Frames(tracker, 54, 80);
            tracker.OnMarker("resumed");

            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
            Assert.That(tracker.Measuring, Is.True);
        }

        [Test]
        public void Guiding_stopped_early_reports_what_it_has_only_past_the_minimum() {
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 10);
            Assert.That(tracker.OnMarker("guiding_stopped"), Is.EqualTo(PaResidualAction.Cancelled));
            Assert.That(tracker.OnStep(12, 0.1, 0), Is.EqualTo(PaResidualAction.None), "nothing until guiding starts again");

            tracker.OnMarker("guiding_started");
            Frames(tracker, 100, 130);
            Assert.That(tracker.OnMarker("guiding_stopped"), Is.EqualTo(PaResidualAction.Complete));
        }

        [Test]
        public void Recalibrating_drops_the_measurement() {
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 10);
            Assert.That(tracker.OnMarker("calibration_started"), Is.EqualTo(PaResidualAction.Cancelled));
            Assert.That(tracker.Measuring, Is.False);
        }

        [Test]
        public void A_guider_already_guiding_at_connect_starts_on_its_first_frame() {
            var tracker = NewTracker();
            Assert.That(tracker.OnStep(0, 0.1, 0), Is.EqualTo(PaResidualAction.Started));
            Assert.That(tracker.Measuring, Is.True);
        }

        [Test]
        public void Reset_drops_a_measurement_in_flight() {
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 10);
            Assert.That(tracker.Reset(), Is.EqualTo(PaResidualAction.Cancelled));
            Assert.That(tracker.Reset(), Is.EqualTo(PaResidualAction.None));
        }
    }
}
