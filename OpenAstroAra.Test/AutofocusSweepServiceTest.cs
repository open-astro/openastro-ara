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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
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
    /// §59.8 — the live autofocus V-curve sweep, fully mocked (focuser + probe source + focus
    /// metric): single-direction probing, curve fit → move-to-best, fail-loud on junk probes and
    /// unusable fits, and the profile's restore-on-failure policy.
    /// </summary>
    [TestFixture]
    public class AutofocusSweepServiceTest {

        private const int StartPosition = 10_000;
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static AutofocusSettingsDto Settings(int steps = 4, int stepSize = 100, bool restore = true) => new(
            Method: "hfr_v_curve", Steps: steps, StepSize: stepSize, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: restore);

        private static Mock<IProfileStore> Profiles(AutofocusSettingsDto settings) {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetAutofocusSettings()).Returns(settings);
            return profiles;
        }

        /// <summary>A focuser whose position tracks MoveFocuser calls; records the move history.</summary>
        private static (Mock<IFocuserMediator> Mock, List<int> Moves) Focuser(bool connected = true, double temperature = double.NaN) {
            var moves = new List<int>();
            var position = StartPosition;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = connected, Position = position, Temperature = temperature });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            return (focuser, moves);
        }

        private static Mock<IAnalysisFrameSource> Frames() {
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UnixEpoch));
            return frames;
        }

        // Build a StarDetectionResult carrying a given HFR + star count (and optionally a per-star list for the
        // §59.10 collimation read), so the widened metric seam stays terse in the tests that don't exercise it.
        private static StarDetectionResult Result(double hfr, int stars, IReadOnlyList<DetectedStar>? starList = null) =>
            new() { AverageHFR = hfr, DetectedStars = stars, StarList = starList ?? Array.Empty<DetectedStar>() };

        /// <summary>A V-curve metric: HFR is minimal at <paramref name="bestPosition"/>. The service
        /// reads the CURRENT focuser position through the shared closure, so the metric returns the
        /// HFR "measured" at wherever the sweep just moved the focuser.</summary>
        private static Func<AnalysisFrame, CancellationToken, StarDetectionResult> VCurveMetric(
                Func<int> currentPosition, double bestPosition) =>
            (_, _) => {
                var delta = (currentPosition() - bestPosition) / 100.0;
                return Result(1.5 + 0.2 * delta * delta, 42);
            };

        private static (AutofocusSweepService Service, List<int> Moves, Func<int> Position) Build(
                AutofocusSettingsDto settings, double bestPosition, bool connected = true) {
            var (focuser, moves) = Focuser(connected);
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var svc = new AutofocusSweepService(
                Profiles(settings).Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, bestPosition));
            return (svc, moves, Current);
        }

        [Test]
        public async Task Sweep_probes_single_direction_and_lands_on_the_curve_minimum() {
            var settings = Settings(steps: 4, stepSize: 100);
            var (svc, moves, position) = Build(settings, bestPosition: StartPosition - 150);
            using var _ = svc;

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // The sweep first overshoots ABOVE the topmost probe so even the first sample is
            // approached downward (the top is otherwise reached by an upward move from start).
            Assert.That(moves[0], Is.EqualTo(StartPosition + 500));
            // 9 probe positions, outermost (start+400) first, strictly descending — one approach
            // direction so backlash biases every sample identically.
            var probes = moves.GetRange(1, 9);
            Assert.That(probes[0], Is.EqualTo(StartPosition + 400));
            Assert.That(probes, Is.Ordered.Descending);
            Assert.That(probes[^1], Is.EqualTo(StartPosition - 400));
            // Final position ≈ the metric's true minimum (parabola vertex recovered by the fit).
            Assert.That(position(), Is.EqualTo(StartPosition - 150).Within(30));
            // The final approach came from above (backlash-consistent overshoot step first).
            Assert.That(moves[^2], Is.GreaterThan(moves[^1]));
        }

        [Test]
        public async Task Disconnected_focuser_fails_without_touching_anything() {
            var (svc, moves, _) = Build(Settings(), StartPosition, connected: false);
            using var __ = svc;
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves, Is.Empty);
        }

        [Test]
        public async Task Invalid_sweep_configuration_fails() {
            var (svc, moves, _) = Build(Settings(steps: 0), StartPosition);
            using var __ = svc;
            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.False);
            Assert.That(moves, Is.Empty);
        }

        [Test]
        public async Task Starless_probe_fails_the_sweep_and_restores_start() {
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5, 0)); // no stars — clouds
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "restore-on-failure returns to the starting position");
        }

        [Test]
        public async Task Restore_policy_off_leaves_the_focuser_where_it_failed() {
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: false)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5, 0));
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves[^1], Is.Not.EqualTo(StartPosition), "no restore when the policy is off");
        }

        [Test]
        public async Task Flat_curve_yields_unusable_fit_and_restores() {
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(2.0, 42)); // identical HFR everywhere — no minimum to find
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves[^1], Is.EqualTo(StartPosition));
        }

        [Test]
        public async Task Probe_capture_failure_fails_the_sweep_and_restores() {
            var (focuser, moves) = Focuser();
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("camera fell over"));
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, frames.Object,
                metric: (_, _) => Result(1.5, 42));
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves[^1], Is.EqualTo(StartPosition));
        }

        [Test]
        public void Cancellation_propagates_and_restores() {
            var (focuser, moves) = Focuser();
            using var cts = new CancellationTokenSource();
            var probes = 0;
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    if (++probes == 3) cts.Cancel(); // abort mid-sweep
                    return Result(1.5, 42);
                });
            Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.RunAutofocusAsync(NoProgress, cts.Token));
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "a cancelled sweep must not strand focus at a probe position");
        }

        // ─── §59.10 collimation read on a completed sweep ───

        private static Mock<IAnalysisFrameSource> FramesSized(int w, int h) {
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[w * h], w, h, DateTimeOffset.UnixEpoch));
            return frames;
        }

        // A cluster of near-centre donut stars each carrying the same shadow-centroid offset (a coherent tilt).
        private static List<DetectedStar> DonutStars(int count, int width, int cx, int cy,
                double offsetX, double offsetY, double outer = 20.0) {
            var list = new List<DetectedStar>(count);
            for (int i = 0; i < count; i++) {
                int x = cx + ((i % 3) - 1) * 15;
                int y = cy + ((i / 3) - 1) * 15;
                list.Add(new DetectedStar {
                    Position = (y * width) + x,
                    DonutOuterDiameter = outer,
                    DonutInnerDiameter = 6.0,
                    DonutCentroidOffsetX = offsetX,
                    DonutCentroidOffsetY = offsetY,
                });
            }
            return list;
        }

        [Test]
        public async Task A_completed_sweep_logs_a_collimation_verdict_from_decentered_donut_stars() {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var logger = new RecordingLogger();
            // Every probe reports the same near-centre donut stars with a coherent +4 px shadow shift on a
            // 20 px donut → 20% of the diameter, well past the 15% "Significant" threshold.
            var donuts = DonutStars(8, width: 200, cx: 100, cy: 100, offsetX: 4.0, offsetY: 0.0, outer: 20.0);
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, FramesSized(200, 200).Object,
                logger: logger,
                metric: (_, _) => {
                    var delta = (Current() - (StartPosition - 150)) / 100.0;
                    return Result(1.5 + 0.2 * delta * delta, 42, donuts);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(logger.Messages, Has.Some.Contains("collimation").And.Contains("Significant"),
                "a completed sweep with decentered donut stars logs the §59.10 collimation verdict");
        }

        [Test]
        public async Task A_refractor_field_logs_no_collimation_verdict() {
            // Hole-less stars (no obstruction shadow) → the evaluator returns Insufficient → nothing logged.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var logger = new RecordingLogger();
            var flat = new List<DetectedStar> {
                new() { Position = (100 * 200) + 100, DonutInnerDiameter = 0, DonutOuterDiameter = 8.0 },
            };
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, FramesSized(200, 200).Object,
                logger: logger,
                metric: (_, _) => {
                    var delta = (Current() - (StartPosition - 150)) / 100.0;
                    return Result(1.5 + 0.2 * delta * delta, 42, flat);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(logger.Messages, Has.None.Contains("collimation"),
                "a refractor / hole-less field is Insufficient and logs no collimation verdict");
        }

        private static (Mock<IWsBroadcaster> Ws, List<(string Type, JsonElement Payload)> Events) CapturingWs() {
            var events = new List<(string, JsonElement)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns<string, JsonElement, CancellationToken>((t, p, _) => { events.Add((t, p.Clone())); return Task.CompletedTask; });
            return (ws, events);
        }

        private static (Mock<INotificationService> Svc, List<NotificationDto> Posted) CapturingNotifications() {
            var posted = new List<NotificationDto>();
            var svc = new Mock<INotificationService>();
            svc.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns<NotificationDto, CancellationToken>((d, _) => { posted.Add(d); return Task.CompletedTask; });
            return (svc, posted);
        }

        private static AutofocusSweepService SurfacingSvc(
                Mock<IWsBroadcaster> ws, Mock<INotificationService> notifications,
                IReadOnlyList<DetectedStar> stars) {
            var (focuser, moves) = Focuser();
            // The V-curve tracks this focuser's real move history so the sweep succeeds and reaches the verdict.
            return new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, FramesSized(200, 200).Object,
                metric: (_, _) => {
                    var delta = (moves.Count == 0 ? StartPosition : moves[^1]) - (StartPosition - 150);
                    return Result(1.5 + 0.2 * (delta / 100.0) * (delta / 100.0), 42, stars);
                },
                ws: ws.Object, notifications: notifications.Object);
        }

        [Test]
        public async Task A_significant_verdict_broadcasts_a_ws_event_and_notifies() {
            var (ws, events) = CapturingWs();
            var (notif, posted) = CapturingNotifications();
            var decentered = DonutStars(8, 200, 100, 100, offsetX: 4.0, offsetY: 0.0, outer: 20.0); // 20% → Significant
            using var svc = SurfacingSvc(ws, notif, decentered);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Type, Is.EqualTo(WsEventCatalog.AutofocusCollimationVerdict));
            Assert.That(events[0].Payload.GetProperty("severity").GetString(), Is.EqualTo("significant"));
            Assert.That(events[0].Payload.GetProperty("offset_percent").GetDouble(), Is.EqualTo(20.0).Within(0.5));
            Assert.That(events[0].Payload.GetProperty("stars_used").GetInt32(), Is.GreaterThan(0));
            Assert.That(posted, Has.Count.EqualTo(1), "a Significant verdict posts a user notification");
            Assert.That(posted[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(posted[0].Message, Does.Contain("collimate before continuing"));
        }

        [Test]
        public async Task A_good_verdict_broadcasts_but_does_not_notify() {
            var (ws, events) = CapturingWs();
            var (notif, posted) = CapturingNotifications();
            var nearlyConcentric = DonutStars(8, 200, 100, 100, offsetX: 0.6, offsetY: 0.0, outer: 20.0); // 3% → Good
            using var svc = SurfacingSvc(ws, notif, nearlyConcentric);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(events, Has.Count.EqualTo(1), "a Good verdict still broadcasts (the client decides what to show)");
            Assert.That(events[0].Payload.GetProperty("severity").GetString(), Is.EqualTo("good"));
            Assert.That(posted, Is.Empty, "a Good verdict must not nag the user with a notification");
        }

        [Test]
        public async Task A_refractor_field_neither_broadcasts_nor_notifies() {
            var (ws, events) = CapturingWs();
            var (notif, posted) = CapturingNotifications();
            var holeLess = new List<DetectedStar> {
                new() { Position = (100 * 200) + 100, DonutInnerDiameter = 0, DonutOuterDiameter = 8.0 },
            };
            using var svc = SurfacingSvc(ws, notif, holeLess);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(events, Is.Empty, "an Insufficient (refractor) read broadcasts nothing");
            Assert.That(posted, Is.Empty);
        }

        [Test]
        public async Task The_most_defocused_probe_drives_the_verdict() {
            // Only the most-defocused probe (the top, StartPosition+400, furthest from best StartPosition-150)
            // carries decentered donuts; every other probe is concentric. The verdict must reflect the former.
            var (focuser, moves) = Focuser();
            var logger = new RecordingLogger();
            var decentered = DonutStars(8, 200, 100, 100, offsetX: 4.0, offsetY: 0.0, outer: 20.0);
            var concentric = DonutStars(8, 200, 100, 100, offsetX: 0.0, offsetY: 0.0, outer: 20.0);
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, FramesSized(200, 200).Object,
                logger: logger,
                metric: (_, _) => {
                    var pos = moves.Count == 0 ? StartPosition : moves[^1];
                    var delta = (pos - (StartPosition - 150)) / 100.0;
                    var stars = pos == StartPosition + 400 ? decentered : concentric;
                    return Result(1.5 + 0.2 * delta * delta, 42, stars);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(logger.Messages, Has.Some.Contains("Significant"),
                "the verdict must come from the most-defocused probe, not the near-focus concentric ones");
        }

        // ---- §59.2 slice B — a successful sweep records the Smart Focus calibration ----

        /// <summary>Stars whose per-star metrics all reflect the given HFR, so the probe's
        /// <see cref="FocusFeatureExtractor"/> medians trace the same V-curve the fit sees.</summary>
        private static List<DetectedStar> FeatureStars(int count, double hfr) {
            var stars = new List<DetectedStar>(count);
            for (int i = 0; i < count; i++) {
                stars.Add(new DetectedStar { HFR = hfr, FWHM = hfr * 2.0, Roundness = 0.9, PeakToBackground = 8.0 });
            }
            return stars;
        }

        /// <summary>A sweep rig over a REAL <see cref="InMemoryProfileStore"/> (so PutFocusCalibration
        /// actually lands) whose metric carries feature-bearing star lists along the V-curve.</summary>
        private static (AutofocusSweepService Service, InMemoryProfileStore Store) CalibrationRig(
                double bestPosition, double focuserTemperature = 12.5, string? filter = "Ha",
                IReadOnlyList<DetectedStar>? fixedStarList = null) {
            var settings = Settings(steps: 4, stepSize: 100);
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(settings);

            var (focuser, moves) = Focuser(temperature: focuserTemperature);
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];

            var wheel = new Mock<IFilterWheelMediator>();
            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                Connected = true,
                SelectedFilter = filter is null ? null! : new FilterInfo(filter, 0, 0),
            });

            var svc = new AutofocusSweepService(
                store, focuser.Object, Frames().Object,
                filterWheel: wheel.Object,
                metric: (_, _) => {
                    var delta = (Current() - bestPosition) / 100.0;
                    var hfr = 1.5 + 0.2 * delta * delta;
                    return Result(hfr, 42, fixedStarList ?? FeatureStars(12, hfr));
                });
            return (svc, store);
        }

        [Test]
        public async Task A_successful_sweep_records_a_calibration_that_rebuilds_a_usable_map() {
            var (svc, store) = CalibrationRig(bestPosition: StartPosition - 150);
            using var _ = svc;

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            var cal = store.GetFocusCalibration();
            Assert.That(cal, Is.Not.Null, "every successful sweep refreshes the calibration");
            Assert.That(cal!.Samples, Has.Count.EqualTo(AutofocusSweepService.ProbeCount(Settings(steps: 4, stepSize: 100))));
            Assert.That(cal.FocuserTemperatureC, Is.EqualTo(12.5));
            Assert.That(cal.Filter, Is.EqualTo("Ha"));
            Assert.That(cal.CalibratedUtc, Is.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5)));

            // The stored DTO samples must survive the round-trip back into a usable inverse map — the
            // whole point of recording them (the slice-C one-frame runner's load path).
            var map = FocusInverseMap.Build(cal.Samples.Select(s => s.ToSample()).ToList());
            Assert.That(map, Is.Not.Null);
            Assert.That(map!.BestFocusOffset, Is.EqualTo(StartPosition - 150).Within(30));
        }

        [Test]
        public async Task A_failed_sweep_records_no_calibration() {
            // A flat AverageHFR everywhere: the fit finds no minimum, the sweep fails, and the
            // calibration write (post-success bookkeeping) must never run.
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings(steps: 4, stepSize: 100));
            using var svc = new AutofocusSweepService(
                store, Focuser().Mock.Object, Frames().Object,
                metric: (_, _) => Result(2.0, 42, FeatureStars(12, 2.0)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False, "a flat curve has no minimum");
            Assert.That(store.GetFocusCalibration(), Is.Null, "a failed sweep must not write a calibration");
        }

        [Test]
        public async Task Unusable_feature_samples_keep_the_previous_calibration() {
            // The probes' AverageHFR traces a fittable V-curve (the sweep succeeds), but every star LIST is
            // empty, so the feature medians carry no signal and can't rebuild an inverse map. The previously
            // stored calibration must survive — junk samples never clobber a good one.
            var (svc, store) = CalibrationRig(bestPosition: StartPosition - 150,
                fixedStarList: Array.Empty<DetectedStar>());
            using var _ = svc;
            var previous = new FocusCalibrationDto(
                Samples: new[] { new FocusCalibrationSampleDto(9_000, 30, 2.0, 4.0, 0.9, 8.0, 0, 0, 0, 0) },
                CalibratedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                FocuserTemperatureC: 5.0,
                Filter: "L");
            store.PutFocusCalibration(previous);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, "the sweep itself succeeds on AverageHFR");
            Assert.That(store.GetFocusCalibration(), Is.EqualTo(previous),
                "unusable feature samples must not overwrite the stored calibration");
        }

        [Test]
        public async Task A_focuser_without_a_temperature_probe_stores_a_null_temperature() {
            var (svc, store) = CalibrationRig(bestPosition: StartPosition - 150,
                focuserTemperature: double.NaN, filter: null);
            using var _ = svc;

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            var cal = store.GetFocusCalibration();
            Assert.That(cal, Is.Not.Null);
            Assert.That(cal!.FocuserTemperatureC, Is.Null, "NaN is unrepresentable in JSON — stored as null");
            Assert.That(cal.Filter, Is.Null);
        }

        // Captures formatted log messages so the collimation-verdict log line can be asserted.
        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<AutofocusSweepService> {
            public List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Messages.Add(formatter(state, exception));
        }
    }
}
