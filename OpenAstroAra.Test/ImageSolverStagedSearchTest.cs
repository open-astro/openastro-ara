using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1136 — the staged search between a failed hinted solve and the blind failover: the last good
    /// solve (when outside the hinted radius), scale search at the hinted radius (ASTAP only), 30°, 90°,
    /// then blind. Each stage is one solver call with a recorded parameter; the solver is a recorder
    /// that fails until a chosen stage.
    /// </summary>
    [TestFixture]
    [NonParallelizable] // SolveMemory is process-wide: any ImageSolver success elsewhere adds a stage here
    public class ImageSolverStagedSearchTest {

        private class RecordingSolver : IPlateSolver {
            public readonly List<PlateSolveParameter> Calls = new();
            public int SucceedOnCall = int.MaxValue;
            public double Pixscale = 1.46;

            public Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus>? progress, CancellationToken canceltoken) {
                Calls.Add(parameter.Clone());
                var ok = Calls.Count >= SucceedOnCall;
                return Task.FromResult(new PlateSolveResult {
                    Success = ok,
                    Coordinates = ok ? parameter.Coordinates ?? new Coordinates(Angle.ByDegree(0), Angle.ByDegree(0), Epoch.J2000) : null,
                    Pixscale = Pixscale,
                });
            }
        }

        /// <summary>ASTAP-like: honours SearchScale, so the staged search runs its scale stage.</summary>
        private sealed class ScaleSearchingSolver : RecordingSolver, IScaleSearchSolver {
        }

        private static PlateSolveParameter Hinted(double radius = 15, bool blind = true) => new() {
            FocalLength = 530,
            PixelSize = 3.76,
            SearchRadius = radius,
            DownSampleFactor = 0,
            MaxObjects = 500,
            BlindFailoverEnabled = blind,
            Timeout = TimeSpan.FromSeconds(60),
            Coordinates = new Coordinates(Angle.ByDegree(100), Angle.ByDegree(20), Epoch.J2000),
        };

        [SetUp]
        public void ClearMemory() => SolveMemory.Clear();

        [Test]
        public async Task Hint_then_scale_then_wider_radii_then_blind() {
            var near = new ScaleSearchingSolver();
            var blind = new RecordingSolver();
            var sut = new ImageSolver(near, blind);

            var result = await sut.Solve(Mock.Of<IImageData>(), Hinted(), null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(Joined(near.Calls.Select(Describe)), Is.EqualTo(
                "hint r=15 scale=known t=60 | hint r=15 scale=searched t=60 | hint r=30 scale=known t=60 | hint r=90 scale=known t=120"));
            Assert.That(Joined(blind.Calls.Select(Describe)), Is.EqualTo("blind r=15 scale=known t=300"));
        }

        [Test]
        public async Task Only_the_last_stage_may_post_the_solvers_own_failure_notice() {
            // CM4 rig 2026-10-19: every failed stage posted "ASTAP - Plate solve failed." to the inbox
            // although the next stage solved. Bot round 2: the last stage keeps the solver's specific
            // notice (a missing star database must still read as one).
            var near = new ScaleSearchingSolver();
            var blind = new RecordingSolver();
            var sut = new ImageSolver(near, blind);
            await sut.Solve(Mock.Of<IImageData>(), Hinted(), null, CancellationToken.None);
            Assert.That(near.Calls.All(c => c.DisableNotifications), Is.True, "every non-final stage is quiet");
            Assert.That(near.Calls.All(c => !c.KeepFailedFrame), Is.True, "and drops its failed frame instead of copying it to disk");
            Assert.That(blind.Calls.Single().DisableNotifications, Is.False, "the final (blind) stage keeps the caller's setting");
            Assert.That(blind.Calls.Single().KeepFailedFrame, Is.True, "and keeps the final failure's frame");
        }

        [Test]
        public async Task A_solve_with_no_further_stages_keeps_the_callers_notification_setting() {
            var only = new RecordingSolver();
            var p = Hinted(blind: false);
            p.StagedSearchEnabled = false;
            await new ImageSolver(only, new RecordingSolver()).Solve(Mock.Of<IImageData>(), p, null, CancellationToken.None);
            Assert.That(only.Calls.Single().DisableNotifications, Is.False);
            var blindOnly = new RecordingSolver();
            p.Coordinates = null;
            await new ImageSolver(new RecordingSolver(), blindOnly).Solve(Mock.Of<IImageData>(), p, null, CancellationToken.None);
            Assert.That(blindOnly.Calls.Single().DisableNotifications, Is.False);
        }

        [Test]
        public async Task A_hinted_success_runs_one_stage_and_is_remembered() {
            var near = new RecordingSolver { SucceedOnCall = 1 };
            var blind = new RecordingSolver();
            var sut = new ImageSolver(near, blind);

            var result = await sut.Solve(Mock.Of<IImageData>(), Hinted(), null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(near.Calls, Has.Count.EqualTo(1));
            Assert.That(blind.Calls, Is.Empty);
            Assert.That(SolveMemory.Recall(), Is.Not.Null);
            Assert.That(SolveMemory.Recall()!.RADegrees, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public async Task The_last_good_solve_is_tried_when_it_lies_outside_the_hinted_radius() {
            SolveMemory.Remember(new Coordinates(Angle.ByDegree(140), Angle.ByDegree(20), Epoch.J2000)); // ~37° away
            var near = new ScaleSearchingSolver { SucceedOnCall = 2 };
            var sut = new ImageSolver(near, new RecordingSolver());

            var result = await sut.Solve(Mock.Of<IImageData>(), Hinted(), null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(near.Calls, Has.Count.EqualTo(2));
            Assert.That(near.Calls[1].Coordinates!.RADegrees, Is.EqualTo(140).Within(1e-6), "the remembered position is tried right after the hint, before the scale search");
            Assert.That(near.Calls[1].SearchRadius, Is.EqualTo(15));
            Assert.That(near.Calls[1].SearchScale, Is.False);
        }

        [Test]
        public void A_remembered_position_outside_the_hinted_radius_comes_before_the_scale_search() {
            SolveMemory.Remember(new Coordinates(Angle.ByDegree(140), Angle.ByDegree(20), Epoch.J2000)); // ~37° away
            Assert.That(Names(Hinted()), Is.EqualTo("last good solve | scale search | radius 30° | radius 90° | blind"));
        }

        [Test]
        public void A_remembered_position_inside_the_hinted_radius_adds_no_stage() {
            SolveMemory.Remember(new Coordinates(Angle.ByDegree(105), Angle.ByDegree(20), Epoch.J2000)); // ~5° away
            Assert.That(Names(Hinted()), Is.EqualTo("scale search | radius 30° | radius 90° | blind"));
        }

        [Test]
        public void A_stale_memory_is_ignored() {
            SolveMemory.UtcNow = () => new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
            try {
                SolveMemory.Remember(new Coordinates(Angle.ByDegree(140), Angle.ByDegree(20), Epoch.J2000));
                SolveMemory.UtcNow = () => new DateTime(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc);
                Assert.That(SolveMemory.Recall(), Is.Null);
            } finally {
                SolveMemory.UtcNow = () => DateTime.UtcNow;
            }
        }

        [Test]
        public void Widened_stages_skip_radii_no_wider_than_the_hint() {
            Assert.That(Names(Hinted(radius: 45)), Is.EqualTo("scale search | radius 90° | blind"));
            Assert.That(Names(Hinted(radius: 120)), Is.EqualTo("scale search | blind"));
        }

        [Test]
        public void No_blind_failover_ends_at_the_widest_radius() {
            Assert.That(Names(Hinted(blind: false)), Is.EqualTo("scale search | radius 30° | radius 90°"));
        }

        [Test]
        public void Staged_search_off_is_the_legacy_hint_then_blind() {
            var p = Hinted();
            p.StagedSearchEnabled = false;
            Assert.That(Names(p), Is.EqualTo("blind"));
            p.BlindFailoverEnabled = false;
            Assert.That(ImageSolver.Stages(p, scaleSearch: true), Is.Empty);
        }

        [Test]
        public void A_solver_that_cannot_search_the_scale_skips_that_stage() {
            // Bot round 1 — astrometry.net / PlateSolve2 ignore SearchScale, so the stage would re-run the
            // identical hinted solve and burn a timeout.
            Assert.That(Names(Hinted(), scaleSearch: false), Is.EqualTo("radius 30° | radius 90° | blind"));
        }

        [Test]
        public async Task A_plain_solver_never_sees_a_scale_search() {
            var near = new RecordingSolver();
            var sut = new ImageSolver(near, new RecordingSolver());
            await sut.Solve(Mock.Of<IImageData>(), Hinted(), null, CancellationToken.None);
            Assert.That(near.Calls.All(c => !c.SearchScale), Is.True);
            Assert.That(near.Calls, Has.Count.EqualTo(3), "hint, 30°, 90°");
        }

        [Test]
        public void A_blind_parameter_has_no_stages() {
            var p = Hinted();
            p.Coordinates = null;
            Assert.That(ImageSolver.Stages(p, scaleSearch: true), Is.Empty);
        }

        [Test]
        public void No_timeout_in_the_parameter_leaves_the_stages_without_one() {
            var p = Hinted();
            p.Timeout = null;
            Assert.That(ImageSolver.Stages(p, scaleSearch: true).All(s => s.Parameter.Timeout is null), Is.True);
        }

        private static string Joined(IEnumerable<string> items) => string.Join(" | ", items);

        private static string Names(PlateSolveParameter p, bool scaleSearch = true) => Joined(ImageSolver.Stages(p, scaleSearch).Select(s => s.Name));

        private static string Describe(PlateSolveParameter p) =>
            $"{(p.Coordinates is null ? "blind" : "hint")} r={p.SearchRadius} scale={(p.SearchScale ? "searched" : "known")} t={p.Timeout?.TotalSeconds:0}";
    }
}
