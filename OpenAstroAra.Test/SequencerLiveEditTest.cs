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
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38.9 — live mid-run sequence editing. Verifies the safety envelope
    /// (pending-only, after-current-only, teardown refusal), that an accepted
    /// add actually EXECUTES in the same run, that removes prevent execution,
    /// that progress totals re-base, and that the persisted body tracks the
    /// edited tree.
    /// </summary>
    [TestFixture]
    public class SequencerLiveEditTest {

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        private static JsonElement Serialize(SequentialContainer container) {
            var converter = new SequenceJsonConverter(HeadlessSequencerFactory.WithDefaults());
            using var doc = JsonDocument.Parse(converter.Serialize(container));
            return doc.RootElement.Clone();
        }

        private static JsonElement BuildBody(Action<SequentialContainer>? populate = null) {
            var root = new SequentialContainer { Name = "Live-edit test sequence" };
            populate?.Invoke(root);
            return Serialize(root);
        }

        /// <summary>A container fragment holding one annotation — the "target block" shape §38.9 adds.</summary>
        private static JsonElement Fragment(string name) {
            var block = new SequentialContainer { Name = name };
            block.Items.Add(new Annotation { Name = $"{name}-note" });
            return Serialize(block);
        }

        private static (SequencerService Svc, RecordingSequenceStore Store) BuildService(Guid id, JsonElement body) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var store = new RecordingSequenceStore(id, body);
            var svc = new SequencerService(new SequenceBodyDeserializer(factory), ws: null, sequencesResolver: () => store, checkpoint: null);
            return (svc, store);
        }

        private static async Task WaitForRunningAsync(SequencerService svc, Guid id) {
            for (var i = 0; i < 250; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s?.State == SequenceRunState.Running) return;
                await Task.Delay(20);
            }
            Assert.Fail("run never reached Running");
        }

        private static async Task<SequenceRunStateDto?> WaitForTerminalAsync(SequencerService svc, Guid id) {
            for (var i = 0; i < 500; i++) { // up to ~10s — slow-leaf tests hold the run open ~2s
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    return s;
                }
                await Task.Delay(20);
            }
            return await svc.GetRunStateAsync(id, CancellationToken.None);
        }

        [Test]
        public async Task Appended_block_joins_the_live_run_executes_and_rebases_totals() {
            var id = Guid.NewGuid();
            var (svc, store) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var before = await svc.GetRunStateAsync(id, CancellationToken.None);
            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: Fragment("late-target")),
                null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), result.Reason);
            Assert.That(result.Sequence, Is.Not.Null, "the updated stored dto rides the Applied result");
            Assert.That(store.ReplacedBody, Is.Not.Null, "the edit persisted the re-serialized live tree");
            Assert.That(store.ReplacedBody!.Value.GetRawText(), Does.Contain("late-target"));
            Assert.That(store.ReplacedBody!.Value.GetProperty("schemaVersion").GetString(),
                Is.EqualTo("openastroara-sequence-v1"), "the ARA schema marker is re-added on serialize");

            var after = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(after!.InstructionsTotal, Is.GreaterThan(before!.InstructionsTotal),
                "the progress denominator re-bases onto the edited plan");

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(terminal.InstructionsCompleted, Is.EqualTo(after.InstructionsTotal),
                "the appended leaf executed as part of the same run");
        }

        [Test]
        public async Task Removed_pending_item_never_executes() {
            var id = Guid.NewGuid();
            // The pending ExternalScript would FAIL if executed — its absence from
            // the failure feed proves the removal actually prevented execution.
            var (svc, store) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new ExternalScript { Name = "Poison", Script = "/definitely/not/a/real/command-xyz" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var result = await svc.RemoveRunItemAsync(id,
                new SequenceRunItemRemoveRequestDto(Path: [1]), null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), result.Reason);
            Assert.That(store.ReplacedBody!.Value.GetRawText(), Does.Not.Contain("command-xyz"));

            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(terminal.InstructionsTotal, Is.EqualTo(1), "only the slow leaf remains");
        }

        [Test]
        public async Task The_running_item_is_not_removable() {
            var id = Guid.NewGuid();
            var (svc, _) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new Annotation { Name = "later" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var result = await svc.RemoveRunItemAsync(id,
                new SequenceRunItemRemoveRequestDto(Path: [0]), null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.ItemAlreadyStarted));
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task Inserting_at_or_before_the_running_position_is_refused() {
            var id = Guid.NewGuid();
            var (svc, _) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new Annotation { Name = "later" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: 0, Item: Fragment("queue-jumper")),
                null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidPath));
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task Pending_items_reorder_but_not_above_the_running_floor() {
            var id = Guid.NewGuid();
            var (svc, store) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                // Named CONTAINERS as the reorder markers — container names serialize
                // into the body (bare item names don't), so the persisted order is assertable.
                c.Items.Add(new SequentialContainer { Name = "alpha" });
                c.Items.Add(new SequentialContainer { Name = "beta" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var above = await svc.MoveRunItemAsync(id,
                new SequenceRunItemMoveRequestDto(Path: [2], NewIndex: 0), null, CancellationToken.None);
            Assert.That(above.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidPath),
                "the slot of the running item is locked");

            var legal = await svc.MoveRunItemAsync(id,
                new SequenceRunItemMoveRequestDto(Path: [2], NewIndex: 1), null, CancellationToken.None);
            Assert.That(legal.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), legal.Reason);

            var raw = store.ReplacedBody!.Value.GetRawText();
            Assert.That(raw.IndexOf("beta", StringComparison.Ordinal),
                Is.LessThan(raw.IndexOf("alpha", StringComparison.Ordinal)),
                "the persisted body reflects the new order");
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task Edits_after_the_run_ends_are_refused() {
            var id = Guid.NewGuid();
            var (svc, _) = BuildService(id, BuildBody(c => {
                c.Items.Add(new Annotation { Name = "quick" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));

            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: Fragment("too-late")),
                null, CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.NoActiveRun));
        }

        [Test]
        public async Task A_malformed_fragment_is_refused_without_touching_the_run() {
            var id = Guid.NewGuid();
            var (svc, store) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            using var notAnObject = JsonDocument.Parse("42");
            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: notAnObject.RootElement.Clone()),
                null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidItem));
            Assert.That(store.ReplacedBody, Is.Null, "nothing persisted for a refused edit");
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task A_bad_path_is_refused_with_InvalidPath() {
            var id = Guid.NewGuid();
            var (svc, _) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var result = await svc.RemoveRunItemAsync(id,
                new SequenceRunItemRemoveRequestDto(Path: [7]), null, CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidPath));
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task A_retried_add_with_the_same_idempotency_key_applies_once() {
            var id = Guid.NewGuid();
            var (svc, _) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var request = new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: Fragment("retried-target"));
            var first = await svc.AddRunItemAsync(id, request, "retry-key-1", CancellationToken.None);
            var totalAfterFirst = (await svc.GetRunStateAsync(id, CancellationToken.None))!.InstructionsTotal;
            var second = await svc.AddRunItemAsync(id, request, "retry-key-1", CancellationToken.None);

            Assert.That(first.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), first.Reason);
            Assert.That(second.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), "the replay reports the original outcome");
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.InstructionsTotal,
                Is.EqualTo(totalAfterFirst), "the retry must not double-insert");
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task An_item_removed_during_a_pause_does_not_execute_on_resume() {
            // Review #871 r3 — the strategy picks `next` BEFORE awaiting the
            // pause gate, so a live remove during the pause detaches an item the
            // loop still holds. The strategy's resume re-validation must re-pick
            // instead of running the deleted (would-fail) item.
            var id = Guid.NewGuid();
            var ws = new RecordingWs();
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var store = new RecordingSequenceStore(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 1 });
                c.Items.Add(new ExternalScript { Name = "Poison", Script = "/definitely/not/a/real/command-xyz" });
                c.Items.Add(new Annotation { Name = "after" });
            }));
            var svc = new SequencerService(deserializer, ws: ws, sequencesResolver: () => store, checkpoint: null);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);
            // Arm the pause during the slow leaf; the engine suspends at the
            // boundary AFTER it — with the poison item already picked as `next`.
            await svc.PauseAsync(id, null, CancellationToken.None);
            for (var i = 0; i < 250; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s?.State == SequenceRunState.Paused) break;
                await Task.Delay(20);
            }
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(SequenceRunState.Paused), "the engine must actually suspend first");

            var removed = await svc.RemoveRunItemAsync(id,
                new SequenceRunItemRemoveRequestDto(Path: [1]), null, CancellationToken.None);
            Assert.That(removed.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), removed.Reason);

            await svc.ResumeAsync(id, null, CancellationToken.None);
            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(ws.Events, Does.Not.Contain("sequence.instruction_failed"),
                "the removed poison item must not have executed after resume");
            Assert.That(terminal.InstructionsTotal, Is.EqualTo(2), "slow leaf + annotation remain");
        }

        [Test]
        public async Task Appending_into_a_finished_sub_container_is_refused() {
            var id = Guid.NewGuid();
            // Block A (an annotation) completes immediately; the slow leaf then
            // holds the run open. A's strategy loop has exited — an accepted
            // append into it would sit CREATED forever, so it must refuse.
            var (svc, store) = BuildService(id, BuildBody(c => {
                var blockA = new SequentialContainer { Name = "block-a" };
                blockA.Items.Add(new Annotation { Name = "quick" });
                c.Items.Add(blockA);
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);
            // Ensure block A has fully finished (its leaf is terminal) before editing.
            for (var i = 0; i < 100; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if ((s?.InstructionsCompleted ?? 0) >= 1) break;
                await Task.Delay(20);
            }

            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [0], Index: null, Item: Fragment("too-late-block")),
                null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.ItemAlreadyStarted));
            Assert.That(store.ReplacedBody, Is.Null);
            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task A_fragment_whose_lifecycle_hook_throws_is_rolled_back_and_refused() {
            var id = Guid.NewGuid();
            var factory = HeadlessSequencerFactory.WithDefaults();
            factory.Containers.Add(new ThrowingInitContainer());
            var converter = new SequenceJsonConverter(factory);
            // The fragment CONTAINER's own hook throws (children only initialize
            // when their block executes, so the container is the surface the
            // live-add's hook call touches).
            using var fragmentDoc = JsonDocument.Parse(
                converter.Serialize(new ThrowingInitContainer { Name = "poisoned-block" }));

            var store = new RecordingSequenceStore(id, BuildBodyWith(factory, c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            var svc = new SequencerService(new SequenceBodyDeserializer(factory),
                ws: null, sequencesResolver: () => store, checkpoint: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);
            var totalBefore = (await svc.GetRunStateAsync(id, CancellationToken.None))!.InstructionsTotal;

            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: fragmentDoc.RootElement.Clone()),
                null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidItem),
                "a throwing lifecycle hook must refuse cleanly, not fault the request");
            Assert.That(store.ReplacedBody, Is.Null, "nothing persisted for the rolled-back splice");
            var after = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(after!.InstructionsTotal, Is.EqualTo(totalBefore),
                "the splice was rolled back — plan shape unchanged");
            var terminal = await WaitForTerminalAsync(svc, id);
            Assert.That(terminal!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task A_persist_failure_reports_PersistFailed() {
            var id = Guid.NewGuid();
            var (svc, store) = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
            }));
            store.FailReplacements = true;
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForRunningAsync(svc, id);

            var result = await svc.AddRunItemAsync(id,
                new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: Fragment("unsaved")),
                null, CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.PersistFailed));
            await WaitForTerminalAsync(svc, id);
        }

        /// <summary>Serialize with a specific factory (for bodies referencing custom test items).</summary>
        private static JsonElement BuildBodyWith(
                HeadlessSequencerFactory factory, Action<SequentialContainer> populate) {
            var converter = new SequenceJsonConverter(factory);
            var root = new SequentialContainer { Name = "Live-edit test sequence" };
            populate(root);
            using var doc = JsonDocument.Parse(converter.Serialize(root));
            return doc.RootElement.Clone();
        }

        /// <summary>Minimal WS recorder — event types only, for absence assertions.</summary>
        private sealed class RecordingWs : IWsBroadcaster {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();
            public IReadOnlyCollection<string> Events => _events;
            public long CurrentSequence => _events.Count;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                _events.Enqueue(eventType);
                return Task.CompletedTask;
            }
        }

        /// <summary>A container whose block-initialize hook throws — exercises the §38.9 splice rollback.</summary>
        private sealed class ThrowingInitContainer : SequentialContainer {
            public override void SequenceBlockInitialize() =>
                throw new InvalidOperationException("deliberate init failure");
            public override object Clone() => new ThrowingInitContainer { Name = Name };
        }

        /// <summary>ISequenceService fake that serves the body and records live-edit replacements.</summary>
        private sealed class RecordingSequenceStore : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement _body;
            public RecordingSequenceStore(Guid id, JsonElement body) { _id = id; _body = body; }

            public JsonElement? ReplacedBody { get; private set; }
            public bool FailReplacements { get; set; }

            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
                if (id != _id) return Task.FromResult<SequenceDto?>(null);
                return Task.FromResult<SequenceDto?>(new SequenceDto(
                    Id: id, Name: "Test", Description: null,
                    CreatedUtc: DateTimeOffset.UnixEpoch, ModifiedUtc: DateTimeOffset.UnixEpoch,
                    Body: _body, TemplateOrigin: null));
            }

            public Task<SequenceDto?> ReplaceRunBodyAsync(Guid id, JsonElement body, CancellationToken ct) {
                if (id != _id || FailReplacements) return Task.FromResult<SequenceDto?>(null);
                ReplacedBody = body;
                return Task.FromResult<SequenceDto?>(new SequenceDto(
                    Id: id, Name: "Test", Description: null,
                    CreatedUtc: DateTimeOffset.UnixEpoch, ModifiedUtc: DateTimeOffset.UnixEpoch,
                    Body: body, TemplateOrigin: null));
            }

            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
