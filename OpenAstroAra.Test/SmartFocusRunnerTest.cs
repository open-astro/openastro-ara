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
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.2 slice C — the Smart Focus one-frame runner and its §59.11 fallback ladder: a calibrated
    /// profile predicts the focuser move from one exposure (2-3 shots total), every failure mode
    /// degrades to the Classic 9-probe sweep, and the run announces itself on the §59.15 WS events.
    /// The scripted rig drives HFR as a pure function of the focuser's CURRENT position, so the "real"
    /// best focus can be placed anywhere relative to what the stored calibration claims.
    /// </summary>
    [TestFixture]
    public class SmartFocusRunnerTest {

        private const int StartPosition = 10_150;
        private const int CalibratedBest = 10_000;
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static AutofocusSettingsDto Settings() => new(
            Method: "hfr_v_curve", Steps: 4, StepSize: 100, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: true);

        /// <summary>A stored calibration whose V-curve is HFR = 1.5 + 0.2·((p − best)/100)², parabolic
        /// about <paramref name="bestPosition"/> — 9 samples, same shape the scripted sky produces.</summary>
        private static FocusCalibrationDto Calibration(int bestPosition = CalibratedBest, double? temperatureC = 12.5) {
            var samples = new List<FocusCalibrationSampleDto>();
            for (int i = -4; i <= 4; i++) {
                int position = bestPosition + i * 100;
                samples.Add(FocusCalibrationSampleDto.From(position, Features(VCurveHfr(position, bestPosition), 42)));
            }
            return new FocusCalibrationDto(samples, new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero), temperatureC, "L");
        }

        private static double VCurveHfr(double position, double best) {
            var delta = (position - best) / 100.0;
            return 1.5 + 0.2 * delta * delta;
        }

        private static FocusFeatureVector Features(double hfr, int stars) =>
            new(stars, hfr, hfr * 2.0, 0.9, 8.0, 0, 0, 0, 0);

        private static List<DetectedStar> Stars(int count, double hfr) {
            var stars = new List<DetectedStar>(count);
            for (int i = 0; i < count; i++) {
                stars.Add(new DetectedStar { HFR = hfr, FWHM = hfr * 2.0, Roundness = 0.9, PeakToBackground = 8.0 });
            }
            return stars;
        }

        // A plain tuple (not a wrapper type) so CA2000 sees the service's ownership transfer to the
        // caller's `using var` — the same pattern as AutofocusSweepServiceTest.Build.

        /// <summary>The scripted sky: each capture measures HFR at the focuser's current position from
        /// <paramref name="skyHfr"/> (defaults to the V-curve about <paramref name="realBest"/>), with
        /// <paramref name="starCount"/> stars. Captures are counted so tests can assert the shot budget.</summary>
        private static (AutofocusSweepService Service, InMemoryProfileStore Store, List<int> Moves,
                List<(string Type, JsonElement Payload)> Events, Func<int> CaptureCount) Build(
                int realBest = CalibratedBest,
                FocusCalibrationDto? calibration = null,
                double focuserTemperature = 12.5,
                int starCount = 42,
                Func<int, int, double>? skyHfr = null) {
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings());
            if (calibration is not null) {
                store.PutFocusCalibration(calibration);
            }

            var moves = new List<int>();
            var position = StartPosition;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo {
                Connected = true, Position = position, Temperature = focuserTemperature,
            });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });

            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UnixEpoch));

            var events = new List<(string Type, JsonElement Payload)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => events.Add((t, p)))
                .Returns(Task.CompletedTask);

            int captures = 0;
            var svc = new AutofocusSweepService(
                store, focuser.Object, frames.Object,
                ws: ws.Object,
                metric: (_, _) => {
                    captures++;
                    var hfr = skyHfr is null ? VCurveHfr(position, realBest) : skyHfr(captures, position);
                    return new StarDetectionResult {
                        AverageHFR = hfr, DetectedStars = starCount, StarList = Stars(starCount, hfr),
                    };
                });
            return (svc, store, moves, events, () => captures);
        }

        private static List<string> Types(List<(string Type, JsonElement Payload)> events) =>
            events.Select(e => e.Type).ToList();

        [Test]
        public async Task A_calibrated_rig_in_steady_conditions_focuses_in_two_shots() {
            // The sky agrees with the calibration (real best == calibrated best): shot 1 at 10150 reads
            // HFR 1.95 → predicted magnitude ≈ 150 toward 10000; shot 2 lands at ~1.5 ≤ target → done.
            var rig = Build(calibration: Calibration());
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(2), "the §59.2 payoff — 2 exposures, not 9 probes");
            Assert.That(rig.Moves[^1], Is.EqualTo(CalibratedBest).Within(30));
            var started = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusStarted).ToList();
            Assert.That(started, Has.Count.EqualTo(1));
            Assert.That(started[0].Payload.GetProperty("mode").GetString(), Is.EqualTo("smart"));
            Assert.That(Types(rig.Events).Count(t => t == WsEventCatalog.AutofocusShotComplete), Is.EqualTo(2));
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
        }

        [Test]
        public async Task An_uncalibrated_profile_runs_classic_silently() {
            var rig = Build(calibration: null, realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(9), "no calibration → the full Classic sweep");
            var started = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusStarted).ToList();
            Assert.That(started, Has.Count.EqualTo(1));
            Assert.That(started[0].Payload.GetProperty("mode").GetString(), Is.EqualTo("classic"));
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic),
                "never-calibrated is not a fallback — Classic IS the calibrator");
        }

        [Test]
        public async Task A_thermally_stale_calibration_runs_classic() {
            // Calibrated at 12.5 °C, focuser now at 25 °C — 12.5 °C drift > the 8 °C §59.13 gate.
            var rig = Build(calibration: Calibration(temperatureC: 12.5), focuserTemperature: 25.0,
                realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(9), "stale calibration must not be trusted for a one-frame move");
        }

        [Test]
        public async Task A_calibration_without_a_temperature_cannot_be_judged_stale_and_still_runs_smart() {
            var rig = Build(calibration: Calibration(temperatureC: null), focuserTemperature: 25.0);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(2), "no calibration temperature → the staleness gate can't fire (§59.13)");
        }

        [Test]
        public async Task Too_few_stars_on_the_smart_shot_falls_back_to_classic() {
            var rig = Build(calibration: Calibration(), starCount: 12, realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "Classic tolerates 12 stars (its per-probe gate is 3)");
            Assert.That(rig.CaptureCount(), Is.EqualTo(1 + 9), "one Smart shot, then the full sweep");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("too_few_stars"));
        }

        [Test]
        public async Task A_frame_beyond_the_calibrated_range_falls_back_to_classic() {
            // Shot 1 reads HFR 20 — far beyond the calibration's most-defocused sample (~4.7) →
            // PredictOffsetMagnitude refuses to extrapolate (§59.11) → Classic, which then sees a
            // normal V-curve about the start and completes the run.
            var rig = Build(calibration: Calibration(), skyHfr: (capture, position) =>
                capture == 1 ? 20.0 : VCurveHfr(position, StartPosition));
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(1 + 9), "one refused Smart shot, then the full sweep");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("outside_calibrated_range"));
        }

        [Test]
        public async Task An_already_focused_rig_finishes_in_one_shot_without_moving() {
            var rig = Build(realBest: StartPosition, calibration: Calibration(bestPosition: StartPosition));
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(1));
            Assert.That(rig.Moves, Is.Empty, "already within tolerance — moving would only add backlash noise");
        }

        [Test]
        public async Task A_wrong_direction_guess_recovers_by_reversing_with_half_magnitude() {
            // The calibration says best focus is BELOW the start (10 000), but the rig has drifted so the
            // real best is ABOVE it (10 300). Shot 2 (toward 10 000) gets worse; the ladder reverses from
            // the start with half the magnitude (10 150 + 75 = 10 225) — closer to 10 300, improved → done.
            var rig = Build(calibration: Calibration(), realBest: StartPosition + 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3), "shot 1 + wrong-direction shot 2 + reversed shot 3");
            Assert.That(rig.Moves[^1], Is.GreaterThan(StartPosition), "the final position is on the REAL best's side");
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
        }

        [Test]
        public async Task A_diverging_run_restores_the_start_and_falls_back_to_classic() {
            // A scripted sky where EVERY Smart shot after the first is worse (shots 2 and 3 read flat 9.0
            // while shot 1 reads 1.95) — the ladder exhausts, restores the start position, and hands the
            // run to Classic, whose probes then see a normal V-curve about the start.
            var rig = Build(calibration: Calibration(), skyHfr: (capture, position) => capture switch {
                1 => 1.95,           // shot 1 at the start — defocused but predictable
                2 or 3 => 9.0,       // both Smart attempts get dramatically worse
                _ => VCurveHfr(position, StartPosition), // the Classic probes
            });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "the Classic fallback completes the run");
            Assert.That(rig.CaptureCount(), Is.EqualTo(3 + 9), "3 Smart shots, then the full sweep");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("smart_focus_diverged"));
            // The restore-then-sweep is visible in the move history: back at the start before probing.
            Assert.That(rig.Moves, Does.Contain(StartPosition), "the start position is restored before Classic runs");
        }

        [Test]
        public async Task An_improved_but_missed_shot_two_takes_one_trim_shot_and_keeps_the_better_position() {
            // Scripted: shot 1 reads 1.95 (needs ~150), shot 2 improves to 1.7 but misses the ≤1.575
            // target, the +20% trim shot reads 1.9 (worse than shot 2) → the runner returns to shot 2's
            // position and succeeds in exactly 3 shots.
            int? position2 = null;
            var rig = Build(calibration: Calibration(), skyHfr: (capture, position) => {
                switch (capture) {
                    case 1: return 1.95;
                    case 2: position2 = position; return 1.7;
                    default: return 1.9;
                }
            });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3), "three shots is the Smart budget — never a fourth");
            Assert.That(position2, Is.Not.Null);
            Assert.That(rig.Moves[^1], Is.EqualTo(position2), "the trim was worse — keep shot 2's position");
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
        }

        [Test]
        public async Task Smart_success_records_the_autofocus_reference_point() {
            var history = new ImageHistoryService();
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings());
            store.PutFocusCalibration(Calibration());

            var position = StartPosition;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = 12.5 });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; return Task.FromResult(p); });
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UnixEpoch));

            using var svc = new AutofocusSweepService(
                store, focuser.Object, frames.Object, history: history,
                metric: (_, _) => {
                    var hfr = VCurveHfr(position, CalibratedBest);
                    return new StarDetectionResult { AverageHFR = hfr, DetectedStars = 42, StarList = Stars(42, hfr) };
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(history.AutofocusPoints, Is.Not.Empty,
                "a Smart success must anchor the §59.5 triggers exactly like a Classic one");
        }
    }
}
