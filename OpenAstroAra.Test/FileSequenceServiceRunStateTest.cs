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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class FileSequenceServiceRunStateTest {

        private string _profileDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-seqstate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (System.IO.IOException) { } catch (System.UnauthorizedAccessException) { }
        }

        private static SequenceCreateRequestDto BodyRequest(string name) {
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "items": [
                        { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer" }
                    ]
                }
                """).RootElement;
            return new SequenceCreateRequestDto(
                Name: name, Description: null, Body: body, TemplateOrigin: null);
        }

        [Test]
        public async Task ListAsync_CurrentRunState_is_null_without_sequencer() {
            var svc = new FileSequenceService(_profileDir);
            await svc.CreateAsync(BodyRequest("idle"), null, CancellationToken.None);
            var page = await svc.ListAsync(50, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].CurrentRunState, Is.Null);
        }

        [Test]
        public async Task ListAsync_CurrentRunState_populated_when_sequencer_reports_running() {
            var sequencer = new Mock<ISequencerService>();
            // Set up a known id whose state will be Running.
            var runningSequenceId = Guid.NewGuid();
            sequencer
                .Setup(s => s.GetRunStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, CancellationToken>((id, _) => {
                    if (id == runningSequenceId) {
                        return Task.FromResult<SequenceRunStateDto?>(new SequenceRunStateDto(
                            SequenceId: id,
                            RunId: Guid.NewGuid(),
                            State: SequenceRunState.Running,
                            CurrentInstructionIndex: 1,
                            CurrentTargetName: null,
                            StartedUtc: DateTimeOffset.UtcNow,
                            CompletedUtc: null,
                            InstructionsCompleted: 0,
                            InstructionsTotal: 5,
                            CurrentInstructionDescription: null));
                    }
                    return Task.FromResult<SequenceRunStateDto?>(null);
                });

            var svc = new FileSequenceService(_profileDir, sequencer.Object);
            // Create + then mutate the saved file's id to match runningSequenceId so the
            // sequencer mock matches. Easier: create via service then test list with
            // any id (the sequencer mock above returns null for non-matching ids).
            var dto = await svc.CreateAsync(BodyRequest("not-the-one"), null, CancellationToken.None);
            // Move the file to runningSequenceId so the list picks it up under that id.
            var libDir = Path.Combine(_profileDir, "sequences", "library");
            var src = Path.Combine(libDir, $"{dto.Id:D}.json");
            var renamed = Path.Combine(libDir, $"{runningSequenceId:D}.json");
            var json = (await File.ReadAllTextAsync(src)).Replace(dto.Id.ToString("D"), runningSequenceId.ToString("D"), StringComparison.Ordinal);
            await File.WriteAllTextAsync(renamed, json);
            File.Delete(src);

            var page = await svc.ListAsync(50, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Id, Is.EqualTo(runningSequenceId));
            Assert.That(page.Items[0].CurrentRunState, Is.EqualTo(SequenceRunState.Running));
        }

        [Test]
        public async Task ListAsync_CurrentRunState_null_when_sequencer_says_no_run() {
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.GetRunStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SequenceRunStateDto?)null);

            var svc = new FileSequenceService(_profileDir, sequencer.Object);
            await svc.CreateAsync(BodyRequest("idle"), null, CancellationToken.None);

            var page = await svc.ListAsync(50, null, CancellationToken.None);
            Assert.That(page.Items[0].CurrentRunState, Is.Null);
        }
    }
}