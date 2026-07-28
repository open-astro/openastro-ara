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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38.10a — pins the engine semantics the client's generated target blocks
    /// rely on (review #876): a container WITH conditions LOOPS its child list
    /// while they all hold, so the horizon guard must ride WITH a one-iteration
    /// LoopCondition — run once, but still exit early when a condition drops.
    /// Runs through the real SequencerService harness; the loop count is
    /// observed via sequence.instruction_failed (one event per failure
    /// OCCURRENCE — a looping block re-fails its poison leaf every pass).
    /// </summary>
    [TestFixture]
    public class SequentialStrategyConditionSemanticsTest {

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        private sealed class RecordingWs : IWsBroadcaster {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();
            public System.Collections.Generic.IReadOnlyCollection<string> Events => _events;
            public long CurrentSequence => _events.Count;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                _events.Enqueue(eventType);
                return Task.CompletedTask;
            }
        }

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

        /// <summary>Root > conditioned block holding one always-failing leaf: every
        /// pass of the block emits exactly one sequence.instruction_failed.</summary>
        private static async Task<int> FailureCountForIterationsAsync(int iterations) {
            var id = Guid.NewGuid();
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);
            var root = new SequentialContainer { Name = "semantics root" };
            var block = new SequentialContainer { Name = "conditioned block" };
            block.Conditions.Add(new LoopCondition { Iterations = iterations });
            block.Items.Add(new ExternalScript { Name = "Poison", Script = "/definitely/not/a/real/command-xyz" });
            root.Items.Add(block);
            using var doc = JsonDocument.Parse(converter.Serialize(root));

            var ws = new RecordingWs();
            var store = new FakeStore(id, doc.RootElement.Clone());
            var svc = new SequencerService(new SequenceBodyDeserializer(factory), ws: ws, sequencesResolver: () => store, checkpoint: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            for (var i = 0; i < 500; i++) {
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) break;
                await Task.Delay(20);
            }
            return ws.Events.Count(e => e == "sequence.instruction_failed");
        }

        [Test]
        public async Task A_conditioned_block_repeats_while_its_condition_holds() {
            // The hazard review #876 caught: conditions turn a container into a
            // LOOP — a lone horizon condition would re-run the whole target
            // block (re-slew, re-focus, re-image) until the target sets.
            Assert.That(await FailureCountForIterationsAsync(2), Is.EqualTo(2),
                "two loop iterations re-ran the block's leaf twice");
        }

        [Test]
        public async Task LoopCondition_1_makes_the_conditioned_block_run_exactly_once() {
            // The generated target-block shape: LoopCondition(1) ANDed with the
            // horizon guard = one pass, early-exit still possible mid-pass.
            Assert.That(await FailureCountForIterationsAsync(1), Is.EqualTo(1),
                "a one-iteration conditioned block runs its children once");
        }
    }
}
