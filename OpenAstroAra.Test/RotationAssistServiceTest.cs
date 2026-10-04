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
            public async Task<RotationSolve?> SolvePositionAngleAsync(CancellationToken ct) {
                await Task.Delay(5, ct);
                Solves++;
                if (_script.Count > 0) { _last = _script.Dequeue(); }
                if (_last is not { } pa) return null;
                var frame = new AnalysisFrame(new ushort[FrameSize * FrameSize], FrameSize, FrameSize, DateTimeOffset.UtcNow);
                return new RotationSolve(pa, RaDeg: 314.82, DecDeg: 44.53, PixelScaleArcsec: 2.3, Flipped: true, frame);
            }
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
            await svc.StartAsync(50, CancellationToken.None);
            var s = await WaitForSeq(svc, 1);

            Assert.That(s.State, Is.EqualTo("error"));
            Assert.That(s.Active, Is.False);
            Assert.That(s.ConsecutiveFailures, Is.EqualTo(RotationAssistService.MaxConsecutiveFailures));
            Assert.That(s.Error, Does.Contain("solves in a row failed"));

            await svc.StartAsync(50, CancellationToken.None);
            s = await WaitForSeq(svc, 1);
            Assert.That(s.State, Is.EqualTo("running"));
            Assert.That(s.Latest!.DeltaDeg, Is.EqualTo(0));
            await svc.StopAsync();
        }

        [Test]
        public async Task A_second_start_is_refused_while_running_and_stop_is_idempotent() {
            using var svc = new RotationAssistService(new ScriptedSolver(10), () => 1.0);
            await svc.StopAsync(); // nothing running — no-op
            await svc.StartAsync(10, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(20, CancellationToken.None));
            await svc.StopAsync();
            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task A_real_sized_frame_is_rendered_and_tagged_with_its_sample() {
            var solver = new ScriptedSolver(10) { FrameSize = 96 };
            using var svc = new RotationAssistService(solver, () => 1.0);
            await svc.StartAsync(10, CancellationToken.None);
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
            public Task<RotationSolve?> SolvePositionAngleAsync(CancellationToken ct) => Task.FromResult<RotationSolve?>(null);
        }

        [Test]
        public async Task A_solver_that_cannot_run_refuses_the_start_and_leaves_the_readout_idle() {
            using var svc = new RotationAssistService(new NotReadySolver(), () => 1.0);
            var ex = await Assert.ThrowsAsync<OpenAstroAra.Sequencer.SequenceItem.Rotator.RotationAssistNotReadyException>(
                () => svc.StartAsync(10, CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("no solver"));
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task An_unreadable_tolerance_falls_back_to_one_degree() {
            using var svc = new RotationAssistService(new ScriptedSolver(10), () => throw new InvalidOperationException("no profile"));
            Assert.That(svc.GetStatus().ToleranceDeg, Is.EqualTo(1.0));
            await Task.CompletedTask;
        }
    }
}
