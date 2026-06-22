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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // §70.5 share-export — the share carries the sequence body inline in Manifest
    // (the client writes it straight to a .araseq.json file), mirroring the
    // profile-share contract; an unknown id is a 404 (null), not a placeholder.
    [TestFixture]
    public class FileSequenceServiceShareExportTest {

        private string _profileDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-seqshare-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        public async Task ShareExportAsync_returns_inline_manifest_for_an_existing_sequence() {
            var svc = new FileSequenceService(_profileDir);
            var created = await svc.CreateAsync(BodyRequest("Andromeda night"), null, CancellationToken.None);

            var share = await svc.ShareExportAsync(created.Id, CancellationToken.None);

            Assert.That(share, Is.Not.Null);
            Assert.That(share!.SequenceId, Is.EqualTo(created.Id));
            Assert.That(share.SequenceName, Is.EqualTo("Andromeda night"));
            Assert.That(share.ShareFormat, Is.EqualTo("openastroara.v1"));
            // The manifest is the sequence body itself — the schemaVersion survives
            // the round-trip so the recipient can validate + import it.
            Assert.That(share.Manifest.GetProperty("schemaVersion").GetString(),
                Is.EqualTo("openastroara-sequence-v1"));
            // PayloadBytes is the inline manifest's UTF-8 size, and there is no
            // separate payload route (Manifest carries the share) — mirrors profiles.
            Assert.That(share.PayloadBytes,
                Is.EqualTo(System.Text.Encoding.UTF8.GetByteCount(share.Manifest.GetRawText())));
            Assert.That(share.PayloadBytes, Is.GreaterThan(0));
            Assert.That(share.DownloadUrl, Is.Null);
        }

        [Test]
        public async Task ShareExportAsync_returns_null_for_an_unknown_id() {
            var svc = new FileSequenceService(_profileDir);
            var share = await svc.ShareExportAsync(Guid.NewGuid(), CancellationToken.None);
            // Null → the endpoint maps to 404 (exporting a deleted/never-existing
            // sequence is a miss, not an empty placeholder share).
            Assert.That(share, Is.Null);
        }
    }
}
