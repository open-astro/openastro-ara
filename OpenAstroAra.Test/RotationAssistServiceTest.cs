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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The by-hand rotation readout: a scripted solver stands in for the camera + plate solver.</summary>
    [TestFixture]
    public class RotationAssistServiceTest {
        private static readonly double[] ExpectedDeltas = { 20.0, 10.0, 1.5, 0.0 };

        /// <summary>Yields the scripted angles in order (null = a failed solve), then holds on the last one;
        /// each solve takes a few ms so a stop has something in flight to drain.</summary>
        private sealed class ScriptedSolver : IPositionAngleSolver {
            private readonly Queue<double?> _script;
            private double? _last;
            public int Solves;
            public int FrameSize = 4; // 4×4 stub: too small to render, so HasFrame stays false unless a test asks for a real-sized one
            public ScriptedSolver(params double?[] script) { _script = new Queue<double?>(script); }
            public double LastExposure;
            public int LastBinning;
            public int MaxBinning { get; set; } = 2;
            public readonly List<int> Binnings = new();
            public async Task<RotationSolve?> SolvePositionAngleAsync(double exposureSeconds, int binning, CancellationToken ct) {
                await Task.Delay(5, ct);
                Solves++;
                LastExposure = exposureSeconds;
                LastBinning = binning;
                Binnings.Add(binning);
                if (_script.Count > 0) { _last = _script.Dequeue(); }
                if (_last is not { } pa) return null;
                var frame = new AnalysisFrame(new ushort[FrameSize * FrameSize], FrameSize, FrameSize, DateTimeOffset.UtcNow);
                return new RotationSolve(pa, RaDeg: 314.82, DecDeg: 44.53, PixelScaleArcsec: 2.3, Flipped: true, frame);
            }
        }

        private static async Task<RotationAssistStatusDto> WaitForState(RotationAssistService svc, params string[] states) {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline) {
                var s = svc.GetStatus();
                if (Array.IndexOf(states, s.State) >= 0) return s;
                await Task.Delay(5);
            }
            return svc.GetStatus();
        }

        private static async Task<RotationAssistStatusDto> WaitForSeq(RotationAssistService svc, long seq) {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline) {
                var s = svc.GetStatus();
                if (s.Seq >= seq || s.State == "error") return s;
                await Task.Delay(5);
            }
            return svc.GetStatus();
        }

        [Test]
        public void Target_normalises_and_rejects_non_finite() {
            Assert.That(RotationAssistService.NormaliseTarget(-61), Is.EqualTo(299));
            Assert.That(RotationAssistService.NormaliseTarget(725), Is.EqualTo(5));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.NormaliseTarget(double.NaN));
        }

        [Test]
        public async Task The_loop_reports_solved_angle_and_folded_delta_until_within_tolerance() {
            // Target 120°: solved 100 → +20, 110 → +10, 118.5 → +1.5 (inside the 2° tolerance). A 180° flip is the
            // same framing, so 300 → 0 (not 180).
            var solver = new ScriptedSolver(100, 110, 118.5, 300);
            using var svc = new RotationAssistService(solver, () => 2.0);
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"));

            await svc.StartAsync(new RotationAssistStartRequestDto(120), CancellationToken.None);
            var s = await WaitForSeq(svc, 4);

            Assert.That(s.Active, Is.True);
            Assert.That(s.TargetPositionAngleDeg, Is.EqualTo(120));
            Assert.That(s.ToleranceDeg, Is.EqualTo(2.0));
            // The solver holds the last scripted angle, so a fifth sample may already have landed.
            var deltas = s.Recent.Take(4).Select(r => r.DeltaDeg).ToList();
            Assert.That(deltas, Is.EqualTo(ExpectedDeltas).Within(0.01).AsCollection);
            Assert.That(s.Latest!.SolvedPositionAngleDeg, Is.EqualTo(300));
            Assert.That(s.WithinTolerance, Is.True);
            // The solve's geometry rides along for the client's overlay; a stub frame too small to render leaves no picture.
            Assert.That(s.Latest.Flipped, Is.True);
            Assert.That(s.Latest.PixelScaleArcsec, Is.EqualTo(2.3));
            Assert.That(s.Latest.FrameWidth, Is.EqualTo(4));
            Assert.That(s.HasFrame, Is.False);
            Assert.That(svc.GetFrame(), Is.Null);

            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
            Assert.That(svc.GetStatus().Active, Is.False);
            Assert.That(svc.GetStatus().Latest, Is.Not.Null, "the last readout stays visible after a stop");
        }

        [Test]
        public async Task Repeated_failed_solves_end_the_loop_in_error_and_a_start_restarts_it() {
            var solver = new ScriptedSolver(null, null, null, null, null, 50);
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(50), CancellationToken.None);
            var s = await WaitForSeq(svc, 1);

            Assert.That(s.State, Is.EqualTo("error"));
            Assert.That(s.Active, Is.False);
            Assert.That(s.ConsecutiveFailures, Is.EqualTo(RotationAssistService.MaxConsecutiveFailures));
            Assert.That(s.Error, Does.Contain("solves in a row failed"));

            await svc.StartAsync(new RotationAssistStartRequestDto(50), CancellationToken.None);
            s = await WaitForSeq(svc, 1);
            Assert.That(s.State, Is.EqualTo("running"));
            Assert.That(s.Latest!.DeltaDeg, Is.EqualTo(0));
            await svc.StopAsync();
        }

        [Test]
        public async Task A_second_start_is_refused_while_running_and_stop_is_idempotent() {
            using var svc = new RotationAssistService(new ScriptedSolver(10), () => 1.0);
            await svc.StopAsync(); // nothing running — no-op
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new RotationAssistStartRequestDto(20), CancellationToken.None));
            await svc.StopAsync();
            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task A_real_sized_frame_is_rendered_and_tagged_with_its_sample() {
            var solver = new ScriptedSolver(10) { FrameSize = 96 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            var s = await WaitForSeq(svc, 1);
            await svc.StopAsync();

            Assert.That(s.HasFrame, Is.True);
            Assert.That(s.FrameSeq, Is.GreaterThanOrEqualTo(1));
            var frame = svc.GetFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.Value.Jpeg.Length, Is.GreaterThan(100), "a JPEG, not an empty buffer");
            Assert.That(frame.Value.Seq, Is.EqualTo(svc.GetStatus().FrameSeq));
        }

        private sealed class NotReadySolver : IPositionAngleSolver {
            public void EnsureReady() => throw new OpenAstroAra.PlateSolving.PlateSolverConfigurationException("no solver");
            public Task<RotationSolve?> SolvePositionAngleAsync(double exposureSeconds, int binning, CancellationToken ct) => Task.FromResult<RotationSolve?>(null);
        }

        [Test]
        public async Task A_solver_that_cannot_run_refuses_the_start_and_leaves_the_readout_idle() {
            using var svc = new RotationAssistService(new NotReadySolver(), () => 1.0);
            var ex = await Assert.ThrowsAsync<RotationAssistNotReadyException>(
                () => svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("no solver"));
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"));
        }

        [Test]
        public void Exposure_and_mode_are_validated_and_defaulted() {
            Assert.That(RotationAssistService.ResolveExposure(null, 3.5), Is.EqualTo(3.5));
            Assert.That(RotationAssistService.ResolveExposure(null, double.NaN), Is.EqualTo(2.0));
            Assert.That(RotationAssistService.ResolveExposure(null, 500), Is.EqualTo(RotationAssistService.MaxExposureSeconds));
            Assert.That(RotationAssistService.ResolveExposure(1.5, 3.5), Is.EqualTo(1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveExposure(0, 3.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveExposure(61, 3.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveExposure(double.PositiveInfinity, 3.5));
            Assert.That(RotationAssistService.ResolveMode(null), Is.EqualTo(RotationAssistModes.Loop));
            Assert.That(RotationAssistService.ResolveMode(" Single "), Is.EqualTo(RotationAssistModes.SingleShot));
            Assert.Throws<ArgumentException>(() => RotationAssistService.ResolveMode("burst"));
        }

        [Test]
        public async Task A_single_shot_solves_one_frame_then_stops_keeping_the_result_and_the_history() {
            var solver = new ScriptedSolver(140, 130, 121);
            using var svc = new RotationAssistService(solver, () => 1.0, () => 4.0);
            Assert.That(svc.GetStatus().DefaultExposureSeconds, Is.EqualTo(4.0));

            await svc.StartAsync(new RotationAssistStartRequestDto(120, ExposureSeconds: 1.5, Mode: "single"), CancellationToken.None);
            var s = await WaitForSeq(svc, 1);
            // Let the loop body finish flipping the state after the sample landed.
            for (var i = 0; i < 100 && svc.GetStatus().State == "running"; i++) { await Task.Delay(5); }
            s = svc.GetStatus();
            Assert.That(s.State, Is.EqualTo("stopped"));
            Assert.That(s.Active, Is.False);
            Assert.That(s.Mode, Is.EqualTo("single"));
            Assert.That(s.ExposureSeconds, Is.EqualTo(1.5));
            Assert.That(solver.LastExposure, Is.EqualTo(1.5));
            Assert.That(solver.Solves, Is.EqualTo(1), "a single shot is ONE solve");
            Assert.That(s.Latest!.DeltaDeg, Is.EqualTo(-20));

            // The user turns, shoots again toward the SAME target: the history carries on (the client's
            // keep-going / go-back advice needs the previous sample).
            await svc.StartAsync(new RotationAssistStartRequestDto(120, Mode: "single"), CancellationToken.None);
            s = await WaitForSeq(svc, 2);
            Assert.That(s.Recent.Count, Is.EqualTo(2));
            Assert.That(s.Latest!.DeltaDeg, Is.EqualTo(-10));
            Assert.That(solver.LastExposure, Is.EqualTo(4.0), "no exposure in the request → the profile default");

            // The same angle with float rounding noise is still the same target.
            await svc.StartAsync(new RotationAssistStartRequestDto(120.004, Mode: "single"), CancellationToken.None);
            s = await WaitForSeq(svc, 3);
            Assert.That(s.Recent.Count, Is.EqualTo(3));

            // A NEW target starts the history over.
            await svc.StartAsync(new RotationAssistStartRequestDto(90, Mode: "single"), CancellationToken.None);
            s = await WaitForSeq(svc, 4);
            Assert.That(s.Recent.Count, Is.EqualTo(1));
            Assert.That(s.TargetPositionAngleDeg, Is.EqualTo(90));
        }

        [Test]
        public async Task A_single_shot_that_will_not_solve_ends_in_error_at_once() {
            var solver = new ScriptedSolver((double?)null);
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10, Mode: "single"), CancellationToken.None);
            var s = await WaitForSeq(svc, 1);
            Assert.That(s.State, Is.EqualTo("error"));
            Assert.That(s.Error, Does.Contain("take another"));
            Assert.That(solver.Solves, Is.EqualTo(1));
        }

        [Test]
        public void Binning_defaults_to_the_camera_maximum_capped_at_four_and_rejects_more() {
            Assert.That(RotationAssistService.ResolveBinning(null, 0), Is.EqualTo(4), "unknown camera ceiling → the cap");
            Assert.That(RotationAssistService.ResolveBinning(null, 2), Is.EqualTo(2));
            Assert.That(RotationAssistService.ResolveBinning(null, 16), Is.EqualTo(4));
            Assert.That(RotationAssistService.ResolveBinning(null, 1), Is.EqualTo(1));
            Assert.That(RotationAssistService.ResolveBinning(2, 4), Is.EqualTo(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveBinning(0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveBinning(3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => RotationAssistService.ResolveBinning(8, 0));
        }

        [Test]
        public async Task The_loop_runs_binned_and_the_status_reports_it() {
            var solver = new ScriptedSolver(10) { MaxBinning = 4 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            Assert.That(svc.GetStatus().AutoBinning, Is.EqualTo(4));
            Assert.That(svc.GetStatus().MaxBinning, Is.EqualTo(4));
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            var s = await WaitForSeq(svc, 1);
            await svc.StopAsync();
            Assert.That(s.Binning, Is.EqualTo(4));
            Assert.That(solver.LastBinning, Is.EqualTo(4));

            await svc.StartAsync(new RotationAssistStartRequestDto(10, Binning: 2), CancellationToken.None);
            s = await WaitForSeq(svc, 2);
            await svc.StopAsync();
            Assert.That(s.Binning, Is.EqualTo(2));
            Assert.That(solver.LastBinning, Is.EqualTo(2));
        }

        [Test]
        public async Task A_restart_across_north_toward_the_same_angle_keeps_the_history() {
            var solver = new ScriptedSolver(359.5) { MaxBinning = 1 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(359.999, Mode: "single"), CancellationToken.None);
            await WaitForState(svc, "stopped");
            // 0.0005° is 0.0015° from 359.999° across north: the same dial angle, float-rounded.
            await svc.StartAsync(new RotationAssistStartRequestDto(0.0005, Mode: "single"), CancellationToken.None);
            var s = await WaitForState(svc, "stopped");
            Assert.That(s.Recent.Count, Is.EqualTo(2), "the history carries on across 0°");
        }

        [Test]
        public async Task A_single_shot_is_binned_like_the_loop() {
            var solver = new ScriptedSolver(10) { MaxBinning = 4 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10, Mode: "single"), CancellationToken.None);
            var s = await WaitForState(svc, "stopped");
            Assert.That(s.Binning, Is.EqualTo(4), "auto binning applies to a single shot too");
            Assert.That(solver.Binnings, Has.Count.EqualTo(1).And.All.EqualTo(4));

            await svc.StartAsync(new RotationAssistStartRequestDto(10, Mode: "single", Binning: 1), CancellationToken.None);
            s = await WaitForState(svc, "stopped");
            Assert.That(s.Binning, Is.EqualTo(1));
            Assert.That(solver.LastBinning, Is.EqualTo(1));
        }

        [Test]
        public async Task Done_takes_one_full_resolution_frame_and_confirms_within_tolerance() {
            // Binned loop reads 12° then 10.5°; the 1×1 confirmation reads 10.4° — within ±1°.
            var solver = new ScriptedSolver(12, 10.5, 10.4) { MaxBinning = 4 };
            using var svc = new RotationAssistService(solver, () => 1.0, () => 3.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10, ExposureSeconds: 0.5), CancellationToken.None);
            await WaitForSeq(svc, 2);

            await svc.ConfirmAsync(CancellationToken.None);
            var s = await WaitForState(svc, "confirmed", "not_confirmed", "error");
            Assert.That(s.State, Is.EqualTo("confirmed"));
            Assert.That(s.Active, Is.False);
            Assert.That(s.Confirmation, Is.Not.Null);
            Assert.That(s.Confirmation!.DeltaDeg, Is.EqualTo(-0.4));
            Assert.That(s.Latest!.Seq, Is.EqualTo(s.Confirmation.Seq), "the confirmation is the latest sample too");
            Assert.That(solver.Binnings[^1], Is.EqualTo(1), "the check is 1×1");
            Assert.That(solver.LastExposure, Is.EqualTo(3.0), "…at the full plate-solve exposure");
            Assert.That(solver.Binnings.Take(solver.Binnings.Count - 1), Has.All.EqualTo(4), "the loop was binned");
        }

        [Test]
        public async Task Done_off_target_ends_not_confirmed_and_a_restart_keeps_the_history() {
            var solver = new ScriptedSolver(10.5, 13) { MaxBinning = 2 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10, Mode: "single"), CancellationToken.None);
            await WaitForState(svc, "stopped");
            await svc.ConfirmAsync(CancellationToken.None);
            var s = await WaitForState(svc, "confirmed", "not_confirmed", "error");
            Assert.That(s.State, Is.EqualTo("not_confirmed"));
            Assert.That(s.Confirmation!.DeltaDeg, Is.EqualTo(-3));
            Assert.That(s.Recent.Count, Is.EqualTo(2));

            // Back to the loop toward the same target: history kept, confirmation cleared.
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            s = await WaitForSeq(svc, 3);
            await svc.StopAsync();
            Assert.That(s.Confirmation, Is.Null);
            // The loop keeps solving every ~5 ms, so a fourth sample may land before the poll snapshots.
            Assert.That(s.Recent.Count, Is.GreaterThanOrEqualTo(3), "the history carries on");
        }

        [Test]
        public async Task Done_that_will_not_solve_ends_in_error_and_nothing_to_confirm_is_a_conflict() {
            using var idle = new RotationAssistService(new ScriptedSolver(10), () => 1.0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => idle.ConfirmAsync(CancellationToken.None));

            var solver = new ScriptedSolver(10, null);
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10, Mode: "single"), CancellationToken.None);
            await WaitForState(svc, "stopped");
            await svc.ConfirmAsync(CancellationToken.None);
            var s = await WaitForState(svc, "confirmed", "not_confirmed", "error");
            Assert.That(s.State, Is.EqualTo("error"));
            Assert.That(s.Error, Does.Contain("full-resolution"));
            Assert.That(s.Confirmation, Is.Null);
        }

        [Test]
        public async Task Done_before_anything_has_solved_is_a_conflict() {
            // Every solve fails: the readout has a target but no measured angle, before and after it errors out.
            var solver = new ScriptedSolver(new double?[] { null });
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmAsync(CancellationToken.None));
            var s = await WaitForState(svc, "error");
            Assert.That(s.State, Is.EqualTo("error"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmAsync(CancellationToken.None));
            Assert.That(svc.GetStatus().State, Is.EqualTo("error"), "a refused Done leaves the readout as it was");
        }

        [Test]
        public async Task A_start_during_the_confirmation_is_refused_and_stop_cancels_it() {
            var solver = new SlowSolver();
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None);
            await WaitForSeq(svc, 1);
            await svc.ConfirmAsync(CancellationToken.None);
            Assert.That(svc.GetStatus().State, Is.EqualTo("confirming"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new RotationAssistStartRequestDto(10), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConfirmAsync(CancellationToken.None));
            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
            Assert.That(svc.GetStatus().Confirmation, Is.Null);
        }

        /// <summary>Solves 10° quickly when binned; a 1×1 solve hangs until cancelled (a slow full frame).</summary>
        private sealed class SlowSolver : IPositionAngleSolver {
            public int MaxBinning => 4;
            public async Task<RotationSolve?> SolvePositionAngleAsync(double exposureSeconds, int binning, CancellationToken ct) {
                if (binning == 1) {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                } else {
                    await Task.Delay(5, ct);
                }
                var frame = new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UtcNow);
                return new RotationSolve(10, 0, 0, 2.0, false, frame);
            }
        }

        [Test]
        public async Task An_unreadable_tolerance_falls_back_to_one_degree() {
            using var svc = new RotationAssistService(new ScriptedSolver(10), () => throw new InvalidOperationException("no profile"));
            Assert.That(svc.GetStatus().ToleranceDeg, Is.EqualTo(1.0));
            await Task.CompletedTask;
        }
    }
}
