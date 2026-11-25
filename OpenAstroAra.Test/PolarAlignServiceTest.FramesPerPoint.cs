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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2082 part 2 — one measurement per Start, with the camera the profile picks (<c>polar_align.camera</c>: guide, or
    /// main) and several frames per point (<c>polar_align.frames_per_point</c>, 1–5, default 3): each point's frames,
    /// taken with the mount still, are averaged as unit vectors and their rms scatter is logged; a point scattered over
    /// 3″ is retaken once, and the result is flagged noisy when the retake is no better.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A guide-camera solver that answers at the bench mount's RA on the Dec-85° circle (like
        /// <see cref="MountFollowingSolver"/>), with <see cref="DecOffsetArcsec"/> added to the frames it names by id
        /// (the frame's file is <c>&lt;frame id&gt;.fits</c>, e.g. <c>seed-a-3</c>).</summary>
        private sealed class OffsetFrameSolver : IPolarAlignFrameSolver, IMountSky {
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
            public Dictionary<string, double> DecOffsetArcsec { get; } = new(StringComparer.Ordinal);
            /// <summary>Frames that do not solve (a cloud), by id.</summary>
            public HashSet<string> Unsolved { get; } = new(StringComparer.Ordinal);
            public List<string> Frames { get; } = new();
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                var frame = Path.GetFileNameWithoutExtension(fitsPath);
                lock (Frames) {
                    Frames.Add(frame);
                }
                if (Unsolved.Contains(frame)) {
                    return Task.FromResult(new PolarAlignSolveOutcome(false, 0.0, 0.0));
                }
                var offset = DecOffsetArcsec.TryGetValue(frame, out var arcsec) ? arcsec / 3600.0 : 0.0;
                return Task.FromResult(new PolarAlignSolveOutcome(true, Sky?.Invoke().RaDeg ?? 0.0, PoleMountDecDeg + offset));
            }
            public int Count(string point) {
                lock (Frames) {
                    return Frames.Count(f => f.StartsWith(point + "-", StringComparison.Ordinal));
                }
            }
        }

        private static InMemoryProfileStore StoreWith(Func<PolarAlignSettingsDto, PolarAlignSettingsDto> change) {
            var store = NewStore();
            store.PutPolarAlignSettings(change(store.GetPolarAlignSettings()));
            return store;
        }

        [Test]
        public async Task Three_frames_per_point_are_averaged_and_their_scatter_logged() {
            // ±1″ about the true pointing: the point's mean is exact, its rms scatter 0.82″ — under 3″, so no retake.
            var solver = new OffsetFrameSolver();
            foreach (var point in new[] { "seed-a", "seed-b", "seed-c" }) {
                solver.DecOffsetArcsec[point + "-1"] = 1.0;
                solver.DecOffsetArcsec[point + "-2"] = -1.0;
            }
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: StoreWith(s => s with { FramesPerPoint = 3 }), logger: logger);

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(new[] { solver.Count("seed-a"), solver.Count("seed-b"), solver.Count("seed-c") }, Is.All.EqualTo(3), "three frames at each point");
            var scatter = logger.Entries.Where(e => e.EventId == 4574).Select(e => e.Message).ToArray();
            Assert.That(scatter, Has.Length.EqualTo(3), "one scatter line per point");
            Assert.That(scatter, Has.All.Contains("3 solves averaged (0 failed)").And.All.Contains("rms scatter 0.82″"));
            // The point is the mean of its frames, not its first frame (85° + 1″ = 85.0003°).
            var seedA = logger.Entries.Single(e => e.EventId == 4527 && e.Message.Contains("seed-a", StringComparison.Ordinal)).Message;
            Assert.That(seedA, Does.Contain("Dec 85.0000°"));
            Assert.That(logger.Entries.Any(e => e.EventId == 4575), Is.False, "no point was retaken");
            Assert.That(status.MeasurementNoisy, Is.False);
        }

        [Test]
        public async Task A_10_arcsec_outlier_retakes_the_point_then_flags_the_result_noisy() {
            // #2082 acceptance: a scripted solver with one 10″ outlier in each set of seed-a's three frames (rms 4.7″).
            var solver = new OffsetFrameSolver { DecOffsetArcsec = { ["seed-a-3"] = 10.0, ["seed-a-6"] = 10.0 } };
            var logger = new CapturingLogger();
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), ws, store: StoreWith(s => s with { FramesPerPoint = 3 }), logger: logger);

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var progress = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignProgress).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"), "a noisy point does not stop the run");
            Assert.That(solver.Count("seed-a"), Is.EqualTo(6), "the point was retaken once: three frames more");
            Assert.That(solver.Count("seed-b"), Is.EqualTo(3), "a steady point is not retaken");
            Assert.That(logger.Entries.Count(e => e.EventId == 4575), Is.EqualTo(1), "one retake");
            Assert.That(status.MeasurementNoisy, Is.True, "still scattered after the retake: the result is noisy");
            Assert.That(progress.GetProperty("measurement_noisy").GetBoolean(), Is.True, "the live readout says so too");
        }

        [Test]
        public async Task An_outlier_in_the_first_set_only_is_retaken_and_the_result_is_not_noisy() {
            var solver = new OffsetFrameSolver { DecOffsetArcsec = { ["seed-a-3"] = 10.0 } };
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: StoreWith(s => s with { FramesPerPoint = 3 }));

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(solver.Count("seed-a"), Is.EqualTo(6), "retaken once");
            Assert.That(status.MeasurementNoisy, Is.False, "the retake was steady: its frames stand for the point");
        }

        [Test]
        public async Task A_retake_that_does_not_solve_keeps_the_first_set_and_flags_the_result_noisy() {
            // seed-b's first set is scattered (one 10″ outlier); a cloud then hides all three retake frames. The first
            // set's mean still stands for the point: the run goes on, flagged noisy, never seed_solve_failed.
            var solver = new OffsetFrameSolver { DecOffsetArcsec = { ["seed-b-3"] = 10.0 }, Unsolved = { "seed-b-4", "seed-b-5", "seed-b-6" } };
            var logger = new CapturingLogger();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), store: StoreWith(s => s with { FramesPerPoint = 3 }), logger: logger);

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"), "a retake with no solve does not fail the run");
            Assert.That(status.MeasurementNoisy, Is.True, "the point is the scattered first set: the result is noisy");
            Assert.That(solver.Count("seed-c"), Is.EqualTo(3), "the run went on to the next point");
            // The point is the first set's mean (85° + 10″/3 = 85.0009°), not a failure.
            var seedB = logger.Entries.Single(e => e.EventId == 4527 && e.Message.Contains("seed-b", StringComparison.Ordinal)).Message;
            Assert.That(seedB, Does.Contain("Dec 85.0009°"));
        }

        [Test]
        public async Task A_noisy_check_point_marks_the_verdict_noisy() {
            var solver = new OffsetFrameSolver { DecOffsetArcsec = { ["check-2-3"] = 10.0, ["check-2-6"] = 10.0 } };
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, NewPoleMount(), ws, store: StoreWith(s => s with { FramesPerPoint = 3 }));

            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);
            var checkedEvent = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignChecked).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.Noisy, Is.True);
            Assert.That(checkedEvent.GetProperty("noisy").GetBoolean(), Is.True);
        }

        [Test]
        public async Task Camera_main_runs_the_fine_measurement_and_its_live_loop_on_the_main_camera() {
            var fake = NewFake();
            var mainCamera = new FakeMainCamera();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new MountFollowingSolver(), NewPoleMount(), ws,
                store: StoreWith(s => s with { Camera = PolarAlignCameras.Main }), mainCamera: mainCamera);

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var mark = ws.Events.Count;
            await ws.WhenAsync(events => events.Skip(mark).Any(e => e.Type == WsEventCatalog.PolarAlignProgress)).WaitAsync(SignalTimeout).ConfigureAwait(false);
            // The guider's last frame is not this run's: no preview while the main camera measures.
            var preview = await svc.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(started.GetProperty("camera").GetString(), Is.EqualTo(PolarAlignCameras.Main));
            Assert.That((status.State, status.Stage, status.Camera), Is.EqualTo(("adjusting", PolarAlignStages.Fine, PolarAlignCameras.Main)));
            Assert.That(mainCamera.Calls, Is.GreaterThanOrEqualTo(4), "three seed frames and the live loop's");
            Assert.That(fake.Captures, Is.Empty, "the guide camera takes no frame");
            Assert.That(mainCamera.Exposures, Is.All.EqualTo(3.0), "the main camera's exposure");
            Assert.That(preview, Is.Null);
        }

        [Test]
        public async Task Camera_main_refuses_the_start_when_the_main_camera_is_not_connected() {
            var fake = NewFake();
            var mount = NewPoleMount();
            var mainCamera = new FakeMainCamera { Unavailable = "the main camera is not connected" };
            using var svc = NewService(fake, new MountFollowingSolver(), mount,
                store: StoreWith(s => s with { Camera = PolarAlignCameras.Main }), mainCamera: mainCamera);

            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None)).ConfigureAwait(false);
            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(r => r != 0)), Times.Never, "nothing moved");
            Assert.That(mainCamera.Calls, Is.Zero);
        }

        [Test]
        public async Task A_check_with_camera_main_solves_on_the_main_camera() {
            var fake = NewFake();
            var mainCamera = new FakeMainCamera();
            using var svc = NewService(fake, new MountFollowingSolver(), NewPoleMount(),
                store: StoreWith(s => s with { Camera = PolarAlignCameras.Main }), mainCamera: mainCamera);

            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(mainCamera.Calls, Is.EqualTo(3), "the check's three points on the main camera");
            Assert.That(fake.Captures, Is.Empty, "the guide camera takes no frame");
        }

    }
}
