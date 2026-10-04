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
using OpenAstroAra.Server.Endpoints;
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

        private static AutofocusSettingsDto Settings(bool restore = true, string telescopeType = "other") => new(
            Method: "hfr_v_curve", Steps: 4, StepSize: 100, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: restore,
            TelescopeType: telescopeType);

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
            new(stars, hfr, hfr * 2.0, 0.9, 8.0, hfr * 2.0, 0, hfr * 2.0, 0, 0);

        private static List<DetectedStar> Stars(int count, double hfr, double skew = 0.0) {
            var stars = new List<DetectedStar>(count);
            for (int i = 0; i < count; i++) {
                stars.Add(new DetectedStar {
                    HFR = hfr, FWHM = hfr * 2.0, Roundness = 0.9, PeakToBackground = 8.0,
                    DonutOuterDiameter = hfr * 2.0, // consistent with Features() so a donut-keyed map reads it
                    RadialProfileSkew = skew,
                });
            }
            return stars;
        }

        /// <summary>A calibration whose arms carry the §59.3 side signature: skew
        /// <paramref name="belowSkew"/> below best, <paramref name="aboveSkew"/> above — what slice B
        /// records on a rig with spherical aberration.</summary>
        private static FocusCalibrationDto SignedCalibration(
                int bestPosition = CalibratedBest, double belowSkew = -0.4, double aboveSkew = 0.4) {
            var samples = new List<FocusCalibrationSampleDto>();
            for (int i = -4; i <= 4; i++) {
                if (i == 0) continue; // the vertex sample carries no side label
                int position = bestPosition + i * 100;
                double skew = i < 0 ? belowSkew : aboveSkew;
                samples.Add(FocusCalibrationSampleDto.From(position,
                    Features(VCurveHfr(position, bestPosition), 42) with { MedianRadialSkew = skew }));
            }
            return new FocusCalibrationDto(samples, new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero), 12.5, "L");
        }

        // A plain tuple (not a wrapper type) so CA2000 sees the service's ownership transfer to the
        // caller's `using var` — the same pattern as AutofocusSweepServiceTest.Build.

        /// <summary>The scripted sky: each capture measures HFR at the focuser's current position from
        /// <paramref name="skyHfr"/> (defaults to the V-curve about <paramref name="realBest"/>), with
        /// <paramref name="starCount"/> stars. Captures are counted so tests can assert the shot budget.</summary>
        private static (AutofocusSweepService Service, InMemoryProfileStore Store, List<int> Moves,
                List<(string Type, JsonElement Payload)> Events, Func<int> CaptureCount,
                List<NotificationDto> Notifications, AutofocusRunTracker Tracker) Build(
                int realBest = CalibratedBest,
                FocusCalibrationDto? calibration = null,
                double focuserTemperature = 12.5,
                int starCount = 42,
                Func<int, int, double>? skyHfr = null,
                bool restoreOnFailure = true,
                Func<int, int>? skyStars = null,
                string telescopeType = "other",
                Func<int, double>? skySkew = null,
                Func<int, bool>? noiseFlooded = null) {
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings(restoreOnFailure, telescopeType));
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

            var posted = new List<NotificationDto>();
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns<NotificationDto, CancellationToken>((d, _) => { posted.Add(d); return Task.CompletedTask; });

            int captures = 0;
            var tracker = new AutofocusRunTracker();
            var svc = new AutofocusSweepService(
                store, focuser.Object, frames.Object,
                ws: ws.Object,
                notifications: notifications.Object,
                tracker: tracker,
                metric: (_, _) => {
                    captures++;
                    var hfr = skyHfr is null ? VCurveHfr(position, realBest) : skyHfr(captures, position);
                    var stars = skyStars is null ? starCount : skyStars(captures);
                    // The physical §59.3 side signal: the sky's skew depends on which side of the REAL
                    // best focus the focuser currently sits (0 when the rig carries no aberration).
                    var skew = skySkew?.Invoke(position) ?? 0.0;
                    return new StarDetectionResult {
                        AverageHFR = hfr, DetectedStars = stars, StarList = Stars(stars, hfr, skew),
                        NoiseFlooded = noiseFlooded?.Invoke(captures) ?? false,
                    };
                });
            return (svc, store, moves, events, () => captures, posted, tracker);
        }

        /// <summary>A Smart success must close the §59.12 run record and announce it: a record left
        /// `running` keeps the Smart Focus pane spinning and the OAG gate shut.</summary>
        private static void AssertSmartRunRecorded(AutofocusRunTracker tracker,
                List<(string Type, JsonElement Payload)> events, int finalPosition) {
            var record = tracker.Snapshot();
            Assert.That(record.State, Is.EqualTo("complete"));
            Assert.That(record.FinalPosition, Is.EqualTo(finalPosition));
            var completed = events.Where(e => e.Type == WsEventCatalog.AutofocusCompleted).ToList();
            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(completed[0].Payload.GetProperty("mode").GetString(), Is.EqualTo("smart"));
            Assert.That(completed[0].Payload.GetProperty("final_position").GetInt32(), Is.EqualTo(finalPosition));
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
            AssertSmartRunRecorded(rig.Tracker, rig.Events, rig.Moves[^1]);
            var record = rig.Tracker.Snapshot();
            Assert.That((record.CompletedSteps, record.TotalSteps), Is.EqualTo((2, AutofocusSweepService.SmartMaxShots)),
                "a Smart run's progress is its shots, not the Classic probe count");
        }

        [Test]
        public async Task An_uncalibrated_profile_runs_classic_silently() {
            var rig = Build(calibration: null, realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(9 + 1), "no calibration → the full Classic sweep, plus its confirmation frame at best focus");
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
            Assert.That(rig.CaptureCount(), Is.EqualTo(9 + 1), "stale calibration must not be trusted for a one-frame move (Classic sweep + confirmation frame)");
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
        public async Task A_noise_flooded_smart_shot_falls_back_to_classic_despite_its_blob_count() {
            // 21,000 "stars" at HFR 2 on a frame with one real star (camera at power-on gain,
            // 2026-10-03): the count passes the 30-star gate, the flag must not.
            var rig = Build(calibration: Calibration(), starCount: 21000, realBest: StartPosition - 150,
                noiseFlooded: capture => capture == 1);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "the sky cleared for the classic sweep");
            Assert.That(rig.CaptureCount(), Is.EqualTo(1 + 9 + 1), "one refused Smart shot, then the sweep and its confirmation");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("too_few_stars"));
            Assert.That(rig.Tracker.Snapshot().Probes.Any(p => p.Phase == "smart"), Is.False);
        }

        [Test]
        public async Task Too_few_stars_on_the_smart_shot_falls_back_to_classic() {
            var rig = Build(calibration: Calibration(), starCount: 12, realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "Classic tolerates 12 stars (its per-probe gate is 3)");
            Assert.That(rig.CaptureCount(), Is.EqualTo(1 + 9 + 1), "one Smart shot, then the full sweep and its confirmation frame");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("too_few_stars"));
            Assert.That(Types(rig.Events).Count(t => t == WsEventCatalog.AutofocusStarted), Is.EqualTo(1),
                "fallback_classic IS the mode hand-off — never a second started");
            var record = rig.Tracker.Snapshot();
            Assert.That(record.Mode, Is.EqualTo("classic"));
            Assert.That(record.Probes.Any(p => p.Phase == "smart"), Is.False, "the refused Smart shot is not a V-curve point");
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
            Assert.That(rig.CaptureCount(), Is.EqualTo(1 + 9 + 1), "one refused Smart shot, then the full sweep and its confirmation frame");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("outside_calibrated_range"));
        }

        [Test]
        public async Task An_already_focused_rig_is_confirmed_by_a_bracket_and_finishes_in_three_shots() {
            // One shot against the stored in-focus HFR is a claim, not a check (2026-10-03: "it only took
            // one shot and was 0.97"). The bracket shots either side must both read clearly worse.
            var rig = Build(realBest: StartPosition, calibration: Calibration(bestPosition: StartPosition));
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(4), "the in-focus shot, one bracket shot each side (a symmetric V puts the vertex at the centre, so no vertex shot), one confirmation at the centre");
            // No half-width in this calibration → half the classic span: 100 × 4 / 2. The − side first
            // (moving down like every calibration sample), the + side, then the centre from above
            // through the sweep's overshoot (one step size) so backlash matches the calibration.
            Assert.That(rig.Moves, Is.EqualTo(new[] { StartPosition - 200, StartPosition + 200, StartPosition + 100, StartPosition }));
            AssertSmartRunRecorded(rig.Tracker, rig.Events, StartPosition);
            var record = rig.Tracker.Snapshot();
            Assert.That(record.Probes.Count(p => p.Phase == "smart"), Is.EqualTo(3));
            Assert.That(record.Fit, Is.Not.Null, "the V is fitted through this run's own shots");
            Assert.That(record.Fit!.Algorithm, Is.Not.EqualTo("calibration"), "a real fit, not the calibration's model curve");
            Assert.That(record.Fit.BestPosition, Is.EqualTo(StartPosition).Within(1));
            Assert.That(record.Fit.PredictedHfr, Is.EqualTo(1.5).Within(0.05), "the curve bottoms on the measured dots");
        }

        [Test]
        public async Task A_bracket_shot_that_reads_better_than_the_centre_falls_back_to_the_classic_sweep() {
            // Shot 1 passes the stored target, but the + side reads sharper: the stored number lied, so
            // the sweep runs and finds the real focus 200 steps up.
            var rig = Build(realBest: StartPosition + 200, calibration: Calibration(bestPosition: StartPosition),
                skyHfr: (capture, position) => capture switch {
                    1 => 1.5,   // ≤ target at the start position
                    2 => 2.6,   // − bracket (taken first)
                    3 => 1.2,   // + bracket: better than the centre
                    _ => VCurveHfr(position, StartPosition + 200),
                });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("bracket_failed"));
            Assert.That(rig.CaptureCount(), Is.GreaterThan(3), "the classic sweep ran after the bracket");
            Assert.That(rig.Moves[^1], Is.EqualTo(StartPosition + 200).Within(30), "the sweep found the real focus");
        }

        [Test]
        public async Task A_lopsided_bracket_takes_a_vertex_shot_and_keeps_it_when_sharper() {
            // − side 2.6, centre 1.5, + side 2.0 at ±200: the parabola's vertex is +38 steps. Shot 4 there
            // reads 1.3 (< 1.5 × 0.97) so the run ends at 10,188 after a confirmation frame — five shots.
            var rig = Build(realBest: StartPosition, calibration: Calibration(bestPosition: StartPosition),
                skyHfr: (capture, position) => capture switch {
                    1 => 1.5, 2 => 2.6, 3 => 2.0, 4 => 1.3, _ => 1.3,
                });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(5));
            Assert.That(rig.Moves, Is.EqualTo(new[] { StartPosition - 200, StartPosition + 200, StartPosition + 38 + 100, StartPosition + 38 }),
                "the vertex is landed from above like every other position");
            var record = rig.Tracker.Snapshot();
            Assert.That(record.FinalPosition, Is.EqualTo(StartPosition + 38));
            Assert.That(record.FinalHfr, Is.EqualTo(1.3).Within(0.01), "the confirmation frame's reading");
            Assert.That(record.Probes.Count(p => p.Phase == "smart"), Is.EqualTo(4));
            Assert.That(record.Fit!.BestPosition, Is.EqualTo(StartPosition + 38).Within(60), "the drawn V is fitted through the shots and bottoms near where the run ended");
            Assert.That(rig.Store.GetFocusCalibration()!.InFocusHfr, Is.EqualTo(1.3).Within(0.001),
                "the confirmation frame beat the calibration's stored in-focus HFR, so the target follows it");
        }

        [Test]
        public async Task A_vertex_shot_that_is_not_sharper_returns_to_the_centre() {
            var rig = Build(realBest: StartPosition, calibration: Calibration(bestPosition: StartPosition),
                skyHfr: (capture, position) => capture switch {
                    1 => 1.5, 2 => 2.6, 3 => 2.0, 4 => 1.49, _ => 1.5,
                });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(5));
            Assert.That(rig.Moves[^2..], Is.EqualTo(new[] { StartPosition + 100, StartPosition }), "back to the centre from above");
            Assert.That(rig.Tracker.Snapshot().FinalPosition, Is.EqualTo(StartPosition));
        }

        [Test]
        public async Task The_in_focus_target_is_the_measured_hfr_not_the_refitted_minimum() {
            // The sample V bottoms at 1.5, but the sweep MEASURED 1.0 at best focus. A 1.3 px first shot
            // is within 10 % of 1.5 and would have passed as in focus; against the measured 1.0 it is
            // not, so Smart Focus goes on to predict a move (and, with nothing between 1.0 and the
            // 1.5 vertex sample to interpolate on, falls back to the classic sweep).
            var rig = Build(realBest: StartPosition, calibration: Calibration(bestPosition: StartPosition) with { InFocusHfr = 1.0 },
                skyHfr: (capture, position) => capture == 1 ? 1.3 : VCurveHfr(position, StartPosition));
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            var bracketConfirmed = rig.Tracker.Snapshot().Probes.Count(p => p.Phase == "smart") == 3
                && rig.Events.All(e => e.Type != WsEventCatalog.AutofocusFallbackClassic);
            Assert.That(bracketConfirmed, Is.False, "1.3 px must not read as in focus when the sweep measured 1.0");
        }

        [Test]
        public void ParabolaVertexOffset_is_pure() {
            Assert.That(AutofocusSweepService.ParabolaVertexOffset(200, 2.6, 1.5, 2.0), Is.EqualTo(37.5).Within(0.1));
            Assert.That(AutofocusSweepService.ParabolaVertexOffset(200, 2.0, 1.0, 2.0), Is.EqualTo(0));
            Assert.That(AutofocusSweepService.ParabolaVertexOffset(277, 2.602, 1.218, 1.76), Is.EqualTo(60).Within(2), "the 2026-10-03 04:14 run");
            Assert.That(AutofocusSweepService.ParabolaVertexOffset(200, 1.0, 1.5, 1.0), Is.Null, "curves downward: no minimum");
            Assert.That(AutofocusSweepService.ParabolaVertexOffset(0, 2.0, 1.0, 2.0), Is.Null);
        }

        [Test]
        public void BracketVerdict_and_BracketOffset_are_pure() {
            Assert.That(AutofocusSweepService.BracketVerdict(1.0, 2.0, true, 2.1, true), Is.Null, "both sides clearly worse");
            Assert.That(AutofocusSweepService.BracketVerdict(1.0, 0.9, true, 2.1, true), Does.Contain("+ side"));
            Assert.That(AutofocusSweepService.BracketVerdict(1.0, 2.0, true, 0.95, true), Does.Contain("− side"));
            Assert.That(AutofocusSweepService.BracketVerdict(1.0, 1.1, true, 1.15, true), Does.Contain("no clear rise"));
            Assert.That(AutofocusSweepService.BracketVerdict(1.0, 2.0, false, 2.1, true), Does.Contain("too few stars"));

            Assert.That(AutofocusSweepService.BracketOffset(277.1, Settings()), Is.EqualTo(277));
            Assert.That(AutofocusSweepService.BracketOffset(null, Settings()), Is.EqualTo(200), "100 × 4 / 2");
            Assert.That(AutofocusSweepService.BracketOffset(0.2, Settings()), Is.EqualTo(200), "a degenerate half-width falls back");
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
            AssertSmartRunRecorded(rig.Tracker, rig.Events, rig.Moves[^1]);
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
            Assert.That(rig.CaptureCount(), Is.EqualTo(3 + 9 + 1), "3 Smart shots, then the full sweep and its confirmation frame");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("smart_focus_diverged"));
            Assert.That(Types(rig.Events).Count(t => t == WsEventCatalog.AutofocusStarted), Is.EqualTo(1),
                "one run, one started — the hand-off is fallback_classic");
            // The restore-then-sweep is visible in the move history: back at the start before probing.
            Assert.That(rig.Moves, Does.Contain(StartPosition), "the start position is restored before Classic runs");
        }

        [Test]
        public async Task A_diverging_run_with_restore_off_leaves_the_focuser_where_the_ladder_ended() {
            // RestorePositionOnFailure: false — the profile asked for no restores, so the diverged Smart
            // run must NOT move back to the start; Classic simply centers its sweep on where it stands.
            var rig = Build(calibration: Calibration(), restoreOnFailure: false,
                skyHfr: (capture, position) => capture switch {
                    1 => 1.95,
                    2 or 3 => 9.0,
                    _ => VCurveHfr(position, StartPosition),
                });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // Moves: shot 2, the reversed shot 3, then straight into Classic's overshoot — the third
            // move must NOT be a restore to the start. (Classic may legitimately LAND at the start
            // later; that's it finding real focus, not a restore.)
            Assert.That(rig.Moves.Take(3), Does.Not.Contain(StartPosition),
                "with restore off, the ladder must hand over without moving back to the pre-Smart position");
            Assert.That(Types(rig.Events).Count(t => t == WsEventCatalog.AutofocusFallbackClassic), Is.EqualTo(1));
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
            AssertSmartRunRecorded(rig.Tracker, rig.Events, position2!.Value);
        }

        [Test]
        public async Task A_better_trim_shot_wins_and_closes_the_run_record_there() {
            // Shot 2 improves but misses the target; the trim shot improves further → its position wins.
            int? position3 = null;
            var rig = Build(calibration: Calibration(), skyHfr: (capture, position) => {
                switch (capture) {
                    case 1: return 1.95;
                    case 2: return 1.7;
                    default: position3 = position; return 1.6;
                }
            });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3));
            Assert.That(position3, Is.Not.Null);
            AssertSmartRunRecorded(rig.Tracker, rig.Events, position3!.Value);
        }

        [Test]
        public async Task A_starless_shot_two_never_fakes_success_and_falls_back() {
            // Clouds roll in between shots: shot 2 detects nothing, so its MedianHFR is 0 — the smallest
            // possible value, which a raw comparison would call "in focus". The gate must fall back instead.
            var rig = Build(calibration: Calibration(), skyStars: capture => capture == 2 ? 0 : 42);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "Classic completes the run once the (scripted) sky clears");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("too_few_stars"));
            Assert.That(rig.CaptureCount(), Is.EqualTo(2 + 9 + 1), "two Smart shots, then the full sweep and its confirmation frame");
        }

        [Test]
        public async Task A_thin_star_trim_shot_keeps_shot_twos_verified_position() {
            // Shot 2 is a real, verified improvement; the ±20% trim shot comes back starless. The runner
            // must treat the untrustworthy trim as "worse" and return to shot 2's known-good position.
            int? position2 = null;
            var rig = Build(calibration: Calibration(),
                skyHfr: (capture, position) => {
                    switch (capture) {
                        case 1: return 1.95;
                        case 2: position2 = position; return 1.7; // improved, missed the ≤1.575 target
                        default: return 0.0;                       // starless trim shot
                    }
                },
                skyStars: capture => capture == 3 ? 0 : 42);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3));
            Assert.That(rig.Moves[^1], Is.EqualTo(position2), "the starless trim must not win the comparison");
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
            AssertSmartRunRecorded(rig.Tracker, rig.Events, position2!.Value);
        }

        [Test]
        public async Task A_device_fault_mid_smart_restores_and_falls_back_to_classic() {
            // The capture/metric path throws on shot 2 (device/comms fault). The Smart boundary must
            // restore the start position, publish the fallback, and let Classic complete the run —
            // never propagate out of RunAutofocusAsync (the meridian-flip executor relies on this).
            var rig = Build(calibration: Calibration(),
                skyHfr: (capture, position) => capture switch {
                    1 => 1.95, // defocused enough that a shot 2 is needed
                    2 => throw new InvalidOperationException("camera link dropped"),
                    _ => VCurveHfr(position, StartPosition),
                });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "Classic completes the run after the Smart fault");
            var fallback = rig.Events.Where(e => e.Type == WsEventCatalog.AutofocusFallbackClassic).ToList();
            Assert.That(fallback, Has.Count.EqualTo(1));
            Assert.That(fallback[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("smart_focus_error"));
            Assert.That(rig.Moves, Does.Contain(StartPosition), "the start position is restored before Classic runs");
        }

        [Test]
        public async Task A_diverged_fallback_posts_a_warning_notification() {
            var rig = Build(calibration: Calibration(), skyHfr: (capture, position) => capture switch {
                1 => 1.95,
                2 or 3 => 9.0,
                _ => VCurveHfr(position, StartPosition),
            });
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.Notifications, Has.Count.EqualTo(1), "a diverged ladder is surprising — tell the user");
            Assert.That(rig.Notifications[0].Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(rig.Notifications[0].Message, Does.Contain("diverged"));
        }

        [Test]
        public async Task A_condition_driven_fallback_does_not_notify() {
            // Thin stars (a passing cloud) → Classic quietly completes; a notification per cloud is noise.
            var rig = Build(calibration: Calibration(), starCount: 12, realBest: StartPosition - 150);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.Notifications, Is.Empty, "condition-driven fallbacks stay log + WS-event only");
        }

        // ── §59.2 slice D — mode-aware job progress + the calibration endpoints' store contract ──

        [Test]
        public void Progress_scaling_is_identity_for_a_classic_run() {
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(5, 9, 9), Is.EqualTo(5));
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(9, 9, 9), Is.EqualTo(9));
        }

        [Test]
        public void Progress_scaling_maps_smart_shots_onto_the_job_total() {
            // A Smart run reports 3 shots; the job's denominator is the Classic probe count (9).
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(1, 3, 9), Is.EqualTo(3), "shot 1 ≈ a third done");
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(2, 3, 9), Is.EqualTo(6));
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(3, 3, 9), Is.EqualTo(9));
        }

        [Test]
        public void Progress_scaling_clamps_to_the_job_total() {
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(4, 3, 9), Is.EqualTo(9), "never overshoot the total");
            Assert.That(EquipmentEndpoints.ScaleAutofocusProgress(0.1, 3, 9), Is.EqualTo(1), "a started run is at least 1 tick in");
        }

        [Test]
        public void Recalibrate_clears_the_stored_calibration_so_the_next_run_routes_classic() {
            // The endpoint's whole mechanism (§59.15): Put(null) → §59.1 routing picks Classic →
            // the successful sweep records fresh samples. Prove the store contract end-to-end.
            var store = new InMemoryProfileStore();
            store.PutFocusCalibration(Calibration());
            Assert.That(store.GetFocusCalibration(), Is.Not.Null);

            store.PutFocusCalibration(null); // what POST /api/v1/autofocus/recalibrate does

            Assert.That(store.GetFocusCalibration(), Is.Null, "cleared — the next AF run recalibrates via Classic");
        }

        // ── §59.3/§59.4 slice 4 — the classifier drives the direction when the rig permits ──

        [Test]
        public async Task A_classified_direction_beats_the_heuristic_when_the_rig_drifted_past_calibrated_best() {
            // The rig drifted so the REAL best (10300) is on the far side of the start (10150) from the
            // CALIBRATED best (10000): the old heuristic guesses toward 10000 — wrong — and pays a
            // 3-shot reversal. The calibration arms carry a learned skew signature (−0.4 below best,
            // +0.4 above), the frame at 10150 reads skew −0.4 (below the real best), so the classifier
            // sends the run UP on shot 2 directly: the §59.3 payoff, 2 shots instead of 3.
            var rig = Build(realBest: StartPosition + 150, telescopeType: "sct",
                calibration: SignedCalibration(),
                skySkew: position => position < StartPosition + 150 ? -0.4 : 0.4);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(2), "the classifier read the correct side from one frame");
            Assert.That(rig.Moves[^1], Is.GreaterThan(StartPosition), "moved TOWARD the real best, away from calibrated best");
            var shot1 = rig.Events.First(e => e.Type == WsEventCatalog.AutofocusShotComplete);
            Assert.That(shot1.Payload.GetProperty("direction_source").GetString(), Is.EqualTo("classifier"));
            Assert.That(shot1.Payload.GetProperty("predicted_offset").GetInt32(), Is.GreaterThan(0),
                "the signed §59.15 predicted move rides the shot-1 event");
        }

        [Test]
        public async Task An_other_telescope_type_keeps_the_heuristic_direction() {
            // Identical drifted-rig physics, but the profile declares no optical design: side
            // classification is disabled, the heuristic guesses wrong, and the §59.11 reversal ladder
            // pays one extra shot — exactly the pre-§59.4 behavior.
            var rig = Build(realBest: StartPosition + 150, telescopeType: "other",
                calibration: SignedCalibration(),
                skySkew: position => position < StartPosition + 150 ? -0.4 : 0.4);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3), "heuristic wrong → one reversal shot");
            var shot1 = rig.Events.First(e => e.Type == WsEventCatalog.AutofocusShotComplete);
            Assert.That(shot1.Payload.GetProperty("direction_source").GetString(), Is.EqualTo("heuristic"));
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
        }

        [Test]
        public async Task A_confidently_wrong_classifier_is_caught_by_the_reversal_ladder() {
            // The stored calibration's sign convention is INVERTED relative to tonight's physics (a
            // changed optical train since calibration): the classifier reads the frame to the wrong arm
            // with full confidence. The §59.11 ladder must absorb it — shot 2 worse → reverse → improved
            // → success in 3 shots, no fallback. A lying classifier costs exactly what a wrong heuristic
            // costs; never more.
            var rig = Build(realBest: StartPosition + 150, telescopeType: "sct",
                calibration: SignedCalibration(belowSkew: 0.4, aboveSkew: -0.4),
                skySkew: position => position < StartPosition + 150 ? -0.4 : 0.4);
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(3));
            Assert.That(rig.Moves[^1], Is.GreaterThan(StartPosition), "the reversal landed on the real best's side");
            Assert.That(Types(rig.Events), Does.Not.Contain(WsEventCatalog.AutofocusFallbackClassic));
        }

        [Test]
        public async Task A_signatureless_calibration_on_an_obstructed_scope_stays_heuristic() {
            // An SCT whose calibration carries no side signature (pre-skew stored data, or a genuinely
            // symmetric sweep): nothing qualifies, the verdict is Unresolved, and the direction falls
            // back to the heuristic — never a guess dressed up as a classification.
            var rig = Build(realBest: CalibratedBest, telescopeType: "sct", calibration: Calibration());
            using var _ = rig.Service;

            var ok = await rig.Service.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(rig.CaptureCount(), Is.EqualTo(2), "heuristic direction is correct here — 2 shots");
            var shot1 = rig.Events.First(e => e.Type == WsEventCatalog.AutofocusShotComplete);
            Assert.That(shot1.Payload.GetProperty("direction_source").GetString(), Is.EqualTo("heuristic"));
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
