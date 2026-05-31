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
using OpenAstroAra.Server.Services;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class FileSequenceServiceDirScaffoldTest {

        private string _tempDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _tempDir = Path.Combine(Path.GetTempPath(), $"oara-seqdir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }

        [Test]
        public void Constructor_creates_all_four_subdirs() {
            // §38.2 storage layout: library/, imported/, templates/, active/
            // The service should mkdir all four on construction.
            _ = new FileSequenceService(_tempDir);

            var sequencesRoot = Path.Combine(_tempDir, "sequences");
            Assert.That(Directory.Exists(sequencesRoot), Is.True, "sequences/ root should exist");

            foreach (var name in new[] {
                FileSequenceService.LibraryDirName,
                FileSequenceService.ImportedDirName,
                FileSequenceService.TemplatesDirName,
                FileSequenceService.ActiveDirName,
            }) {
                var subdir = Path.Combine(sequencesRoot, name);
                Assert.That(Directory.Exists(subdir), Is.True, $"sequences/{name} should exist");
            }
        }

        [Test]
        public void Constructor_is_idempotent_when_dirs_already_exist() {
            var sequencesRoot = Path.Combine(_tempDir, "sequences");
            Directory.CreateDirectory(Path.Combine(sequencesRoot, FileSequenceService.LibraryDirName));
            Directory.CreateDirectory(Path.Combine(sequencesRoot, FileSequenceService.ImportedDirName));
            // Construct after dirs exist — should not throw, should not lose data.
            Assert.DoesNotThrow(() => _ = new FileSequenceService(_tempDir));
        }
    }
}
