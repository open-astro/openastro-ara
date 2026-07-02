#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38 — verifies the real <see cref="SequencerService"/> actually EXECUTES
    /// a deserialized sequence body through NINA's inherited sequencer (the first
    /// time a sequence runs, vs. the prior placeholder's mock <c>Task.Delay</c>
    /// loop). No-equipment instructions (Annotation, WaitForTimeSpan) run for
    /// real against the headless stub set.
    /// </summary>
    [TestFixture]
    public class SequencerServiceTest {

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        /// <summary>Serialize a SequentialContainer (populated by <paramref name="populate"/>) to a body JsonElement.</summary>
        private static JsonElement BuildBody(Action<SequentialContainer>? populate = null) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);
            var root = new SequentialContainer { Name = "Test sequence" };
            populate?.Invoke(root);
            var json = converter.Serialize(root);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static SequencerService BuildService(Guid id, JsonElement? body, IWsBroadcaster? ws = null) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var fake = new FakeSequenceService(id, body);
            return new SequencerService(deserializer, ws: ws, sequencesResolver: () => fake, checkpoint: null);
        }

        private static async Task<SequenceRunStateDto?> WaitForTerminalAsync(SequencerService svc, Guid id) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    return s;
                }
                await Task.Delay(20);
            }
            return await svc.GetRunStateAsync(id, CancellationToken.None);
        }

        private static async Task WaitForStateAsync(SequencerService svc, Guid id, SequenceRunState target) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State == target) return;
                await Task.Delay(20);
            }
        }

        [Test]
        public async Task Runs_empty_sequence_to_completion() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Runs_sequence_with_no_equipment_instructions_to_completion() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new Annotation { Name = "note" });
                c.Items.Add(new WaitForTimeSpan { Time = 0 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public void CountTerminalLeaves_counts_disabled_as_done() {
            // A DISABLED leaf never runs (SequentialStrategy only picks CREATED), so
            // it stays DISABLED — it must count as "done" or a successful run with a
            // disabled instruction would report instructions_completed < instructions_total.
            var leaves = new List<ISequenceItem> {
                new Annotation { Status = SequenceEntityStatus.FINISHED },
                new Annotation { Status = SequenceEntityStatus.DISABLED },
                new Annotation { Status = SequenceEntityStatus.SKIPPED },
                new Annotation { Status = SequenceEntityStatus.CREATED },
                new Annotation { Status = SequenceEntityStatus.RUNNING },
            };
            // FINISHED + DISABLED + SKIPPED = 3 done; CREATED + RUNNING are not.
            Assert.That(SequencerService.CountTerminalLeaves(leaves), Is.EqualTo(3));
            Assert.That(SequencerService.RunningLeafIndex(leaves), Is.EqualTo(4));
        }

        [Test]
        public async Task Completed_run_reports_all_instructions_done() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new Annotation { Name = "a" });
                c.Items.Add(new WaitForTimeSpan { Time = 0 });
                c.Items.Add(new Annotation { Name = "b" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(state.InstructionsTotal, Is.EqualTo(3), "3 leaf instructions");
            Assert.That(state.InstructionsCompleted, Is.EqualTo(3), "all completed");
            Assert.That(state.CurrentInstructionIndex, Is.Null, "nothing running at completion");
        }

        [Test]
        public async Task Missing_body_fails_the_run() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, body: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            // Body load + the Failed transition happen on the worker now, so wait
            // for the terminal state rather than reading immediately.
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Failed));
        }

        [Test]
        public async Task GetRunState_is_null_before_any_start() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            Assert.That(await svc.GetRunStateAsync(id, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Concurrent_starts_yield_a_single_coherent_run() {
            // Fire many simultaneous starts for the same id. The atomic slot
            // reservation must let exactly one win; the run resolves to a single
            // coherent terminal state with no exception/corruption from the race.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            var starts = Enumerable.Range(0, 8)
                .Select(_ => svc.StartAsync(id, StartReq, null, CancellationToken.None))
                .ToArray();
            await Task.WhenAll(starts);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Second_start_while_running_is_idempotent_same_run() {
            // A second start for an already-running id must NOT spawn a second
            // worker — the run id stays the same (the atomic reservation keeps the
            // live run).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            var firstRunId = (await svc.GetRunStateAsync(id, CancellationToken.None))!.RunId;
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var secondRunId = (await svc.GetRunStateAsync(id, CancellationToken.None))!.RunId;
            Assert.That(secondRunId, Is.EqualTo(firstRunId));
            await svc.AbortAsync(id, null, CancellationToken.None); // clean up the long run
        }

        [Test]
        public async Task Skip_current_skips_the_running_item_and_advances() {
            // §38 — the only item is a 30s wait; skipping the current item cancels it so the run
            // completes well within that wait (proving SkipCurrentRunningItems reaches the run).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            // Poll-skip until the run terminates rather than sleeping a fixed interval and
            // skipping once: until the 30s wait leaf has registered as a running item, skip
            // is a harmless no-op, so retrying absorbs scheduler starvation on a busy CI host
            // without ever waiting out the full 30s wait. Each skip is idempotent (no-op on a
            // terminal run), so over-calling is safe.
            SequenceRunStateDto? state = null;
            for (var i = 0; i < 250; i++) { // up to ~5s
                await svc.SkipAsync(id, null, CancellationToken.None);
                state = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (state is not null && state.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    break;
                }
                await Task.Delay(20);
            }
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed),
                "skipping the running 30s wait should let the run finish promptly");
        }

        [Test]
        public async Task Skip_on_unknown_run_is_an_accepted_noop() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            Assert.That(await svc.SkipAsync(id, null, CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task Host_shutdown_stops_live_runs() {
            // On daemon shutdown (IHostedService.StopAsync), in-flight runs must be
            // cancelled rather than abandoned mid-execution.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Abort_during_run_stops_the_sequence() {
            // A long wait keeps the run in Running long enough to abort it; the
            // run must end as Stopped (not mis-reported Completed — guards the
            // abort-state-vs-worker race).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running); // deterministic, not a fixed sleep
            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task AbortActiveRunsAsync_halts_the_running_sequence_and_counts_only_real_aborts() {
            // §29 hard-stop path: a running sequence is aborted and counted once; a re-entrant call (the disk
            // oscillating back to Critical) finds it already Aborting/terminal and counts nothing.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            var aborted = await svc.AbortActiveRunsAsync(CancellationToken.None);
            Assert.That(aborted, Is.EqualTo(1));

            var second = await svc.AbortActiveRunsAsync(CancellationToken.None);
            Assert.That(second, Is.EqualTo(0), "an already-aborting/terminal run is not re-counted");

            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Completed_run_emits_started_then_complete_WS_events() {
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
            Assert.That(ws.Events, Does.Contain("sequence.started"));
            Assert.That(ws.Events, Does.Contain("sequence.complete"));
            Assert.That(ws.Events, Does.Not.Contain("sequence.aborted"));
            Assert.That(ws.Events, Does.Not.Contain("sequence.stopped"));
        }

        [Test]
        public async Task Pause_suspends_at_the_instruction_boundary_and_resume_completes() {
            // §38 pause: the current instruction (a 2s wait) always finishes; the
            // run then suspends BETWEEN instructions, reports Paused, and emits
            // sequence.paused. Resume releases the gate, emits sequence.resumed,
            // and the run completes.
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new Annotation { Name = "after-pause" });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            await svc.PauseAsync(id, null, CancellationToken.None);
            // The 2s wait must finish first — Paused is reported only when the
            // engine actually suspends at the boundary, never on the mere request.
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);
            var paused = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(paused!.State, Is.EqualTo(SequenceRunState.Paused));
            Assert.That(paused.InstructionsCompleted, Is.EqualTo(1), "the in-flight wait ran to completion before the suspension");
            Assert.That(ws.Events, Does.Contain("sequence.paused"));

            // Suspended means suspended: the run must still be Paused after a beat.
            await Task.Delay(200);
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State, Is.EqualTo(SequenceRunState.Paused));

            await svc.ResumeAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(state.InstructionsCompleted, Is.EqualTo(2), "the post-pause instruction ran after resume");
            Assert.That(ws.Events, Does.Contain("sequence.resumed"));
        }

        [Test]
        public async Task Abort_wins_over_an_active_pause() {
            // Abort while suspended at the gate must cancel the wait and end the
            // run as Stopped (aborted event) — never leave it wedged in Paused.
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new WaitForTimeSpan { Time = 30 });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await svc.PauseAsync(id, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);

            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
            Assert.That(ws.Events, Does.Contain("sequence.aborted"));
        }

        [Test]
        public async Task Pause_racing_completion_is_a_harmless_accepted_noop() {
            // A pause request that never reaches another instruction boundary
            // (the run completes first) must not wedge or mislabel the run.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "only" })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await svc.PauseAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed).Or.EqualTo(SequenceRunState.Paused));
            if (state.State == SequenceRunState.Paused) {
                // The request landed before the last boundary — resume finishes it.
                await svc.ResumeAsync(id, null, CancellationToken.None);
                state = await WaitForTerminalAsync(svc, id);
                Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            }
        }

        [Test]
        public async Task Pause_and_resume_on_unknown_or_finished_runs_are_accepted_noops() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            // Unknown run — accepted, nothing to do.
            Assert.That(await svc.PauseAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That(await svc.ResumeAsync(id, null, CancellationToken.None), Is.Not.Null);
            // Finished run — still accepted no-ops.
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
            Assert.That(await svc.PauseAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That(await svc.ResumeAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Host_shutdown_stops_a_paused_run() {
            // Daemon shutdown must not hang on a suspended gate — the cancelled
            // token aborts the wait and the run ends Stopped.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new WaitForTimeSpan { Time = 30 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await svc.PauseAsync(id, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Abort_during_body_load_reports_stopped_not_failed() {
            // GetAsync blocks (honoring the token) so abort lands while the body is
            // still loading; the run must end Stopped, not Failed.
            var id = Guid.NewGuid();
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var fake = new DelayedSequenceService(id, BuildBody());
            var svc = new SequencerService(deserializer, ws: null, sequencesResolver: () => fake, checkpoint: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            // The run is registered synchronously by StartAsync, and the cancelled
            // token makes the blocking GetAsync throw whenever the worker reaches it
            // — so aborting now is deterministic, no fixed sleep needed to hit a window.
            Assert.That(await svc.GetRunStateAsync(id, CancellationToken.None), Is.Not.Null);
            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        /// <summary>ISequenceService whose GetAsync blocks (honoring the token) to exercise abort-during-load.</summary>
        private sealed class DelayedSequenceService : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement _body;
            public DelayedSequenceService(Guid id, JsonElement body) { _id = id; _body = body; }

            public async Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // cancelled by abort -> OperationCanceledException
                if (id != _id) return null;
                return new SequenceDto(id, "Test", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, _body, null);
            }

            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }

        /// <summary>Records the event types published, to assert the WS lifecycle.</summary>
        private sealed class RecordingWsBroadcaster : IWsBroadcaster {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();
            public System.Collections.Generic.IReadOnlyCollection<string> Events => _events;
            public long CurrentSequence => _events.Count;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                _events.Enqueue(eventType);
                return Task.CompletedTask;
            }
        }

        /// <summary>Minimal ISequenceService whose GetAsync returns the test body; the rest are unused.</summary>
        private sealed class FakeSequenceService : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement? _body;
            public FakeSequenceService(Guid id, JsonElement? body) { _id = id; _body = body; }

            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
                if (id != _id || _body is null) return Task.FromResult<SequenceDto?>(null);
                return Task.FromResult<SequenceDto?>(new SequenceDto(
                    Id: id, Name: "Test", Description: null,
                    CreatedUtc: DateTimeOffset.UnixEpoch, ModifiedUtc: DateTimeOffset.UnixEpoch,
                    Body: _body.Value, TemplateOrigin: null));
            }

            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
