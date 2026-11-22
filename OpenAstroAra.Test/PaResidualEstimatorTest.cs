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
        public void The_fit_reports_the_Dec_pulses_it_added_back() {
            // #2072 — for the log: how much of the figure is rebuilt from pulses (net North-positive, and in all).
            var estimator = new PaResidualEstimator();
            double[] pulsesMs = [100, -40, 60, -20, 0, 30, -10, 50];
            for (var k = 0; k < pulsesMs.Length; k++) {
                estimator.Add(k * FrameSec, 0.1 * k, pulsesMs[k]);
            }

            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec)!.Value;

            Assert.That(fit.DecPulseNetMs, Is.EqualTo(170).Within(1e-9));
            Assert.That(fit.DecPulseTotalMs, Is.EqualTo(310).Within(1e-9));
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
        public void Dec_pulses_the_star_does_not_answer_are_flagged_unanswered() {
            // #2072 — South pulses every frame, and the star keeps creeping North of the lock position: backlash.
            var estimator = new PaResidualEstimator();
            for (var k = 0; k < 40; k++) {
                estimator.Add(k * FrameSec, 0.05 + 0.008 * k, -150);
            }
            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec);
            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.Value.DecPulsesUnanswered, Is.True);
        }

        [Test]
        public void A_steady_offset_under_one_sided_pulses_is_real_drift_and_still_measures() {
            // #2072 — a proportional guider holding a real drift sends one-sided pulses and keeps the star a steady
            // offset (drift per frame ÷ aggressiveness) on the far side: that is a measurement, not backlash.
            var estimator = new PaResidualEstimator();
            Guide(estimator, driftPxPerSec: 0.05, frames: 60, noisePx: 0.05);
            var fit = estimator.Fit(RatePxPerSec, ScaleArcsec);
            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.Value.DecPulsesUnanswered, Is.False);
            Assert.That(fit.Value.DriftArcsecPerMin, Is.EqualTo(0.05 * 60 * ScaleArcsec).Within(0.5));
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

        // #2072 — the start-guiding settle: the star pulled onto the lock position, every Dec pulse South.
        private static void Recovery(PaResidualTracker tracker, double fromSec, double toSec) {
            for (var t = fromSec; t <= toSec; t += 2) {
                tracker.OnStep(t, 4.0, -2000);
            }
        }

        [Test]
        public void A_start_settle_recovery_is_left_out_and_only_the_frames_after_settle_done_are_sampled() {
            var tracker = NewTracker();
            Assert.That(tracker.OnMarker("guiding_started"), Is.EqualTo(PaResidualAction.Started));
            tracker.OnMarker("settle_begin");
            Recovery(tracker, 0, 20);
            tracker.OnMarker("settle_done");
            Frames(tracker, 22, 42);

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11), "the clean frames only");
            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
            Assert.That(tracker.Measuring, Is.True);
        }

        [Test]
        public void A_settle_reported_before_the_guiding_start_is_not_lost() {
            var tracker = NewTracker();
            tracker.OnMarker("settling");
            Assert.That(tracker.OnMarker("guiding_started"), Is.EqualTo(PaResidualAction.Started));
            Recovery(tracker, 0, 20);
            tracker.OnMarker("settle_done");
            Frames(tracker, 22, 42);

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11), "the start did not wipe the open settle");
            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void The_rig_order_restart_then_dither_samples_only_the_settled_frames() {
            // Rig night 2026-11-22: a re-center slew stopped guiding, the Dither restarted it (guiding_started,
            // its settle) and then dithered (dithered, settling, settle_done).
            var tracker = NewTracker();
            tracker.OnMarker("guiding_stopped");
            tracker.OnMarker("guiding_started");
            tracker.OnMarker("settle_begin");
            Recovery(tracker, 0, 20);
            tracker.OnMarker("settle_done");
            Frames(tracker, 22, 32);
            tracker.OnMarker("dithered");
            tracker.OnMarker("settling");
            Recovery(tracker, 34, 44);
            tracker.OnMarker("settle_done");
            Frames(tracker, 46, 56);

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(12), "6 + 6 settled frames");
            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
            Assert.That(tracker.Measuring, Is.True);
        }

        [Test]
        public void A_stop_ends_an_open_settle_so_the_next_start_without_one_samples() {
            // #2072 — a dither whose settle_done never came (guiding stopped mid-settle), then a start from the guider's
            // own page (no settle_begin): its frames are samples, not held back by the old run's settle.
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 10);
            tracker.OnMarker("dithered");
            tracker.OnMarker("settle_begin");
            tracker.OnMarker("guiding_stopped");
            Assert.That(tracker.OnMarker("guiding_started"), Is.EqualTo(PaResidualAction.Started));
            Frames(tracker, 100, 120);

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11));
            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void A_start_the_guider_runs_no_settle_for_samples_from_its_first_frame() {
            // A start from the guider's own page: no settle_begin before its first frame, so no settle_done will come.
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            Frames(tracker, 0, 20);

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11));
            Assert.That(tracker.Estimator.SampleSeconds, Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void A_Dec_reversal_holds_sampling_until_the_star_answers_the_new_pulses() {
            // #2072 — North pulses, then South ones the gear's backlash eats: the star stays North (positive) until it
            // crosses the lock position, and those frames are not samples.
            var tracker = NewTracker();
            tracker.OnMarker("guiding_started");
            for (var t = 0; t <= 20; t += 2) {
                tracker.OnStep(t, -0.2, 100); // 11 frames, North
            }
            for (var t = 22; t <= 40; t += 2) {
                tracker.OnStep(t, 0.3, -100); // 10 frames, South, unanswered
            }
            for (var t = 42; t <= 60; t += 2) {
                tracker.OnStep(t, -0.05, -20); // the star is across: 10 frames, sampled
            }

            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11 + 2 + 10), "the two South frames before the reversal counted, then the answered ones");
            Assert.That(tracker.Measuring, Is.True);
        }

        [Test]
        public void A_Dec_reversal_the_star_never_answers_is_let_through_after_the_backlash_hold() {
            var tracker = new PaResidualTracker(targetSeconds: 600, minSeconds: 20, progressEverySeconds: 10, backlashHoldSeconds: 30);
            tracker.OnMarker("guiding_started");
            for (var t = 0; t <= 20; t += 2) {
                tracker.OnStep(t, -0.2, 100);
            }
            for (var t = 22; t <= 100; t += 2) {
                tracker.OnStep(t, 0.3, -100); // never answered
            }

            // The reversal is the third South frame (t = 26): held to t = 56, sampled from 56 to 100 (23 frames).
            Assert.That(tracker.Estimator.Frames, Is.EqualTo(11 + 2 + 23));
        }

        [Test]
        public void The_2026_11_22_rig_window_replayed_is_flagged_unanswered_and_never_reads_45_arcmin() {
            // #2072 — the guide log of the bad window, 30 s bins of mean DECRaw and Dec pulse ms (6.45″/px; the daemon's
            // 45.61′ from net −8161 ms puts the Dec rate at 1.14 px/s). Start settle 0–20 s, a dither and its settle
            // 20–60 s, then guiding: North pulses with the star on the lock position, a reversal to South at 210 s, and
            // the star creeping North (+0.17 → +0.33 px) while every pulse goes South. The real error was ≈0.6′.
            (double Raw, double PulseMs)[] bins = [
                (-0.24, 1930), (-1.13, 6313), (0.00, 192), (-0.02, 148), (-0.03, 109), (-0.01, 212),
                (0.05, 0), (0.17, -1302), (0.24, -1675), (0.26, -2193), (0.33, -923), (0.35, -923),
            ];
            const double frameSec = 2.3, binSec = 30;
            var tracker = new PaResidualTracker(); // the daemon's own settings
            tracker.OnMarker("guiding_started");
            tracker.OnMarker("settle_begin");
            var (startSettleDone, ditherSettleDone) = (false, false);
            for (var t = 0.0; t < 365; t += frameSec) {
                if (!startSettleDone && t >= 20) {
                    tracker.OnMarker("settle_done");
                    tracker.OnMarker("dithered");
                    tracker.OnMarker("settle_begin");
                    startSettleDone = true;
                }
                if (!ditherSettleDone && t >= 60) {
                    tracker.OnMarker("settle_done");
                    ditherSettleDone = true;
                }
                var (raw, pulseMs) = bins[Math.Min((int)(t / binSec), bins.Length - 1)]; // 330–365 s: the last bin
                tracker.OnStep(t, raw, pulseMs * frameSec / binSec);
            }

            var fit = tracker.Estimator.Fit(decRatePxPerSec: 1.14, pixelScaleArcsec: 6.45);
            Assert.That(fit, Is.Not.Null);
            Assert.That(fit!.Value.DecPulsesUnanswered, Is.True, "the pulses did not reach the star");
            // Without the hold it reads 42′ over 301 s and 132 frames, the rig's 45.6′ over 301 s and 130 frames.
            Assert.That(fit.Value.PaErrorMinArcmin, Is.LessThan(10), "the backlash after the reversal is held back");
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
