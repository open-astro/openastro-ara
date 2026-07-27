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
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38.10 — resume refinement: a user resume of a run still paused on the
    /// SAME target plate-solves + re-centers (and optionally refocuses) BEFORE
    /// the pause gate releases; every fault path still releases the gate.
    /// </summary>
    [TestFixture]
    public class SequencerResumeRefinementTest {

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        /// <summary>Root holding one DSO target block with two slow leaves — pause
        /// during the first, and the boundary suspension leaves the DSO RUNNING.</summary>
        private static JsonElement DsoBody(HeadlessSequencerFactory factory) {
            var converter = new SequenceJsonConverter(factory);
            var root = new SequentialContainer { Name = "resume-test root" };
            var dso = new DeepSkyObjectContainer(new HeadlessProfileService()) { Name = "Target A" };
            dso.Items.Add(new WaitForTimeSpan { Time = 1 });
            dso.Items.Add(new WaitForTimeSpan { Time = 1 });
            root.Items.Add(dso);
            using var doc = JsonDocument.Parse(converter.Serialize(root));
            return doc.RootElement.Clone();
        }

        private sealed class FakeAutofocus : IAutofocusExecutor {
            public int Runs;
            public bool Converge = true;
            public Task<bool> RunAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Interlocked.Increment(ref Runs);
                return Task.FromResult(Converge);
            }
        }

        private static (SequencerService Svc, Mock<ICenteringService> Centering, FakeAutofocus Af) BuildService(
                Guid id, Func<CancellationToken, Task<PlateSolveResult>>? centerBehaviour = null) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var body = DsoBody(factory);
            var store = new FakeStore(id, body);
            var centering = new Mock<ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(
                    It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                    It.IsAny<IProgress<PlateSolveProgress>?>(),
                    It.IsAny<IProgress<ApplicationStatus>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<OpenAstroAra.Astrometry.Coordinates, IProgress<PlateSolveProgress>?, IProgress<ApplicationStatus>?, CancellationToken>(
                    (_, _, _, ct) => centerBehaviour is null
                        ? Task.FromResult(new PlateSolveResult { Success = true })
                        : centerBehaviour(ct));
            var af = new FakeAutofocus();
            var svc = new SequencerService(new SequenceBodyDeserializer(factory),
                ws: null, sequencesResolver: () => store, checkpoint: null,
                centeringResolver: () => centering.Object,
                autofocusResolver: () => af);
            return (svc, centering, af);
        }

        private static async Task PauseAtBoundaryAsync(SequencerService svc, Guid id) {
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            for (var i = 0; i < 250; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s?.State == SequenceRunState.Running) break;
                await Task.Delay(20);
            }
            await svc.PauseAsync(id, null, CancellationToken.None);
            for (var i = 0; i < 250; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s?.State == SequenceRunState.Paused) return;
                await Task.Delay(20);
            }
            Assert.Fail("the engine never suspended");
        }

        private static async Task<SequenceRunStateDto?> WaitForTerminalAsync(SequencerService svc, Guid id) {
            for (var i = 0; i < 500; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    return s;
                }
                await Task.Delay(20);
            }
            return await svc.GetRunStateAsync(id, CancellationToken.None);
        }

        [Test]
        public async Task Default_resume_recenters_the_paused_target_then_completes() {
            var id = Guid.NewGuid();
            var (svc, centering, af) = BuildService(id);
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, null, null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            centering.Verify(c => c.CenterOnTarget(
                It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                It.IsAny<IProgress<PlateSolveProgress>?>(),
                It.IsAny<IProgress<ApplicationStatus>?>(),
                It.IsAny<CancellationToken>()), Times.Once,
                "an absent body defaults to re-center on a same-target resume");
            Assert.That(af.Runs, Is.Zero, "refocus defaults to off");
        }

        [Test]
        public async Task Refocus_option_runs_autofocus_before_imaging_continues() {
            var id = Guid.NewGuid();
            var (svc, centering, af) = BuildService(id);
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, new SequenceResumeRequestDto(Recenter: true, Refocus: true), null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(af.Runs, Is.EqualTo(1), "the requested autofocus ran");
        }

        [Test]
        public async Task Declining_recenter_releases_the_gate_without_touching_the_solver() {
            var id = Guid.NewGuid();
            var (svc, centering, af) = BuildService(id);
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, new SequenceResumeRequestDto(Recenter: false, Refocus: false), null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            centering.VerifyNoOtherCalls();
            Assert.That(af.Runs, Is.Zero);
        }

        [Test]
        public async Task A_centering_fault_still_releases_the_gate_and_completes() {
            var id = Guid.NewGuid();
            var (svc, _, _) = BuildService(id,
                _ => throw new InvalidOperationException("solver exploded"));
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, null, null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed),
                "a solve fault degrades to resume-with-warning, never a stuck gate");
        }

        [Test]
        public async Task An_unconfigured_solver_skips_gracefully() {
            var id = Guid.NewGuid();
            var (svc, _, _) = BuildService(id,
                _ => throw new PlateSolverConfigurationException("no solver configured"));
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, null, null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task A_throwing_resolver_probe_degrades_to_a_plain_resume() {
            // Review #873 r3 — the probe runs on the request path between the
            // resume CAS and the gate release: a factory fault must degrade to
            // plain-resume, never 500 the request with the gate never released.
            var id = Guid.NewGuid();
            var factory = HeadlessSequencerFactory.WithDefaults();
            var store = new FakeStore(id, DsoBody(factory));
            var svc = new SequencerService(new SequenceBodyDeserializer(factory),
                ws: null, sequencesResolver: () => store, checkpoint: null,
                centeringResolver: () => throw new InvalidOperationException("provider disposed"),
                autofocusResolver: () => throw new InvalidOperationException("provider disposed"));
            await PauseAtBoundaryAsync(svc, id);

            Assert.That(await svc.ResumeAsync(id, null, null, CancellationToken.None), Is.Not.Null,
                "the resume request itself must not fault");

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed),
                "the gate was released and the run finished without refinement");
        }

        [Test]
        public async Task Safety_auto_resume_cannot_release_the_gate_mid_refinement() {
            // Review #873 — §35's ResumeRunsAsync must not yank the gate open
            // while the user-resume refinement still holds it: imaging would
            // continue mid-plate-solve. The refinement's own release (after the
            // solve completes) is the ONE release.
            var id = Guid.NewGuid();
            using var solveStarted = new SemaphoreSlim(0);
            using var solveHold = new SemaphoreSlim(0);
            var (svc, _, _) = BuildService(id, async ct => {
                solveStarted.Release();
                await solveHold.WaitAsync(ct);
                return new PlateSolveResult { Success = true };
            });
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, null, null, CancellationToken.None);
            Assert.That(await solveStarted.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);

            // The safety engine's auto-resume lands mid-solve (state is Running).
            await svc.ResumeRunsAsync([id], CancellationToken.None);
            await Task.Delay(300);
            var during = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(during!.State, Is.EqualTo(SequenceRunState.Running));
            Assert.That(during.InstructionsCompleted, Is.EqualTo(1),
                "the engine must still be suspended at the boundary — the safety resume must not release the held gate");

            solveHold.Release();
            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Abort_during_the_recenter_still_ends_the_run() {
            var id = Guid.NewGuid();
            using var started = new SemaphoreSlim(0);
            var (svc, _, _) = BuildService(id, async ct => {
                started.Release();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new PlateSolveResult { Success = true };
            });
            await PauseAtBoundaryAsync(svc, id);

            await svc.ResumeAsync(id, null, null, CancellationToken.None);
            Assert.That(await started.WaitAsync(TimeSpan.FromSeconds(5)), Is.True, "the re-center began");
            await svc.AbortAsync(id, null, CancellationToken.None);

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        /// <summary>Minimal store: serves the body; live-edit persistence unused here.</summary>
        private sealed class FakeStore : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement _body;
            public FakeStore(Guid id, JsonElement body) { _id = id; _body = body; }
            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) =>
                Task.FromResult<SequenceDto?>(id == _id
                    ? new SequenceDto(id, "Test", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, _body, null)
                    : null);
            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto?> ReplaceRunBodyAsync(Guid id, JsonElement body, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
