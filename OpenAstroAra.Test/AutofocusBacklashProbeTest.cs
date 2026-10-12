#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§59.7 layer 1 (#1139) — the backlash probe appended to a Classic sweep, on a mocked focuser
    /// whose optics stop short of the counter by a fixed number of steps after every UPWARD move.</summary>
    [TestFixture]
    public class AutofocusBacklashProbeTest {

        private const int Start = 10_000;
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static AutofocusSettingsDto Settings() => new(
            Method: "hfr_v_curve", Steps: 4, StepSize: 100, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: true, StepSizeAuto: false);

        /// <summary>A focuser with <paramref name="backlashUp"/> steps of upward backlash: the COUNTER reads the
        /// commanded position, the OPTICS sit that many steps below it after an upward move (and exactly on
        /// it after a downward one). The metric reads the optics.</summary>
        private static (AutofocusSweepService Svc, InMemoryProfileStore Store, List<int> Moves, Func<int> Captures, AutofocusRunTracker Tracker, List<(string Type, JsonElement Payload)> Events) Build(
                int backlashUp, double bestPhysical = Start - 150, Func<int, int>? starsAt = null, int travelMin = 0, int travelMax = int.MaxValue, Action<int>? onCapture = null,
                double hfrShiftAfterSweep = 0) {
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings());
            var moves = new List<int>();
            var counter = Start;
            var optics = Start;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = counter, Temperature = 10 });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => {
                    optics = p > counter ? p - backlashUp : p;
                    counter = p;
                    moves.Add(p);
                    return Task.FromResult(p);
                });
            var frames = new Mock<IAnalysisFrameSource>();
            var captures = 0;
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<double, int, CancellationToken>((_, _, ct) => {
                    onCapture?.Invoke(captures + 1);
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(new AnalysisFrame(new ushort[64 * 64], 64, 64, DateTimeOffset.UnixEpoch)); // ≥ 64 px: the run record renders it
                });
            var events = new List<(string Type, JsonElement Payload)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => events.Add((t, p.Clone())))
                .Returns(Task.CompletedTask);
            var tracker = new AutofocusRunTracker();
            var svc = new AutofocusSweepService(store, focuser.Object, frames.Object, tracker: tracker, ws: ws.Object,
                travelRange: _ => Task.FromResult<(int, int)?>((travelMin, travelMax)),
                metric: (_, _) => {
                    captures++;
                    var delta = (optics - bestPhysical) / 100.0;
                    // Captures after the 9-probe sweep (confirmation + probe shots) may read off the fit's
                    // minimum, as a real confirmation frame does — the probe must work on the excess.
                    var hfr = 1.5 + 0.2 * delta * delta + (captures > 9 ? hfrShiftAfterSweep : 0);
                    var stars = starsAt?.Invoke(captures) ?? 42;
                    return new StarDetectionResult { AverageHFR = hfr, DetectedStars = stars, StarList = Stars(stars, hfr) };
                });
            return (svc, store, moves, () => captures, tracker, events);
        }

        /// <summary>A star list consistent with the HFR, so the calibration's feature samples are usable.</summary>
        private static List<DetectedStar> Stars(int count, double hfr) {
            var stars = new List<DetectedStar>(count);
            for (var i = 0; i < count; i++) {
                stars.Add(new DetectedStar { HFR = hfr, FWHM = hfr * 2.0, Roundness = 0.9, PeakToBackground = 8.0, DonutOuterDiameter = hfr * 2.0 });
            }
            return stars;
        }

        [Test]
        public void Inverting_the_fitted_V_turns_a_return_HFR_back_into_a_distance() {
            static double V(double p) => 1.5 + 0.2 * Math.Pow((p - 1000) / 100.0, 2);
            Assert.That(AutofocusSweepService.InvertBelowBest(V, 1000, V(970), 400), Is.EqualTo(30).Within(0.01));
            Assert.That(AutofocusSweepService.InvertBelowBest(V, 1000, 1.5, 400), Is.EqualTo(0), "at the baseline: no backlash");
            Assert.That(AutofocusSweepService.InvertBelowBest(V, 1000, 99, 400), Is.EqualTo(400), "beyond the control: clamped");
            Assert.That(AutofocusSweepService.InvertBelowBest(null, 1000, 2, 400), Is.Null);
            Assert.That(AutofocusSweepService.InvertBelowBest(_ => 1.5, 1000, 2, 400), Is.Null, "a flat model places nothing");
        }

        [Test]
        public void The_return_tolerance_is_the_curves_rise_over_a_quarter_step_above_the_baseline() {
            static double V(double p) => 1.5 + 0.2 * Math.Pow((p - 1000) / 100.0, 2);
            // model(975) − model(1000) = 0.2 × 0.0625 = 0.0125 above the MEASURED 1.52 baseline.
            Assert.That(AutofocusSweepService.ReturnTolerance(V, 1000, 1.52, 100), Is.EqualTo(1.5325).Within(1e-9));
            Assert.That(AutofocusSweepService.ReturnTolerance(_ => 1.5, 1000, 1.5, 100), Is.Null, "a flat curve resolves nothing");
            Assert.That(AutofocusSweepService.ReturnTolerance(null, 1000, 1.5, 100), Is.Null);
        }

        [Test]
        public async Task A_focuser_with_30_steps_of_upward_backlash_is_measured_verified_and_left_on_best_from_above() {
            var (svc, store, moves, captures, tracker, events) = Build(backlashUp: 30);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            var cal = store.GetFocusCalibration();
            Assert.That(cal, Is.Not.Null);
            Assert.That(cal!.BacklashUpSteps, Is.EqualTo(30).Within(2), "read off the fitted V from the return shot");
            Assert.That(cal.BacklashVerified, Is.True, "the compensated upward move landed back on the baseline");
            Assert.That(captures(), Is.EqualTo(9 + 1 + 3), "sweep, confirmation, control + return + verification");
            // The run record shows the three probe frames, and the focuser ends on best, landed from above.
            var record = tracker.Snapshot();
            Assert.That(record.Probes.Count(p => p.Phase == "backlash"), Is.EqualTo(3));
            var best = moves[^1];
            Assert.That(record.FramePosition, Is.EqualTo(best), "the run's picture is still the confirmation frame at best, not a probe shot");
            var completed = events.Single(e => e.Type == "autofocus.completed").Payload;
            Assert.That(completed.GetProperty("backlash_up_steps").GetDouble(), Is.EqualTo(30).Within(2));
            Assert.That(completed.GetProperty("backlash_verified").GetBoolean(), Is.True);
            Assert.That(moves[^2], Is.EqualTo(best + 100), "re-entered from above");
            Assert.That(moves.Skip(moves.Count - 6).Take(4), Is.EqualTo(new[] { best - 400, best, best - 400, best + 30 }).Within(2),
                "control, straight return, control again, compensated return");
        }

        [Test]
        public async Task A_confirmation_frame_that_reads_above_the_fits_minimum_still_measures_the_backlash_right() {
            // Review of #1319: the measured baseline is 0.1 px above model(best). Inverting the return shot's
            // absolute HFR against the model would place a 30-step shortfall at ~77 steps and fail to verify.
            var (svc, store, _, captures, _, _) = Build(backlashUp: 30, hfrShiftAfterSweep: 0.1);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            var cal = store.GetFocusCalibration()!;
            Assert.That(cal.BacklashUpSteps, Is.EqualTo(30).Within(2));
            Assert.That(cal.BacklashVerified, Is.True);
            Assert.That(captures(), Is.EqualTo(9 + 1 + 3));

            // …and the other sign: a baseline BELOW the fit's minimum must not read as "no backlash".
            var (svc2, store2, _, _, _, _) = Build(backlashUp: 30, hfrShiftAfterSweep: -0.1);
            using var __ = svc2;
            Assert.That(await svc2.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);
            Assert.That(store2.GetFocusCalibration()!.BacklashUpSteps, Is.EqualTo(30).Within(2));
            Assert.That(store2.GetFocusCalibration()!.BacklashVerified, Is.True);
        }

        [Test]
        public async Task A_backlash_free_focuser_records_zero_in_two_frames() {
            var (svc, store, _, captures, _, _) = Build(backlashUp: 0);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            var cal = store.GetFocusCalibration()!;
            Assert.That(cal.BacklashUpSteps, Is.EqualTo(0));
            Assert.That(cal.BacklashVerified, Is.True);
            Assert.That(captures(), Is.EqualTo(9 + 1 + 2), "the return shot already reads the baseline: no verification shot");
        }

        [Test]
        public async Task A_starless_probe_shot_records_nothing_and_still_leaves_focus_on_best() {
            // Captures 1–9 sweep, 10 confirmation, 11 control (fine), 12 return: clouds.
            var (svc, store, moves, _, tracker, events) = Build(backlashUp: 30, starsAt: n => n == 12 ? 2 : 42);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True, "the sweep itself succeeded");

            var cal = store.GetFocusCalibration()!;
            Assert.That(cal.BacklashUpSteps, Is.Null);
            Assert.That(cal.BacklashVerified, Is.Null);
            Assert.That(moves[^2], Is.EqualTo(moves[^1] + 100), "still landed on best from above");
            Assert.That(tracker.Snapshot().FramePosition, Is.EqualTo(moves[^1]), "the picture is the confirmation frame, not the control shot");
            var completed = events.Single(e => e.Type == "autofocus.completed").Payload;
            Assert.That(completed.TryGetProperty("backlash_up_steps", out var _steps), Is.False, "nothing measured, nothing announced");
            Assert.That(completed.TryGetProperty("backlash_verified", out var _verified), Is.False);
        }

        [Test]
        public async Task A_cancel_during_the_probe_keeps_the_completed_sweep_and_records_no_backlash() {
            // Captures 1–9 sweep, 10 confirmation, 11 control: the user cancels there.
            using var cts = new CancellationTokenSource();
            var (svc, store, moves, _, tracker, _) = Build(backlashUp: 30, onCapture: n => { if (n == 11) { cts.Cancel(); } });
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, cts.Token), Is.True, "the sweep had already succeeded");

            var cal = store.GetFocusCalibration();
            Assert.That(cal, Is.Not.Null, "the calibration is recorded without backlash");
            Assert.That(cal!.BacklashUpSteps, Is.Null);
            Assert.That(moves[^2], Is.EqualTo(moves[^1] + 100), "landed on best from above, not restored to the start");
            Assert.That(moves[^1], Is.Not.EqualTo(Start));
            Assert.That(tracker.Snapshot().State, Is.EqualTo("complete"));
        }

        [Test]
        public async Task A_compensated_return_past_the_travel_top_is_not_attempted() {
            // The travel stops 120 above best (the sweep still fits: its top probe sits at best + 20) but a
            // 150-step backlash would need a compensated return at best + 150.
            var best = Start - 150;
            var (svc, store, moves, _, _, _) = Build(backlashUp: 150, bestPhysical: best, travelMax: best + 120);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            Assert.That(moves.Max(), Is.LessThanOrEqualTo(best + 120), "never past the travel");
            Assert.That(store.GetFocusCalibration()!.BacklashUpSteps, Is.Null, "unverifiable inside the travel: nothing recorded");
        }

        [Test]
        public async Task The_control_shot_stays_inside_the_focusers_travel() {
            // The travel stops 250 below best: the probe's 4-step (400) control is shortened to 250, never past it.
            var best = Start - 150;
            var (svc, store, moves, _, _, _) = Build(backlashUp: 30, bestPhysical: best, travelMin: best - 250);
            using var _ = svc;

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            Assert.That(moves.Min(), Is.GreaterThanOrEqualTo(best - 250), "never below the travel");
            Assert.That(moves, Does.Contain(best - 250), "the control shot at the shortened span");
            Assert.That(store.GetFocusCalibration()!.BacklashUpSteps, Is.EqualTo(30).Within(2));
        }
    }
}
