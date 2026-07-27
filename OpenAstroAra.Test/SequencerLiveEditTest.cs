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
                CancellationToken.None);

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
                new SequenceRunItemRemoveRequestDto(Path: [1]), CancellationToken.None);

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
                new SequenceRunItemRemoveRequestDto(Path: [0]), CancellationToken.None);

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
                CancellationToken.None);

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
                new SequenceRunItemMoveRequestDto(Path: [2], NewIndex: 0), CancellationToken.None);
            Assert.That(above.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidPath),
                "the slot of the running item is locked");

            var legal = await svc.MoveRunItemAsync(id,
                new SequenceRunItemMoveRequestDto(Path: [2], NewIndex: 1), CancellationToken.None);
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
                CancellationToken.None);
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
                CancellationToken.None);

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
                new SequenceRunItemRemoveRequestDto(Path: [7]), CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.InvalidPath));
            await WaitForTerminalAsync(svc, id);
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
                CancellationToken.None);
            Assert.That(result.Outcome, Is.EqualTo(SequenceLiveEditOutcome.PersistFailed));
            await WaitForTerminalAsync(svc, id);
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
