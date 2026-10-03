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
            var fineProbes = 0;
            using var svc = new AutofocusSweepService(
                Profiles(Settings(steps: 4, stepSize: 100, restore: true)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => { fineProbes++; return Result(1.5, 42); },
                coarseMetric: (_, _) => 1e6 - Current());

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            Assert.That(fineProbes, Is.Zero);
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
    }
}
