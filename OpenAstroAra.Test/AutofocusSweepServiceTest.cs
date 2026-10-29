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

        private static AutofocusSettingsDto Settings(int steps = 4, int stepSize = 100, bool restore = true, bool stepSizeAuto = true) => new(
            Method: "hfr_v_curve", Steps: steps, StepSize: stepSize, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: restore,
            StepSizeAuto: stepSizeAuto);

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
        public async Task Coarse_search_finds_focus_far_from_the_start() {
            // Focus 3000 steps above the start: every fine probe around the start is unmeasurable, so only
            // the coarse pass (binned measurement, readable at any defocus) can find the way there.
            const int best = StartPosition + 3000;
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var delta = (Current() - best) / 100.0;
                    return Math.Abs(Current() - best) > 600 ? Result(0, 0) : Result(1.5 + 0.2 * delta * delta, 42);
                },
                coarseMetric: (_, _) => 2.0 + Math.Abs(Current() - best) / 50.0);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(Current(), Is.EqualTo(best).Within(30));
        }

        [Test]
        public async Task Coarse_search_that_never_brackets_focus_fails_after_its_probe_budget_and_restores() {
            // HFR keeps improving upward forever: the walk must stop at CoarseMaxProbes, fail, and
            // restore, without ever starting the fine sweep.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var fullResReads = 0;
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => { fullResReads++; return Result(1.5, 42); },
                coarseMetric: (_, _) => 1e6 - Current());

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            // #1445 — each coarse probe logs its full-resolution reading beside the binned one; no fine probe ran.
            Assert.That(fullResReads, Is.EqualTo(AutofocusSweepService.CoarseMaxProbes));
            var coarseMoves = moves.GetRange(0, moves.Count - 1);
            Assert.That(coarseMoves, Has.Count.EqualTo(AutofocusSweepService.CoarseMaxProbes));
            Assert.That(coarseMoves, Is.Ordered.Ascending);
            Assert.That(coarseMoves.Zip(coarseMoves.Skip(1), (a, b) => b - a).Max(),
                Is.EqualTo(400 * AutofocusSweepService.CoarseMaxStepMultiplier), "the step stops growing at the cap");
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "restore-on-failure returns to the starting position");
        }

        private static readonly int[] TravelStopMoves = { 500, 900, 100, 900 };

        [Test]
        public async Task Coarse_search_treats_the_travel_stop_at_zero_as_the_bracket() {
            // Start at 500 with HFR improving toward 0: the walk reaches 100 and the next doubled step
            // would pass 0, so 0 is the far side of the bracket. The fine sweep around 100 would probe
            // down to -300, so its centre is lifted to 400 (lowest probe 0) and no move goes below 0.
            var position = 500;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5 + 0.2 * Math.Pow((position - 100) / 100.0, 2), 42),
                coarseMetric: (_, _) => 1000.0 + position);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // Coarse: start, +400 (worse), -400 (better, at 100); refinement re-uses 500 and skips -300.
            // Then the fine overshoot above the clamped centre: 400 + 500.
            Assert.That(moves.GetRange(0, 4), Is.EqualTo(TravelStopMoves));
            Assert.That(moves.Min(), Is.GreaterThanOrEqualTo(0));
            Assert.That(position, Is.EqualTo(100).Within(30));
        }

        [Test]
        public async Task Sweep_stays_inside_the_focusers_reported_travel() {
            // Travel 0..12000, focus at 11700 with HFR improving upward from the start: the coarse walk
            // stops at the top of travel and the fine sweep (and its overshoot) must not pass 12000.
            const int maxPosition = 12_000;
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, 11_700),
                coarseMetric: (_, _) => 1e6 - Current(),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, maxPosition)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves.Max(), Is.LessThanOrEqualTo(maxPosition));
            Assert.That(Current(), Is.EqualTo(11_700).Within(30));
        }

        [Test]
        public async Task The_sweep_overshoots_by_the_measured_backlash_and_stays_inside_the_travel() {
            // §59.7 — a calibration that measured 150 steps of backlash: the sweep's entry and its final landing
            // come down from 1.5× that (225) above, and the centre clamp leaves room for it under the travel's
            // top (the overshoot used to be one step, and the clamp reserved one step).
            const int maxPosition = 12_000;
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var profiles = Profiles(Settings(steps: 4, stepSize: 100));
            // No samples: the Smart path has no map to run on, so the Classic sweep runs with this backlash.
            profiles.Setup(p => p.GetFocusCalibration()).Returns(new FocusCalibrationDto([], DateTimeOffset.UnixEpoch, null, null, BacklashUpSteps: 150));
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, 11_700),
                coarseMetric: (_, _) => 1e6 - Current(),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, maxPosition)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves.Max(), Is.LessThanOrEqualTo(maxPosition), "the larger overshoot stays inside the travel");
            // The centre clamps to 12 000 − 4·100 − 225 = 11 375, so the probes run 11 775 down to 10 975.
            var top = IndexOfRun(moves, 11_775, 11_675);
            Assert.That(top, Is.GreaterThan(0), "the fine sweep ran from 11 775 down");
            Assert.That(moves[top - 1], Is.EqualTo(11_775 + 225), "entered from 225 above the top probe");
            var bottom = moves.IndexOf(10_975, top);
            Assert.That(bottom, Is.GreaterThan(top));
            Assert.That(moves[bottom + 1] - moves[bottom + 2], Is.EqualTo(225), "the final landing comes down from 225 above best");
            Assert.That(Current(), Is.EqualTo(11_700).Within(30));
        }

        [Test]
        public async Task On_a_travel_too_short_for_the_sweep_the_entry_overshoot_stops_at_the_top() {
            // Travel 0..1000 cannot hold ±4·100 plus a 225-step overshoot: the centre clamps to 400 (the bottom
            // probe at 0), the top probe sits at 800, and the entry must stop at 1000, not 1025.
            const int maxPosition = 1_000;
            var moves = new List<int>();
            var position = 500; // inside the travel
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            int Current() => position;
            var profiles = Profiles(Settings(steps: 4, stepSize: 100));
            profiles.Setup(p => p.GetFocusCalibration()).Returns(new FocusCalibrationDto([], DateTimeOffset.UnixEpoch, null, null, BacklashUpSteps: 150));
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, 450),
                coarseMetric: (_, _) => Current(),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, maxPosition)));

            await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            var top = IndexOfRun(moves, 800, 700);
            Assert.That(top, Is.GreaterThan(0), "the fine sweep ran from 800 down");
            Assert.That(moves[top - 1], Is.EqualTo(maxPosition), "entered from the top of the travel, not 800 + 225");
        }

        [Test]
        public async Task A_sweep_wider_than_the_travel_fails_without_moving_past_it() {
            // ±4·100 spans 800 steps; a 0..500 travel cannot hold it, so the sweep says so instead of commanding
            // probes above MaxStep.
            var moves = new List<int>();
            var position = 250;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                tracker: tracker,
                metric: VCurveMetric(() => position, 250),
                coarseMetric: (_, _) => position,
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, 500)));

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.False);

            Assert.That(moves.DefaultIfEmpty(position).Max(), Is.LessThanOrEqualTo(500));
            Assert.That(tracker.Snapshot().Reason, Does.Contain("wider than the focuser's travel"));
            Assert.That(tracker.Snapshot().Reason, Does.Contain("Automatic step size"), "an automatic step says how to override it");
        }

        private static int IndexOfRun(List<int> moves, int first, int second) {
            for (int i = 0; i + 1 < moves.Count; i++) {
                if (moves[i] == first && moves[i + 1] == second) {
                    return i;
                }
            }
            return -1;
        }

        [TestCase(true, 30_000, 0, 30_000)]
        [TestCase(false, 30_000, int.MinValue, int.MaxValue)] // relative focuser: no travel stop
        [TestCase(true, 0, null, null)]       // range not reported yet
        public async Task FocuserTravel_is_the_absolute_focusers_reported_range(bool absolute, int maxPosition, int? min, int? max) {
            var focusers = new Mock<IFocuserService>();
            focusers.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new FocuserDto(
                "f", "Focuser", EquipmentConnectionState.Connected,
                new FocuserCapabilitiesDto(absolute ? 0 : -maxPosition, maxPosition, 3.76, false, absolute),
                new FocuserStateDto("idle", 100, null, false)));
            var travel = await AutofocusSweepService.FocuserTravelAsync(focusers.Object, CancellationToken.None);
            Assert.That(travel, Is.EqualTo(min is null ? null : (min.Value, max!.Value)));
        }

        [TestCase(3.76, 3.76)]
        [TestCase(0.0, null)] // the EAF through AlpacaBridge reports 0: no µm/step to size a CFZ from
        [TestCase(-1.0, null)]
        public async Task FocuserStepUm_is_the_focusers_reported_step_size_or_null(double reported, double? expected) {
            var focusers = new Mock<IFocuserService>();
            focusers.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new FocuserDto(
                "f", "Focuser", EquipmentConnectionState.Connected,
                new FocuserCapabilitiesDto(0, 30_000, reported, false, true),
                new FocuserStateDto("idle", 100, null, false)));
            Assert.That(await AutofocusSweepService.FocuserStepUmAsync(focusers.Object, CancellationToken.None), Is.EqualTo(expected));
        }

        [Test]
        public async Task FocuserStepUm_is_null_without_a_focuser_or_its_capabilities() {
            var none = new Mock<IFocuserService>();
            none.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((FocuserDto?)null);
            Assert.That(await AutofocusSweepService.FocuserStepUmAsync(none.Object, CancellationToken.None), Is.Null);
            var uncached = new Mock<IFocuserService>();
            uncached.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new FocuserDto(
                "f", "Focuser", EquipmentConnectionState.Connecting, null, new FocuserStateDto("idle", null, null, false)));
            Assert.That(await AutofocusSweepService.FocuserStepUmAsync(uncached.Object, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task FocuserTravel_is_null_with_no_focuser() {
            var focusers = new Mock<IFocuserService>();
            focusers.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((FocuserDto?)null);
            Assert.That(await AutofocusSweepService.FocuserTravelAsync(focusers.Object, CancellationToken.None), Is.Null);
        }

        [TestCase(100, 400)]       // lowest probe would be below 0
        [TestCase(5_000, 5_000)]   // already inside
        [TestCase(11_900, 11_500)] // overshoot above the top probe would pass 12000
        public void ClampSweepCentre_keeps_every_probe_inside_the_travel(int centre, int expected) =>
            Assert.That(AutofocusSweepService.ClampSweepCentre(centre, Settings(steps: 4, stepSize: 100), (0, 12_000)),
                Is.EqualTo(expected));

        [TestCase(100, 400)]   // travel shorter than one sweep: only the bottom probe is kept at 0
        [TestCase(700, 700)]
        public void ClampSweepCentre_on_a_travel_shorter_than_the_sweep_keeps_the_bottom_probe_in(int centre, int expected) =>
            Assert.That(AutofocusSweepService.ClampSweepCentre(centre, Settings(steps: 4, stepSize: 100), (0, 600)),
                Is.EqualTo(expected));

        [Test]
        public void ClampSweepCentre_leaves_an_unbounded_travel_alone() =>
            Assert.That(AutofocusSweepService.ClampSweepCentre(-250, Settings(steps: 4, stepSize: 100), (int.MinValue, int.MaxValue)),
                Is.EqualTo(-250));

        [Test]
        public async Task Coarse_search_keeps_the_start_when_it_already_brackets_focus() {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, StartPosition - 150),
                coarseMetric: (_, _) => 2.0 + Math.Abs(Current() - (StartPosition - 150)) / 50.0);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            // Three coarse probes (start, +400, −400), then the overshoot + nine fine probes around the start.
            Assert.That(moves.GetRange(0, 3), Is.EqualTo(new[] { StartPosition, StartPosition + 400, StartPosition - 400 }));
            Assert.That(moves[3], Is.EqualTo(StartPosition + 500));
            Assert.That(Current(), Is.EqualTo(StartPosition - 150).Within(30));
        }

        [Test]
        public async Task Coarse_search_with_no_measurable_stars_fails_and_restores() {
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5, 42),
                coarseMetric: (_, _) => double.PositiveInfinity);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(moves, Has.Count.EqualTo(4), "three coarse probes, then the restore");
            Assert.That(moves[^1], Is.EqualTo(StartPosition));
        }

        [Test]
        public async Task Edge_minimum_re_centres_and_sweeps_again() {
            // Focus 550 above the start: the first ±400 sweep falls monotonically toward its top edge.
            var (svc, moves, position) = Build(Settings(steps: 4, stepSize: 100), bestPosition: StartPosition + 550);
            using var _ = svc;

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves, Has.Member(StartPosition + 900), "the re-sweep overshoots above the new top (edge + 400 + 100)");
            Assert.That(position(), Is.EqualTo(StartPosition + 550).Within(30));
        }

        [Test]
        public async Task Unmeasurable_edge_probes_are_skipped_not_fatal() {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var delta = (Current() - StartPosition) / 100.0;
                    return Math.Abs(Current() - StartPosition) >= 400 ? Result(0, 0) : Result(1.5 + 0.2 * delta * delta, 42);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(Current(), Is.EqualTo(StartPosition).Within(30));
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
        public async Task Too_few_measurable_probes_fails_the_sweep_and_restores_start() {
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5, 0)); // no stars — clouds
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "restore-on-failure returns to the starting position");
        }

        // 9 probes need ceil(9/2) = 5 measurable: one short of that fails, exactly that many fits.
        [TestCase(-100, 200, false)] // 4 of 9 probes see stars
        [TestCase(-200, 200, true)]  // 5 of 9
        public async Task Sweep_needs_half_its_probes_measurable(int lowOffset, int highOffset, bool expected) {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var offset = Current() - StartPosition;
                    return offset >= lowOffset && offset <= highOffset
                        ? Result(1.5 + 0.2 * (offset / 100.0) * (offset / 100.0), 42)
                        : Result(1.5, 0);
                });
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.EqualTo(expected));
            if (!expected) {
                Assert.That(moves[^1], Is.EqualTo(StartPosition), "restore-on-failure returns to the starting position");
            }
        }

        [Test]
        public async Task A_throw_during_run_setup_still_releases_the_sweep_gate() {
            // The setup between the gate and the sweep (settings, focuser info, the run record) must
            // not leak the gate: the next run would wait on it forever.
            var (focuser, _) = Focuser();
            var profiles = new Mock<IProfileStore>();
            profiles.SetupSequence(p => p.GetAutofocusSettings())
                .Throws(new InvalidOperationException("profile store unavailable"))
                .Returns(Settings());
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object, metric: (_, _) => Result(2.0, 42));

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAutofocusAsync(NoProgress, CancellationToken.None));
            var second = svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(second),
                "the second run must not hang on a leaked gate");
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
        public async Task Cancellation_propagates_and_restores() {
            var (focuser, moves) = Focuser();
            using var cts = new CancellationTokenSource();
            var probes = 0;
            using var svc = new AutofocusSweepService(
                Profiles(Settings(restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    if (++probes == 3) cts.Cancel(); // abort mid-sweep
                    return Result(1.5, 42);
                });
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.RunAutofocusAsync(NoProgress, cts.Token));
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "a cancelled sweep must not strand focus at a probe position");
        }

        // ─── coarse search: the production metric ───

        [Test]
        public void SoftwareBin_averages_each_tile_and_drops_partial_edges() {
            var pixels = Enumerable.Repeat((ushort)1000, 17 * 9).ToArray();
            pixels[0] = 64000; // one hot pixel in the first 8x8 tile
            var (binned, width, height) = AutofocusSweepService.SoftwareBin(pixels, 17, 9, 8);
            Assert.That((width, height), Is.EqualTo((2, 1)), "the partial 9th column / row tiles are dropped");
            Assert.That(binned[0], Is.EqualTo((63 * 1000 + 64000) / 64));
            Assert.That(binned[1], Is.EqualTo(1000));
        }

        [Test]
        public void SoftwareBin_factor_one_is_a_copy() {
            var pixels = new ushort[] { 1, 2, 3, 4 };
            var (binned, width, height) = AutofocusSweepService.SoftwareBin(pixels, 2, 2, 1);
            Assert.That((width, height), Is.EqualTo((2, 2)));
            Assert.That(binned, Is.EqualTo(pixels));
        }

        /// <summary>Gaussian stars of the given sigma on a flat 1000-ADU sky.</summary>
        private static AnalysisFrame StarFrame(int size, double sigma, int spacing) {
            var pixels = new ushort[size * size];
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    var v = 1000.0;
                    var cx = (x / spacing) * spacing + spacing / 2;
                    var cy = (y / spacing) * spacing + spacing / 2;
                    var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    v += 30000.0 * Math.Exp(-r2 / (2 * sigma * sigma));
                    pixels[y * size + x] = (ushort)Math.Min(65535, v);
                }
            }
            return new AnalysisFrame(pixels, size, size, DateTimeOffset.UnixEpoch);
        }

        [Test]
        public void DefaultCoarseMetric_reports_native_pixel_hfr_from_the_binned_frame() {
            // 1024 px wide: binned 8x to 128 px. A sigma-12 star has a native HFR near 14 px; the
            // binned HFR (~1.8 px) must be scaled back up by the bin factor.
            var hfr = AutofocusSweepService.DefaultCoarseMetric(StarFrame(1024, 12.0, 128), CancellationToken.None);
            Assert.That(hfr, Is.InRange(8.0, 24.0));
        }

        [Test]
        public void DefaultCoarseMetric_measures_small_frames_unbinned() {
            // Under 32 binned px a side the frame is measured at full resolution (factor 1), so a
            // sigma-2 star reads ~2.4 px (binning it anyway reads 4).
            var hfr = AutofocusSweepService.DefaultCoarseMetric(StarFrame(200, 2.0, 40), CancellationToken.None);
            Assert.That(hfr, Is.InRange(1.5, 3.5));
        }

        [Test]
        public void DefaultCoarseMetric_is_infinite_without_stars() {
            var blank = new AnalysisFrame(Enumerable.Repeat((ushort)1000, 512 * 512).ToArray(), 512, 512, DateTimeOffset.UnixEpoch);
            Assert.That(AutofocusSweepService.DefaultCoarseMetric(blank, CancellationToken.None), Is.EqualTo(double.PositiveInfinity));
        }

        [Test]
        public async Task Default_construction_runs_the_coarse_search_first() {
            // No metric injected = production wiring: the coarse pass probes start, start + Steps*StepSize
            // and start - Steps*StepSize before any fine-sweep overshoot. The 4x4 blank probe frame has
            // no stars, so the coarse pass gives up and the run restores.
            var (focuser, moves) = Focuser();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, restore: true)).Object, focuser.Object, Frames().Object);
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(ok, Is.False);
            Assert.That(moves.Take(3), Is.EqualTo(new[] { StartPosition, StartPosition + 400, StartPosition - 400 }));
            Assert.That(moves[^1], Is.EqualTo(StartPosition));
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
            var verdicts = events.Where(e => e.Type == WsEventCatalog.AutofocusCollimationVerdict).ToList();
            Assert.That(verdicts, Has.Count.EqualTo(1));
            Assert.That(verdicts[0].Payload.GetProperty("severity").GetString(), Is.EqualTo("significant"));
            Assert.That(verdicts[0].Payload.GetProperty("offset_percent").GetDouble(), Is.EqualTo(20.0).Within(0.5));
            Assert.That(verdicts[0].Payload.GetProperty("stars_used").GetInt32(), Is.GreaterThan(0));
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
            var verdicts = events.Where(e => e.Type == WsEventCatalog.AutofocusCollimationVerdict).ToList();
            Assert.That(verdicts, Has.Count.EqualTo(1), "a Good verdict still broadcasts (the client decides what to show)");
            Assert.That(verdicts[0].Payload.GetProperty("severity").GetString(), Is.EqualTo("good"));
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
            Assert.That(events.Where(e => e.Type == WsEventCatalog.AutofocusCollimationVerdict), Is.Empty,
                "an Insufficient (refractor) read broadcasts no verdict");
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
            var (svc, store, _) = CalibrationRigWithMoves(bestPosition, focuserTemperature, filter, fixedStarList);
            return (svc, store);
        }

        private static (AutofocusSweepService Service, InMemoryProfileStore Store, List<int> Moves) CalibrationRigWithMoves(
                double bestPosition, double focuserTemperature = 12.5, string? filter = "Ha",
                IReadOnlyList<DetectedStar>? fixedStarList = null, AutofocusSettingsDto? settings = null) {
            settings ??= Settings(steps: 4, stepSize: 100);
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
            return (svc, store, moves);
        }

        // ─── §59.8 automatic step size ───

        // The test V: HFR = 1.5 + 0.2·(Δ/100)² doubles (3.0) at Δ = 100·√7.5 ≈ 273.9 steps.
        private const double TestCurveHalfWidth = 273.86;

        [Test]
        public async Task A_completed_sweep_stores_the_curve_half_width_with_the_calibration() {
            var (svc, store) = CalibrationRig(bestPosition: StartPosition - 150);
            using var _ = svc;

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(store.GetFocusCalibration()!.CurveHalfWidthSteps, Is.EqualTo(TestCurveHalfWidth).Within(1.0),
                "the offset at which the fitted HFR doubles, read from the model, not the probes");
        }

        [Test]
        public async Task An_automatic_sweep_is_sized_from_the_stored_half_width() {
            // A stored width of 120 with 4 steps a side → outer probes at 1.5 × 120 = 180 → step 45 (not the
            // profile's 100). One junk sample so the inverse map can't build and Smart Focus stands aside.
            var (svc, store, moves) = CalibrationRigWithMoves(bestPosition: StartPosition - 20);
            using var _ = svc;
            store.PutFocusCalibration(new FocusCalibrationDto(
                Samples: new[] { new FocusCalibrationSampleDto(9_000, 30, 2.0, 4.0, 0.9, 8.0, 0, 0, 0, 0) },
                CalibratedUtc: DateTimeOffset.UtcNow, FocuserTemperatureC: null, Filter: null,
                CurveHalfWidthSteps: 120));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves[0], Is.EqualTo(StartPosition + 180 + 45), "overshoot one (auto) step above the top probe");
            var probes = moves.GetRange(1, 9);
            Assert.That(probes[0], Is.EqualTo(StartPosition + 180));
            Assert.That(probes.Zip(probes.Skip(1), (a, b) => a - b), Has.All.EqualTo(45));
        }

        [Test]
        public async Task A_manual_step_size_ignores_the_stored_half_width() {
            var (svc, store, moves) = CalibrationRigWithMoves(bestPosition: StartPosition - 20,
                settings: Settings(steps: 4, stepSize: 100, stepSizeAuto: false));
            using var _ = svc;
            store.PutFocusCalibration(new FocusCalibrationDto(
                Samples: new[] { new FocusCalibrationSampleDto(9_000, 30, 2.0, 4.0, 0.9, 8.0, 0, 0, 0, 0) },
                CalibratedUtc: DateTimeOffset.UtcNow, FocuserTemperatureC: null, Filter: null,
                CurveHalfWidthSteps: 120));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves[0], Is.EqualTo(StartPosition + 500));
            Assert.That(moves.GetRange(1, 9).Zip(moves.GetRange(2, 8), (a, b) => a - b), Has.All.EqualTo(100));
        }

        [Test]
        public async Task The_run_record_carries_each_kept_probes_rim_core_asymmetry() {
            // #1139 — a kept fine probe records its median rim-vs-core asymmetry (−0.3 below best, +0.3 above on
            // this sky); a probe dropped for too few stars records none.
            var settings = Settings(steps: 4, stepSize: 100);
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var best = StartPosition - 150;
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                Profiles(settings).Object, focuser.Object, Frames().Object, tracker: tracker,
                metric: (_, _) => {
                    var position = Current();
                    var delta = (position - best) / 100.0;
                    var hfr = 1.5 + 0.2 * delta * delta;
                    // The top probe sees only 2 stars and is dropped.
                    var count = position == StartPosition + 400 ? 2 : 42;
                    var asymmetry = position < best ? -0.3 : 0.3;
                    var stars = new List<DetectedStar>();
                    for (int i = 0; i < count; i++) {
                        stars.Add(new DetectedStar { HFR = hfr, FWHM = hfr * 2, Roundness = 0.9, PeakToBackground = 8, RimCoreAsymmetry = asymmetry });
                    }
                    return Result(hfr, count, stars);
                });

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            var fine = tracker.Snapshot().Probes.Where(p => p.Phase == "fine").ToList();
            Assert.Multiple(() => {
                Assert.That(fine.Single(p => p.Position == StartPosition + 400).Asymmetry, Is.Null, "the dropped probe");
                Assert.That(fine.Where(p => p.Kept && p.Position < best).Select(p => p.Asymmetry), Is.All.EqualTo(-0.3));
                Assert.That(fine.Where(p => p.Kept && p.Position > best).Select(p => p.Asymmetry), Is.All.EqualTo(0.3));
            });
        }

        [Test]
        public void The_tracker_rounds_a_probes_asymmetry_and_drops_a_non_finite_one() {
            var tracker = new AutofocusRunTracker();
            Assert.Multiple(() => {
                Assert.That(tracker.AddProbe("fine", 100, 2.0, 40, true, 0.123456).Asymmetry, Is.EqualTo(0.123));
                Assert.That(tracker.AddProbe("fine", 200, 2.0, 40, true, double.NaN).Asymmetry, Is.Null);
                Assert.That(tracker.AddProbe("coarse", 300, 2.0, 0, true).Asymmetry, Is.Null);
            });
        }

        [Test]
        public async Task Without_a_width_or_a_focuser_step_size_the_stored_step_size_is_the_wide_first_sweep() {
            // Moq profiles: no calibration, no optics; the EAF reports no µm/step → "default" = the stored 100.
            var settings = Settings(steps: 4, stepSize: 100);
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                Profiles(settings).Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, StartPosition - 150), tracker: tracker,
                focuserStepUm: _ => Task.FromResult<double?>(null));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves[0], Is.EqualTo(StartPosition + 500));
            var snap = tracker.Snapshot();
            Assert.That(snap.StepSize, Is.EqualTo(100));
            Assert.That(snap.StepSizeSource, Is.EqualTo("default"));
        }

        [Test]
        public async Task A_reported_focuser_step_size_seeds_the_first_sweep_from_the_CFZ() {
            // f/5 optics (530/106) + 1.2 µm/step → CFZ 55 µm → half = 27.5 µm → 23 steps (the wizard's own seed).
            var settings = Settings(steps: 4, stepSize: 100);
            var profiles = Profiles(settings);
            profiles.Setup(p => p.GetOpticsSettings()).Returns(new OpticsSettingsDto(
                FocalLengthMm: 530, ReducerFactor: 1.0, SensorWidthPx: 4000, SensorHeightPx: 3000, PixelSizeUm: 3.76, ApertureMm: 106));
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, StartPosition - 20), tracker: tracker,
                focuserStepUm: _ => Task.FromResult<double?>(1.2));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves[0], Is.EqualTo(StartPosition + 4 * 23 + 23));
            Assert.That(tracker.Snapshot().StepSize, Is.EqualTo(23));
            Assert.That(tracker.Snapshot().StepSizeSource, Is.EqualTo("cfz"));
        }

        [Test]
        public void ResolveStepSize_prefers_measured_then_cfz_then_the_stored_value() {
            var settings = Settings(steps: 7, stepSize: 50);
            var optics = new OpticsSettingsDto(FocalLengthMm: 250, ReducerFactor: 1.0, SensorWidthPx: 1, SensorHeightPx: 1, PixelSizeUm: 3.76, ApertureMm: 51);
            var measured = new FocusCalibrationDto(Array.Empty<FocusCalibrationSampleDto>(), DateTimeOffset.UtcNow, null, null, CurveHalfWidthSteps: 100);

            // The RedCat night: HFR doubles 100 steps out, 7 steps a side → 1.5 × 100 / 7 ≈ 21 instead of 50.
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, measured, 1.0, optics), Is.EqualTo((21, "measured")));
            // f/4.9, 1 µm/step → CFZ 52.8 µm → 26 steps.
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, null, 1.0, optics), Is.EqualTo((26, "cfz")));
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, null, 0.0, optics), Is.EqualTo((50, "default")), "a 0 µm/step report is no report");
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, null, 1.0, null), Is.EqualTo((50, "default")));
            Assert.That(AutofocusSweepService.ResolveStepSize(settings with { StepSizeAuto = false }, measured, 1.0, optics), Is.EqualTo((50, "manual")));
            // A pre-§59.8 calibration (no width) falls through to the CFZ seed.
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, measured with { CurveHalfWidthSteps = null }, 1.0, optics), Is.EqualTo((26, "cfz")));
        }

        [Test]
        public void ResolveStepSize_clamps_degenerate_widths_and_trains() {
            var settings = Settings(steps: 4, stepSize: 50);
            var tiny = new FocusCalibrationDto(Array.Empty<FocusCalibrationSampleDto>(), DateTimeOffset.UtcNow, null, null, CurveHalfWidthSteps: 2);
            var huge = new FocusCalibrationDto(Array.Empty<FocusCalibrationSampleDto>(), DateTimeOffset.UtcNow, null, null, CurveHalfWidthSteps: 1e6);
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, tiny, null, null).StepSize, Is.EqualTo(AutofocusSweepService.MinAutoStepSize));
            Assert.That(AutofocusSweepService.ResolveStepSize(settings, huge, null, null).StepSize, Is.EqualTo(AutofocusSweepService.MaxAutoStepSize));
            // f/15 on a coarse 0.1 µm/step focuser → thousands of steps → clamped.
            var slow = new OpticsSettingsDto(FocalLengthMm: 3000, ReducerFactor: 1.0, SensorWidthPx: 1, SensorHeightPx: 1, PixelSizeUm: 3.76, ApertureMm: 200);
            Assert.That(AutofocusSweepService.CfzStepSize(0.1, slow), Is.EqualTo(AutofocusSweepService.MaxAutoStepSize));
            // The reducer scales the focal length like it does for the pixel scale: f/5 × 0.8 → f/4.
            var reduced = new OpticsSettingsDto(FocalLengthMm: 530, ReducerFactor: 0.8, SensorWidthPx: 1, SensorHeightPx: 1, PixelSizeUm: 3.76, ApertureMm: 106);
            Assert.That(AutofocusSweepService.CfzStepSize(1.0, reduced), Is.EqualTo(18), "CFZ 35.2 µm → 17.6 → 18");
        }

        [Test]
        public void CurveHalfWidth_reads_the_doubling_offset_from_the_fitted_model() {
            var points = new List<FocusPoint>();
            for (var x = -400; x <= 400; x += 100) {
                var d = x / 100.0;
                points.Add(new FocusPoint(10_000 + x, 1.5 + 0.2 * d * d, 42));
            }
            var fit = FocusCurveFit.FitParabolic(points)!;
            Assert.That(AutofocusSweepService.CurveHalfWidth(fit, 9_600, 10_400), Is.EqualTo(TestCurveHalfWidth).Within(0.5));

            // The hyperbola √(a² + b²x²) with a = 1.2, b = 0.012: doubles at x = a√3 / b = 173.2.
            var hyper = new List<FocusPoint>();
            for (var x = -400; x <= 400; x += 100) {
                hyper.Add(new FocusPoint(10_000 + x, Math.Sqrt(1.2 * 1.2 + 0.012 * 0.012 * x * x), 42));
            }
            var hfit = FocusCurveFit.FitHyperbolic(hyper)!;
            Assert.That(AutofocusSweepService.CurveHalfWidth(hfit, 9_600, 10_400), Is.EqualTo(173.2).Within(0.5));
        }

        [Test]
        public void CurveHalfWidth_is_null_for_a_flat_curve_or_an_unusable_fit() {
            var flat = new List<FocusPoint>();
            for (var x = -400; x <= 400; x += 100) {
                flat.Add(new FocusPoint(10_000 + x, 2.0 + 1e-9 * x * x, 42));
            }
            var fit = FocusCurveFit.FitParabolic(flat)!;
            Assert.That(fit.IsUsable, Is.True);
            Assert.That(AutofocusSweepService.CurveHalfWidth(fit, 9_600, 10_400), Is.Null, "never doubles within 4× the half-span");

            var downward = new List<FocusPoint>();
            for (var x = -400; x <= 400; x += 100) {
                downward.Add(new FocusPoint(10_000 + x, 4.0 - 1e-5 * x * x, 42));
            }
            var bad = FocusCurveFit.FitParabolic(downward)!;
            Assert.That(bad.IsUsable, Is.False);
            Assert.That(AutofocusSweepService.CurveHalfWidth(bad, 9_600, 10_400), Is.Null);
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
    

        [Test]
        public async Task Noise_flooded_probes_are_dropped_and_an_all_noise_sweep_fails() {
            // A frame the detector flags as noise (thousands of speckle blobs, HFR ~2) must never
            // become a V-curve point — its flat HFR would fit as "in focus" wherever the focuser is.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var r = Result(2.0, 21000);
                    r.NoiseFlooded = true;
                    return r;
                },
                coarseMetric: (_, _) => 2.0 + Math.Abs(Current() - StartPosition) / 50.0);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(Current(), Is.EqualTo(StartPosition), "restored after the failed sweep");
        }

        [Test]
        public async Task Noise_flooded_edge_probes_are_skipped_like_unmeasurable_ones() {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var delta = (Current() - StartPosition) / 100.0;
                    if (Math.Abs(Current() - StartPosition) >= 400) {
                        var noise = Result(2.0, 21000);
                        noise.NoiseFlooded = true;
                        return noise;
                    }
                    return Result(1.5 + 0.2 * delta * delta, 42);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(Current(), Is.EqualTo(StartPosition).Within(30));
        }
    

        [Test]
        public void DefaultMetric_flags_a_frame_whose_blobs_are_noise_not_stars() {
            // The 2026-10-03 failure: a camera at its power-on gain read one real star and ~21,000
            // speckle blobs. 5-pixel crosses every 6 px give ~1,100 blobs on 40,000 px — far past the
            // one-per-400-px plausibility line — while the median/MAD background still sits at 1000.
            int w = 200, h = 200;
            var pixels = Enumerable.Repeat((ushort)1000, w * h).ToArray();
            for (int cy = 3; cy < h - 3; cy += 6) {
                for (int cx = 3; cx < w - 3; cx += 6) {
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) }) {
                        pixels[(cy + dy) * w + cx + dx] = 1400;
                    }
                }
            }

            var result = AutofocusSweepService.DefaultMetric(new AnalysisFrame(pixels, w, h, DateTimeOffset.UnixEpoch), CancellationToken.None);

            Assert.That(result.DetectedStars, Is.GreaterThan(w * h / AutofocusSweepService.NoiseFloodDivisor));
            Assert.That(result.NoiseFlooded, Is.True);
        }

        [Test]
        public void DefaultMetric_does_not_flag_a_real_star_field() {
            var result = AutofocusSweepService.DefaultMetric(StarFrame(200, 2.0, 40), CancellationToken.None);
            Assert.That(result.DetectedStars, Is.GreaterThan(0));
            Assert.That(result.NoiseFlooded, Is.False);
        }
    

        private static readonly double[] ThinWingSurvivors = { 29663, 29513, 29463, 29413, 29363, 29313, 29213 };

        [Test]
        public void TrimThinWings_drops_probes_that_saw_almost_no_stars_but_keeps_the_trustworthy_minimum() {
            // The 2026-10-03 sweep: 11- and 19-star probes at the far ends against 277 at focus.
            var points = new List<FocusPoint> {
                new(29763, 5.1, 11), new(29713, 3.8, 19), new(29663, 2.9, 44), new(29513, 2.6, 83),
                new(29463, 1.4, 204), new(29413, 1.0, 277), new(29363, 1.3, 227), new(29313, 2.6, 101),
                new(29213, 4.0, 59), new(29113, 4.9, 20),
            };
            var kept = AutofocusSweepService.TrimThinWings(points, keepAtLeast: 5);
            Assert.That(kept.Select(p => p.Position), Is.EquivalentTo(ThinWingSurvivors),
                "under 10 % of 277 stars (27.7) goes: 11, 19 and 20");

            var floor = AutofocusSweepService.TrimThinWings(points, keepAtLeast: 9);
            Assert.That(floor, Has.Count.EqualTo(9), "never below the trustworthy minimum: only the thinnest probe goes");
            Assert.That(floor.Any(p => p.StarCount == 11), Is.False);

            Assert.That(AutofocusSweepService.TrimThinWings(new List<FocusPoint>(), 5), Is.Empty);
            var even = points.Select(p => p with { StarCount = 50 }).ToList();
            Assert.That(AutofocusSweepService.TrimThinWings(even, 5), Has.Count.EqualTo(even.Count), "nothing thin, nothing dropped");
        }

        // ---- 2026-10-17 rig run (ASI533MC Pro + ZWO EAF through AlpacaBridge, travel 0..23000, start 100). ----
        // Fine (full-resolution) probes as the rig logged them, keyed by focuser position: 0..900 from the
        // first sweep, 930..1350 from the re-sweep. From 540 up every frame read HFR ≈ 1.21 on 9-15 "stars",
        // below anything measured near real focus (≈ 1.27 at 210), although the binned coarse probe at 550
        // read 11.6 native px: whatever full resolution detected there was not the defocused star field.
        private static readonly Dictionary<int, (double Hfr, int Stars)> Rig20261017Fine = new() {
            [0] = (1.717, 19), [30] = (1.770, 14), [60] = (1.891, 11), [90] = (1.859, 18), [120] = (1.922, 19),
            [150] = (1.625, 23), [180] = (1.300, 28), [210] = (1.269, 25), [240] = (1.490, 26), [270] = (1.700, 27),
            [300] = (1.654, 20), [330] = (1.938, 14), [360] = (2.117, 14), [390] = (1.750, 12), [420] = (1.718, 14),
            [450] = (1.810, 14), [480] = (2.138, 15), [510] = (1.322, 17), [540] = (1.214, 11), [570] = (1.213, 10),
            [600] = (1.227, 11), [630] = (1.223, 11), [660] = (1.208, 9), [690] = (1.210, 9), [720] = (1.215, 10),
            [750] = (1.211, 10), [780] = (1.216, 10), [810] = (1.222, 11), [840] = (1.220, 11), [870] = (1.231, 12),
            [900] = (1.198, 9), [930] = (1.212, 10), [960] = (1.213, 10), [990] = (1.215, 10), [1020] = (1.212, 10),
            [1050] = (1.205, 9), [1080] = (1.229, 12), [1110] = (1.225, 11), [1140] = (1.218, 11), [1170] = (1.217, 10),
            [1200] = (1.217, 10), [1230] = (1.211, 10), [1260] = (1.214, 10), [1290] = (1.225, 11), [1320] = (1.241, 14),
            [1350] = (1.214, 10),
        };

        /// <summary>The rig only measured the binned coarse metric at 100 (4.74) and 550 (11.59); this V through
        /// both points (floor 4, focus near 140) stands in for the positions it never measured.</summary>
        private static double Rig20261017Coarse(int position) => 4.0 + 0.0185 * Math.Abs(position - 140);

        [Test]
        public async Task Defocused_plateau_from_the_2026_10_17_rig_run_neither_re_centres_into_it_nor_settles_there() {
            var position = 100;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            var profiles = Profiles(Settings(steps: 15, stepSize: 30, restore: true, stepSizeAuto: false));
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object,
                metric: (_, _) => Rig20261017Fine.TryGetValue(position, out var probe) ? Result(probe.Hfr, probe.Stars) : Result(0, 0),
                coarseMetric: (_, _) => Rig20261017Coarse(position),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, 23_000)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            // Even without the plateau the near-focus probes are too noisy for a trustworthy V, so the honest
            // outcome is a loud failure back at the start, not a "completed" run at a defocused position.
            Assert.That(moves.Max(), Is.LessThanOrEqualTo(930), "no re-sweep into the plateau above the first window (top 900 + overshoot)");
            Assert.That(ok, Is.False, "the rig run settled at 1014 with R² 0.59, deep in the defocused plateau");
            Assert.That(position, Is.EqualTo(100), "restore-on-failure returns to the starting position");
            profiles.Verify(p => p.PutFocusCalibration(It.IsAny<FocusCalibrationDto?>()), Times.Never,
                "a failed run must not store a curve width, backlash or Smart Focus samples");
        }

        [Test]
        public async Task Probes_the_binned_frame_shows_far_from_focus_are_dropped_so_a_false_low_plateau_cannot_win() {
            // A clean V at 200 (the in-focus floor 1.5), but from 500 up the full-resolution detector reads a
            // false HFR 1.2 on ten "stars" (the 2026-10-17 shape); the binned measurement still sees the real
            // defocus there. Dropping those probes leaves the V to the fit.
            const int best = 200;
            var position = 100;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 15, stepSize: 30, stepSizeAuto: false)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    if (position >= 500) {
                        return Result(1.2, 10);
                    }
                    var delta = (position - best) / 100.0;
                    return Result(1.5 + 0.4 * delta * delta, 30);
                },
                coarseMetric: (_, _) => 4.0 + 0.0185 * Math.Abs(position - best),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, 23_000)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(position, Is.EqualTo(best).Within(30));
            Assert.That(moves.Max(), Is.LessThanOrEqualTo(930), "no re-sweep toward the plateau");
        }

        [Test]
        public async Task A_fit_that_explains_too_little_of_the_curve_fails_instead_of_moving_there() {
            // Every probe measurable, but alternating ±0.3 HFR noise swamps a shallow V: the parabola still
            // opens upward with its vertex inside the sweep, yet it explains well under the R² floor.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var profiles = Profiles(Settings(steps: 4, stepSize: 100, restore: true, stepSizeAuto: false));
            using var svc = new AutofocusSweepService(
                profiles.Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var delta = (Current() - StartPosition - 50) / 100.0;
                    var noise = (Math.Abs(Current() - StartPosition) / 100) % 2 == 0 ? 0.3 : -0.3;
                    return Result(1.5 + 0.03 * delta * delta + noise, 42);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(Current(), Is.EqualTo(StartPosition));
            profiles.Verify(p => p.PutFocusCalibration(It.IsAny<FocusCalibrationDto?>()), Times.Never);
        }

        [Test]
        public async Task Edge_re_centre_follows_the_probes_the_fit_used_not_a_thin_wing() {
            // Focus 550 above the start, beyond the first ±400 sweep's top edge. The bottom probe saw only 3
            // stars against 42 elsewhere and reads a spuriously low HFR: the thin-wing trim keeps it out of the
            // fit, and the re-centre must ignore it too, or the re-sweep heads down, away from focus.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, stepSizeAuto: false)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    if (Current() == StartPosition - 400) {
                        return Result(0.5, 3);
                    }
                    var delta = (Current() - StartPosition - 550) / 100.0;
                    return Result(1.5 + 0.2 * delta * delta, 42);
                });

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(Current(), Is.EqualTo(StartPosition + 550).Within(30));
        }

        [Test]
        public async Task Coarse_search_keeps_a_start_already_in_focus_when_binning_loses_its_sharp_stars() {
            // 2026-10-17, narrowband (L-eNhance) filter: a refocus started at focus, where the faint, sharp
            // stars vanish once the frame is binned 8× (coarse metric +∞) although full resolution measures
            // them. Treated as "worse than anything", the coarse walk left focus for the finite reading above
            // and the fine sweep had to re-centre. Both neighbours measurable means the start lies between
            // them, inside the V: keep it.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, stepSizeAuto: false)).Object, focuser.Object, Frames().Object,
                metric: VCurveMetric(Current, StartPosition),
                coarseMetric: (_, _) => Math.Abs(Current() - StartPosition) < 100 ? double.PositiveInfinity : 2.0 + Math.Abs(Current() - StartPosition) / 50.0);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves.GetRange(0, 4), Is.EqualTo(SharpStartMoves),
                "start, both neighbours, then the fine sweep's overshoot above a sweep centred on the start");
            Assert.That(Current(), Is.EqualTo(StartPosition).Within(30));
        }

        [Test]
        public async Task Coarse_search_does_not_mistake_a_start_too_defocused_to_bin_for_focus() {
            // The other way +∞ happens: a start so far out that even the binned discs are lost, with full
            // resolution finding a few spurious blobs. Its far neighbour is lost too, so it is not bracketed and
            // the walk still heads toward the measurable side.
            const int best = StartPosition - 2000;
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, stepSizeAuto: false)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var off = Math.Abs(Current() - best);
                    if (off > 600) {
                        return Result(1.1, 5); // spurious blobs, the 2026-10-17 plateau shape
                    }
                    var delta = (Current() - best) / 100.0;
                    return Result(1.5 + 0.2 * delta * delta, 42);
                },
                coarseMetric: (_, _) => Math.Abs(Current() - best) > 1800 ? double.PositiveInfinity : 2.0 + Math.Abs(Current() - best) / 50.0);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(Current(), Is.EqualTo(best).Within(30));
        }

        private static readonly int[] PlateauDropped = { 796, 760, 724, 688, 652, 616 };
        private static readonly int[] SharpStartMoves = { StartPosition, StartPosition + 400, StartPosition - 400, StartPosition + 500 };

        [Test]
        public void FalsePlateauProbes_drops_the_plateau_but_keeps_genuine_wings_from_the_2026_10_17_runs() {
            // 10 s re-sweep: genuine wings (full-resolution HFR stops growing near 2.5-3.7) around a 1.22 minimum,
            // binned +∞ at 216 and 180 where the sharp narrowband stars average away.
            var wings = new List<(int Pos, double Hfr, int Stars, double Binned)> {
                (576, 3.746, 8, 12.8), (540, 3.531, 5, 11.77), (504, 2.759, 6, 10.23), (468, 2.479, 8, 9.17),
                (432, 2.616, 12, 8.0), (396, 3.601, 7, 7.05), (360, 3.455, 6, 6.13), (324, 2.247, 13, 5.12),
                (288, 2.021, 19, 4.3), (252, 1.491, 23, 4.39), (216, 1.223, 28, double.PositiveInfinity),
                (180, 1.485, 22, double.PositiveInfinity), (144, 2.272, 13, 4.13), (108, 2.793, 12, 4.78),
                (72, 2.469, 13, 6.21), (36, 2.826, 9, 6.61), (0, 2.621, 9, 6.92),
            };
            Assert.That(AutofocusSweepService.FalsePlateauProbes(
                wings.Select(w => new FocusPoint(w.Pos, w.Hfr, w.Stars)).ToList(), wings.Select(w => w.Binned).ToList()),
                Is.Empty, "every far probe reads well above the 1.22 minimum: real wings");

            // 10 s first sweep: the false plateau (3-8 "stars" at 1.14-1.45) far out, the V near 220.
            var plateau = new List<(int Pos, double Hfr, int Stars, double Binned)> {
                (796, 1.140, 3, 21.81), (760, 1.451, 4, 20.6), (724, 1.135, 3, 19.33), (688, 1.139, 3, 17.69),
                (652, 1.142, 3, 16.78), (616, 1.281, 8, 15.24), (580, 2.899, 12, 13.29), (544, 3.905, 4, 11.95),
                (508, 3.579, 4, 10.69), (472, 2.531, 7, 9.24), (436, 2.725, 11, 8.05), (400, 3.558, 7, 7.14),
                (364, 2.705, 11, 6.34), (328, 2.420, 13, 5.13), (292, 2.197, 13, 4.5), (256, 1.567, 21, 4.21),
                (220, 1.216, 33, double.PositiveInfinity),
            };
            var dropped = AutofocusSweepService.FalsePlateauProbes(
                plateau.Select(p => new FocusPoint(p.Pos, p.Hfr, p.Stars)).ToList(), plateau.Select(p => p.Binned).ToList());
            Assert.That(dropped.Select(k => plateau[k].Pos), Is.EquivalentTo(PlateauDropped),
                "the probes reading as sharp as focus with the binned frame far out go; 580 and in are wing");
            Assert.That(dropped, Is.Ordered.Descending, "removable in order");
        }

        // The 2026-10-17 22:10 re-sweep (L-eNhance, 10 s): a clean V around 216 whose full-resolution HFR stops
        // growing about 150 steps out and scatters at 2.3-3.8.
        private static readonly (int Pos, double Hfr, int Stars)[] Rig20261017Sweep = {
            (576, 3.001, 13), (540, 3.840, 4), (504, 3.079, 5), (468, 2.286, 9), (432, 2.809, 13), (396, 3.236, 8),
            (360, 2.695, 12), (324, 2.304, 12), (288, 1.858, 16), (252, 1.451, 20), (216, 1.245, 32), (180, 1.489, 25),
            (144, 2.139, 12), (108, 2.744, 11), (72, 3.299, 9), (36, 2.819, 10), (0, 2.681, 11),
        };

        [Test]
        public void MeasuredHalfWidth_reads_the_doubling_distance_off_the_probes_not_the_flattened_model() {
            var points = Rig20261017Sweep.Select(p => new FocusPoint(p.Pos, p.Hfr, p.Stars)).ToList();
            // 2 × 1.245 = 2.49: crossed between 324 and 360 above (≈ 127 out) and between 144 and 108 below (≈ 95).
            Assert.That(AutofocusSweepService.MeasuredHalfWidth(points), Is.EqualTo(111).Within(5));
            Assert.That(AutofocusSweepService.MeasuredHalfWidth(points.Where(p => p.Hfr < 2.4).ToList()), Is.Null,
                "a sweep that never reaches twice the minimum has nothing to read");
        }

        [Test]
        public async Task An_automatic_step_sized_from_a_too_wide_stored_width_shrinks_itself_and_stores_the_measured_width() {
            // The stored width (192, from a fit dragged wide by flat wings) gives step 36 over 8 steps a side:
            // half the probes land where the V has gone flat and noisy, and the fit fails. Automatic sizing must
            // recover on its own: re-sweep at the step the probes themselves measure, then store that width.
            const int best = 216;
            var position = 220;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings(steps: 8, stepSize: 30, stepSizeAuto: true));
            store.PutFocusCalibration(new FocusCalibrationDto(
                Samples: new[] { new FocusCalibrationSampleDto(9_000, 30, 2.0, 4.0, 0.9, 8.0, 0, 0, 0, 0) },
                CalibratedUtc: DateTimeOffset.UtcNow, FocuserTemperatureC: null, Filter: null, CurveHalfWidthSteps: 192.4));
            using var svc = new AutofocusSweepService(
                store, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var d = position - best;
                    if (Math.Abs(d) > 150) {
                        var flat = (position / 36) % 2 == 0 ? 2.4 : 3.6; // flat, noisy wings
                        return Result(flat, 9, FeatureStars(9, flat));
                    }
                    var hfr = 1.25 * Math.Sqrt(1 + Math.Pow(d / 64.0, 2)); // doubles ≈ 111 out
                    return Result(hfr, 30, FeatureStars(12, hfr));
                },
                coarseMetric: (_, _) => Math.Abs(position - best) < 50 ? double.PositiveInfinity : 4.0 + 0.03 * Math.Abs(position - best),
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, 23_000)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(position, Is.EqualTo(best).Within(25));
            Assert.That(store.GetFocusCalibration()!.CurveHalfWidthSteps, Is.LessThan(150),
                "the next automatic sweep is sized from the V the probes measured, not the flattened fit");
        }

        [Test]
        public async Task A_completed_sweep_stores_the_probe_measured_width_when_the_fit_reads_wider() {
            // A sky whose HFR grows linearly with defocus (1.5 + |d|/50, doubling 75 steps out). The hyperbolic
            // and parabolic models both round that corner off and reach twice their minimum further out than
            // the probes do; the stored width must be the probes', so the next automatic sweep is not sized wide.
            const int best = 10_000;
            var position = 10_000;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            var store = new InMemoryProfileStore();
            store.PutAutofocusSettings(Settings(steps: 4, stepSize: 40, stepSizeAuto: false));
            var probes = new List<FocusPoint>();
            var posted = new List<NotificationDto>();
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns<NotificationDto, CancellationToken>((d, _) => { posted.Add(d); return Task.CompletedTask; });
            using var svc = new AutofocusSweepService(
                store, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    var hfr = 1.5 + Math.Abs(position - best) / 50.0;
                    probes.Add(new FocusPoint(position, hfr, 30));
                    return Result(hfr, 30, FeatureStars(12, hfr));
                },
                coarseMetric: (_, _) => Math.Abs(position - best) < 50 ? double.PositiveInfinity : 4.0 + 0.03 * Math.Abs(position - best),
                notifications: notifications.Object);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True, string.Join(" | ", posted.Select(n => n.Message)));
            var measured = AutofocusSweepService.MeasuredHalfWidth(probes.Where(p => Math.Abs(p.Position - best) <= 160).ToList());
            var fit = FocusCurveFit.FitBest(probes.Where(p => Math.Abs(p.Position - best) <= 160).ToList());
            var model = AutofocusSweepService.CurveHalfWidth(fit!, best - 160, best + 160);
            Assert.That(model, Is.GreaterThan(measured!.Value + 5), "precondition: the fitted curve reads wider than the probes");
            Assert.That(store.GetFocusCalibration()!.CurveHalfWidthSteps, Is.EqualTo(measured.Value).Within(1.0),
                "the narrower, probe-measured width is stored");
        }

        [Test]
        public async Task Coarse_search_keeps_a_sharp_start_near_the_travel_stop() {
            // Focus 120 above the travel stop with a ±400 half-width: the lower neighbour would be at -280, so the
            // bracket is checked at the stop itself (0) instead of giving up on it.
            const int best = 120;
            var position = best;
            var moves = new List<int>();
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = double.NaN });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, stepSizeAuto: false)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1.5 + 0.2 * Math.Pow((position - best) / 100.0, 2), 42),
                coarseMetric: (_, _) => Math.Abs(position - best) < 60 ? double.PositiveInfinity : 2.0 + Math.Abs(position - best) / 50.0,
                travelRange: _ => Task.FromResult<(int Min, int Max)?>((0, 23_000)));

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(moves.GetRange(0, 3), Is.EqualTo(SharpStartAtStopMoves), "start, the neighbour above, then the stop below");
            Assert.That(moves.Take(4).Max(), Is.LessThan(best + 800), "no walk away from focus");
            Assert.That(position, Is.EqualTo(best).Within(30));
        }

        private static readonly int[] SharpStartAtStopMoves = { 120, 520, 0 };

        [Test]
        public void FalsePlateauProbes_spares_a_near_focus_probe_whose_binned_reading_spiked() {
            // 2026-10-17 22:24, step 20: as focus nears, the binned frame starts losing stars and two readings
            // spiked (9.17 at 281, 11.8 at 261) between ordinary ones (4.5 at 301, +∞ at 241). 261 read 1.63 on
            // 23 stars, a real probe on the V, and was dropped. The plateau is contiguous out to the sweep edge;
            // a spike with in-gate readings beyond it is not.
            var probes = new List<(int Pos, double Hfr, int Stars, double Binned)> {
                (381, 3.304, 7, 6.28), (361, 2.733, 9, 6.01), (341, 2.994, 9, 5.98), (321, 2.350, 12, 4.91),
                (301, 2.034, 16, 4.5), (281, 1.962, 14, 9.17), (261, 1.634, 23, 11.8), (241, 1.388, 23, double.PositiveInfinity),
                (221, 1.247, 45, double.PositiveInfinity), (201, 1.304, 34, double.PositiveInfinity), (181, 1.414, 28, 4.05),
                (161, 1.917, 14, 5.65), (141, 2.159, 12, 5.96), (121, 2.803, 9, 5.25), (101, 2.776, 11, 5.3),
                (81, 2.992, 9, 5.87), (61, 2.250, 15, 6.46),
            };
            Assert.That(AutofocusSweepService.FalsePlateauProbes(
                probes.Select(p => new FocusPoint(p.Pos, p.Hfr, p.Stars)).ToList(), probes.Select(p => p.Binned).ToList()),
                Is.Empty);
        }

        /// <summary>A one-shot-colour frame shaped like the 2026-10-17 ASI533MC Pro + L-eNhance light at offset
        /// 50: R background 2073, G 2004, B 3796 (per-colour noise 6-12 ADU), with 100 faint stars of peak
        /// 60-250 ADU on a 10 × 10 grid.</summary>
        private static AnalysisFrame UnevenBayerFrame(int size = 1000) {
            var pixels = new ushort[size * size];
            // Deterministic noise from a tiny LCG (System.Random trips CA5394; test-only pseudo-noise).
            uint state = 1017;
            double Next() { state = state * 1664525u + 1013904223u; return (state >> 8) / 16777216.0; }
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    var (bg, sd) = ((y & 1) << 1 | (x & 1)) switch {
                        0 => (2073.0, 7.0), 3 => (3796.0, 12.0), _ => (2004.0, 6.0),
                    };
                    var noise = sd * Math.Sqrt(-2 * Math.Log(1 - Next())) * Math.Cos(2 * Math.PI * Next());
                    var v = bg + noise;
                    var cx = (x / 100) * 100 + 50;
                    var cy = (y / 100) * 100 + 50;
                    var peak = 60 + 190 * (((x / 100) * 10 + (y / 100)) % 10) / 9.0;
                    var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    v += peak * Math.Exp(-r2 / (2 * 1.2 * 1.2));
                    pixels[y * size + x] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }
            return new AnalysisFrame(pixels, size, size, DateTimeOffset.UnixEpoch, "RGGB");
        }

        [Test]
        public void DefaultMetric_equalises_the_bayer_phases_only_when_the_frame_carries_a_pattern() {
            // The same uneven frame tagged mono must measure as the raw detector would (no equalisation), and
            // a mono frame whose sampled phase medians differ by noise alone must come back untouched.
            var colour = UnevenBayerFrame();
            var asMono = colour with { BayerPattern = null };
            var direct = StarDetector.Detect(asMono.Pixels.Span, asMono.Width, asMono.Height,
                new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true });
            var viaMetric = AutofocusSweepService.DefaultMetric(asMono, CancellationToken.None);
            Assert.That(viaMetric.DetectedStars, Is.EqualTo(direct.DetectedStars), "a frame without a Bayer pattern is not equalised");
            Assert.That(viaMetric.DetectedStars, Is.LessThan(AutofocusSweepService.DefaultMetric(colour, CancellationToken.None).DetectedStars));

            var noisy = NoisyMonoFrame(256);
            var measured = AutofocusSweepService.DefaultMetric(noisy, CancellationToken.None);
            var raw = StarDetector.Detect(noisy.Pixels.Span, noisy.Width, noisy.Height,
                new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true });
            Assert.That(measured.AverageHFR, Is.EqualTo(raw.AverageHFR).Within(1e-9), "the metric leaves a mono frame alone");
        }

        /// <summary>A mono frame with 1000 ADU background, a few ADU of pseudo-noise and a handful of stars.</summary>
        private static AnalysisFrame NoisyMonoFrame(int size) {
            var pixels = new ushort[size * size];
            uint state = 4242;
            double Next() { state = state * 1664525u + 1013904223u; return (state >> 8) / 16777216.0; }
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    var v = 1000.0 + 20.0 * (Next() - 0.5);
                    var cx = (x / 64) * 64 + 32;
                    var cy = (y / 64) * 64 + 32;
                    var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    v += 20000.0 * Math.Exp(-r2 / (2 * 1.5 * 1.5));
                    pixels[y * size + x] = (ushort)Math.Clamp(v, 0, 65535);
                }
            }
            return new AnalysisFrame(pixels, size, size, DateTimeOffset.UnixEpoch);
        }

        [Test]
        public void DefaultMetric_finds_the_stars_on_a_colour_frame_whose_bayer_channels_sit_at_different_backgrounds() {
            // 2026-10-17: on the raw mosaic the blue pixels' background (1.8× the others) inflated the robust
            // sigma ten-fold (65 ADU against a real 6-12), the threshold rose with it and the probe saw 26 stars
            // where the frame held ~240. Equalising the four Bayer phases first restores them.
            var result = AutofocusSweepService.DefaultMetric(UnevenBayerFrame(), CancellationToken.None);
            Assert.That(result.DetectedStars, Is.GreaterThanOrEqualTo(80), "most of the 100 faint stars");
            Assert.That(result.NoiseFlooded, Is.False);
        }

        [Test]
        public void DefaultMetric_leaves_a_mono_frame_measuring_as_before() {
            var frame = StarFrame(512, 1.5, 64);
            var direct = StarDetector.Detect(frame.Pixels.Span, frame.Width, frame.Height,
                new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true });
            var viaMetric = AutofocusSweepService.DefaultMetric(frame, CancellationToken.None);
            Assert.That(viaMetric.DetectedStars, Is.EqualTo(direct.DetectedStars));
            Assert.That(viaMetric.AverageHFR, Is.EqualTo(direct.AverageHFR).Within(1e-9));
        }

        /// <summary>Width of a baseline JPEG, read from its SOF0 marker.</summary>
        internal static int JpegWidth(ReadOnlySpan<byte> jpeg) {
            for (var i = 2; i + 8 < jpeg.Length; i++) {
                if (jpeg[i] == 0xFF && (jpeg[i + 1] == 0xC0 || jpeg[i + 1] == 0xC2)) {
                    return (jpeg[i + 7] << 8) | jpeg[i + 8];
                }
            }
            return -1;
        }

        [Test]
        public async Task The_probe_preview_of_a_colour_camera_is_debayered() {
            // 2026-10-17: the Smart Focus page showed the raw colour-filter grid of an ASI533MC Pro frame. A frame
            // carrying a Bayer pattern renders through the super-pixel debayer (half the width), not as a mosaic.
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AnalysisFrame(StarFrame(128, 1.5, 32).Pixels, 128, 128, DateTimeOffset.UnixEpoch, "RGGB"));
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, stepSizeAuto: false)).Object, focuser.Object, frames.Object,
                metric: VCurveMetric(Current, StartPosition), tracker: tracker);

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.True);

            var frame = tracker.GetFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(JpegWidth(frame!.Value.Jpeg.Span), Is.EqualTo(64), "super-pixel debayer: 128 → 64");
        }
    }
}
