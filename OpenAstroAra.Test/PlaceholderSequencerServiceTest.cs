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
using OpenAstroAra.Server.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38j-5 — verifies PlaceholderSequencerService reads the saved
    /// sequence body's instruction count instead of using the hardcoded
    /// mock default. Falls back to the mock when no body is found or
    /// when the resolver isn't wired.
    /// </summary>
    [TestFixture]
    public class PlaceholderSequencerServiceTest {

        private static SequenceDto BodyWithInstructions(Guid id, int count) {
            var items = string.Join(",",
                Enumerable.Range(0, count).Select(_ =>
                    "{\"$type\":\"NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer\"}"));
            var json = $$"""{ "schemaVersion": "openastroara-sequence-v1", "items": [{{items}}] }""";
            var body = JsonDocument.Parse(json).RootElement;
            return new SequenceDto(id, $"Seq-{count}", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, body, null);
        }

        [Test]
        public async Task StartAsync_uses_default_count_without_sequences_resolver() {
            var svc = new PlaceholderSequencerService();
            var id = Guid.NewGuid();
            await svc.StartAsync(id,
                new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false),
                idempotencyKey: null, CancellationToken.None);

            var state = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.InstructionsTotal, Is.EqualTo(5));  // DefaultMockInstructionCount
        }

        [Test]
        public async Task StartAsync_uses_instruction_count_from_stored_body() {
            var id = Guid.NewGuid();
            var sequences = new Mock<ISequenceService>();
            sequences.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BodyWithInstructions(id, 12));

            var svc = new PlaceholderSequencerService(ws: null, () => sequences.Object);

            await svc.StartAsync(id,
                new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false),
                idempotencyKey: null, CancellationToken.None);

            var state = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.InstructionsTotal, Is.EqualTo(12));
        }

        [Test]
        public async Task StartAsync_falls_back_to_default_when_stored_body_has_zero_instructions() {
            var id = Guid.NewGuid();
            var sequences = new Mock<ISequenceService>();
            // Body with 0 instructions — fall back to default mock count.
            sequences.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BodyWithInstructions(id, 0));

            var svc = new PlaceholderSequencerService(ws: null, () => sequences.Object);
            await svc.StartAsync(id,
                new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false),
                idempotencyKey: null, CancellationToken.None);

            var state = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(state!.InstructionsTotal, Is.EqualTo(5));
        }

        [Test]
        public void IsAbortableRun_excludes_terminal_and_already_aborting_states() {
            // The §29 abort-active-runs guard: only Idle/Starting/Running/Paused are abortable.
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Idle), Is.True);
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Starting), Is.True);
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Running), Is.True);
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Paused), Is.True);
            // Already aborting → not re-abortable (prevents the double count / double "Sequence halted" notify).
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Aborting), Is.False);
            // Terminal states.
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Stopped), Is.False);
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Completed), Is.False);
            Assert.That(SequencerService.IsAbortableRun(SequenceRunState.Failed), Is.False);
        }

        [Test]
        public async Task AbortActiveRunsAsync_returns_zero_with_no_active_runs() {
            var svc = new PlaceholderSequencerService();
            Assert.That(await svc.AbortActiveRunsAsync(CancellationToken.None), Is.EqualTo(0));
        }
    }
}