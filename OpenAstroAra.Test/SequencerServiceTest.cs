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

        private static SequencerService BuildService(Guid id, JsonElement? body) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var fake = new FakeSequenceService(id, body);
            return new SequencerService(deserializer, ws: null, sequencesResolver: () => fake, checkpoint: null);
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
        public async Task Missing_body_fails_the_run() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, body: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Failed));
        }

        [Test]
        public async Task GetRunState_is_null_before_any_start() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            Assert.That(await svc.GetRunStateAsync(id, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Abort_returns_accepted() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            var op = await svc.AbortAsync(id, null, CancellationToken.None);
            Assert.That(op, Is.Not.Null);
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
            public Task<SequenceShareDto> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
