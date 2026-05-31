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
    public class SequenceImportTest {

        private string _profileDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            Directory.CreateDirectory(Path.Combine(_profileDir, "sequences", "imported"));
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch { }
        }

        private static (Mock<ISequenceService> mock, Func<SequenceCreateRequestDto?> captured) NewSequenceServiceMock() {
            var mock = new Mock<ISequenceService>();
            SequenceCreateRequestDto? captured = null;
            mock.Setup(s => s.CreateAsync(It.IsAny<SequenceCreateRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<SequenceCreateRequestDto, string?, CancellationToken>((req, _, _) => captured = req)
                .ReturnsAsync((SequenceCreateRequestDto req, string? _, CancellationToken _) =>
                    new SequenceDto(Guid.NewGuid(), req.Name, req.Description,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, req.Body, req.TemplateOrigin));
            return (mock, () => captured);
        }

        [Test]
        public async Task ImportAsync_backfills_schemaVersion_when_missing() {
            var (sequenceService, captured) = NewSequenceServiceMock();
            var svc = new PlaceholderSequenceImportService(sequenceService.Object, _profileDir);

            var nina = JsonDocument.Parse("""{ "version": "nina-3.x", "items": [] }""").RootElement;
            var result = await svc.ImportAsync(
                new SequenceImportRequestDto("M31 Tonight", nina, TreatWarningsAsErrors: false),
                CancellationToken.None);

            Assert.That(captured(), Is.Not.Null);
            var bodyText = captured()!.Body.GetRawText();
            Assert.That(bodyText, Does.Contain("\"schemaVersion\":\"openastroara-sequence-v1\""));
            Assert.That(bodyText, Does.Contain("\"version\":\"nina-3.x\""));  // original fields preserved
            Assert.That(result.Warnings, Has.One.Contains("schemaVersion was missing"));
        }

        [Test]
        public async Task ImportAsync_does_not_double_add_schemaVersion_when_present() {
            var (sequenceService, captured) = NewSequenceServiceMock();
            var svc = new PlaceholderSequenceImportService(sequenceService.Object, _profileDir);

            var alreadyV1 = JsonDocument.Parse("""
                { "schemaVersion": "openastroara-sequence-v1", "items": [] }
                """).RootElement;
            var result = await svc.ImportAsync(
                new SequenceImportRequestDto("Test", alreadyV1, false),
                CancellationToken.None);

            Assert.That(result.Warnings, Is.Empty.Or.None.Contains("schemaVersion"));
            // Body should be unchanged.
            // Original was pretty-printed; preserved through pass-through.
            var roundTrip = captured()!.Body.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", "");
            Assert.That(roundTrip, Does.Contain("\"schemaVersion\":\"openastroara-sequence-v1\""));
        }

        [Test]
        public async Task ImportAsync_persists_raw_upload_under_imported_dated_dir() {
            var (sequenceService, _) = NewSequenceServiceMock();
            var svc = new PlaceholderSequenceImportService(sequenceService.Object, _profileDir);

            var nina = JsonDocument.Parse("""{ "items": [] }""").RootElement;
            await svc.ImportAsync(
                new SequenceImportRequestDto("MyTarget!?", nina, false),
                CancellationToken.None);

            var importedDir = Path.Combine(_profileDir, "sequences", "imported");
            var todayBucket = $"from-nina-{DateTime.UtcNow:yyyy-MM-dd}";
            var bucketPath = Path.Combine(importedDir, todayBucket);
            Assert.That(Directory.Exists(bucketPath), Is.True, $"Expected {bucketPath} to exist");
            var files = Directory.GetFiles(bucketPath, "*.json");
            Assert.That(files, Has.Length.EqualTo(1));
            // Filename sanitized: "MyTarget!?" → "MyTarget_" with trailing -HHmmss + .json.
            Assert.That(Path.GetFileName(files[0]), Does.StartWith("MyTarget"));
            Assert.That(Path.GetFileName(files[0]), Does.EndWith(".json"));
        }
    }
}
