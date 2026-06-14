#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2a restore: roll a profile back to a captured snapshot via an atomic per-area staged-swap. Covers the
    /// round-trip (profiles + sequences), per-area selection, checksum/source/not-found rejection, and that a
    /// successful restore leaves no scratch dirs behind.
    /// </summary>
    [TestFixture]
    public class BackupRestoreTest {

        private string _profileDir = null!;
        private BackupService _svc = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-restore-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            _svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _svc.Dispose();
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private string ProfilePath => Path.Combine(_profileDir, "profile.json");
        private string SeqPath(string rel) => Path.Combine(_profileDir, "sequences", rel);

        private void WriteProfile(string content) => File.WriteAllText(ProfilePath, content);

        private void WriteSequence(string rel, string content) {
            var path = SeqPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private async Task<Uri> CreateSnapshotAsync() {
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var snap = (await _svc.ListSnapshotsAsync(CancellationToken.None)).Single();
            return snap.DownloadUrl;
        }

        private static RestoreRequestDto Req(Uri src, bool profiles, bool sequences) =>
            new(src, RestoreSequences: sequences, RestoreProfiles: profiles, RestoreFrameMetadata: false, RestoreLogs: false);

        [Test]
        public async Task Restore_rolls_both_areas_back_to_the_snapshot() {
            WriteProfile("{\"v\":1}");
            WriteSequence("library/m31.json", "ORIGINAL");
            var url = await CreateSnapshotAsync();

            // Mutate live config after the snapshot.
            WriteProfile("{\"v\":2}");
            WriteSequence("library/m31.json", "CHANGED");
            WriteSequence("library/new.json", "ADDED-AFTER");

            var op = await _svc.RestoreZipAsync(Req(url, profiles: true, sequences: true), idempotencyKey: null, CancellationToken.None);

            Assert.That(op.OperationType, Is.EqualTo("backup.restore-zip"));
            Assert.That(await File.ReadAllTextAsync(ProfilePath), Is.EqualTo("{\"v\":1}"), "profile.json rolled back");
            Assert.That(await File.ReadAllTextAsync(SeqPath("library/m31.json")), Is.EqualTo("ORIGINAL"), "sequence rolled back");
            Assert.That(File.Exists(SeqPath("library/new.json")), Is.False,
                "the sequences/ tree is replaced wholesale — a file added after the snapshot is gone");
            Assert.That(Directory.GetDirectories(_profileDir, ".restore-*"), Is.Empty, "no scratch dirs left behind");
        }

        [Test]
        public async Task Restore_only_the_selected_area() {
            WriteProfile("{\"v\":1}");
            WriteSequence("a.json", "SEQ-ORIGINAL");
            var url = await CreateSnapshotAsync();

            WriteProfile("{\"v\":2}");
            WriteSequence("a.json", "SEQ-CHANGED");

            // Restore sequences only — profile.json must stay at its post-snapshot value.
            await _svc.RestoreZipAsync(Req(url, profiles: false, sequences: true), idempotencyKey: null, CancellationToken.None);

            Assert.That(await File.ReadAllTextAsync(ProfilePath), Is.EqualTo("{\"v\":2}"), "profile untouched");
            Assert.That(await File.ReadAllTextAsync(SeqPath("a.json")), Is.EqualTo("SEQ-ORIGINAL"), "sequences restored");
        }

        [Test]
        public async Task Restore_from_an_unknown_snapshot_throws_not_found() {
            WriteProfile("{\"v\":1}");
            await CreateSnapshotAsync();
            var bogus = new Uri($"/api/v1/backup/snapshot/{Guid.NewGuid():D}/download", UriKind.Relative);

            Assert.That(async () => await _svc.RestoreZipAsync(Req(bogus, true, true), null, CancellationToken.None),
                Throws.InstanceOf<BackupSnapshotNotFoundException>());
        }

        [Test]
        public void Restore_from_an_unsupported_source_throws() {
            var external = new Uri("https://example.com/some-backup.zip");

            Assert.That(async () => await _svc.RestoreZipAsync(Req(external, true, true), null, CancellationToken.None),
                Throws.InstanceOf<BackupRestoreSourceUnsupportedException>(),
                "only a local snapshot URL is a supported restore source in §43-2a");
        }

        [Test]
        public async Task Restore_with_no_area_selected_throws() {
            WriteProfile("{\"v\":1}");
            var url = await CreateSnapshotAsync();

            Assert.That(async () => await _svc.RestoreZipAsync(Req(url, profiles: false, sequences: false), null, CancellationToken.None),
                Throws.InstanceOf<BackupRestoreSourceUnsupportedException>());
        }

        [Test]
        public async Task Restore_rejects_a_corrupt_archive_before_touching_config() {
            WriteProfile("{\"v\":1}");
            var url = await CreateSnapshotAsync();
            WriteProfile("{\"v\":2}");

            // Corrupt the stored archive so it no longer matches the manifest sha256.
            var zip = Directory.GetFiles(Path.Combine(_profileDir, "backups"), "backup-*.zip").Single();
            await File.WriteAllTextAsync(zip, "not a zip anymore");

            Assert.That(async () => await _svc.RestoreZipAsync(Req(url, true, true), null, CancellationToken.None),
                Throws.InstanceOf<BackupCorruptException>());
            Assert.That(await File.ReadAllTextAsync(ProfilePath), Is.EqualTo("{\"v\":2}"),
                "a corrupt archive is refused before any live config is touched");
        }
    }
}
