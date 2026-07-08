#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2b(c) — the frames-catalog backup area: a consistent <c>db/openastroara.db</c>
    /// snapshot rides every backup zip (playbook §43.4), the manifest carries the §43.7 row
    /// count, and <c>restore_frame_metadata</c> swaps the catalog back with the stale
    /// WAL/SHM sidecars moved aside (a replayed old WAL would corrupt the restored file).
    /// </summary>
    [TestFixture]
    public class BackupFrameMetadataAreaTest {

        private string _profileDir = null!;
        private BackupService _svc = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-fmarea-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            File.WriteAllText(Path.Combine(_profileDir, "profile.json"), "{\"v\":1}");
            _svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _svc.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private string DbPath => Path.Combine(_profileDir, "openastroara.db");

        private void SeedCatalog(params string[] frameIds) {
            var cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS frames (id TEXT PRIMARY KEY);";
            cmd.ExecuteNonQuery();
            foreach (var id in frameIds) {
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT OR REPLACE INTO frames (id) VALUES ($id);";
                insert.Parameters.AddWithValue("$id", id);
                insert.ExecuteNonQuery();
            }
        }

        private string[] CatalogIds() {
            var cs = new SqliteConnectionStringBuilder {
                DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM frames ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            var ids = new System.Collections.Generic.List<string>();
            while (reader.Read()) {
                ids.Add(reader.GetString(0));
            }
            return ids.ToArray();
        }

        private async Task<Uri> CreateSnapshotAsync() {
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var snap = (await _svc.ListSnapshotsAsync(CancellationToken.None))[0];
            return snap.DownloadUrl;
        }

        private async Task WaitForRestoreDoneAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline) {
                var status = await _svc.GetCloneStatusAsync(CancellationToken.None);
                var state = status.GetProperty("state").GetString();
                if (state == "done") {
                    return;
                }
                if (state == "failed") {
                    Assert.Fail("restore failed: " + status.GetProperty("message").GetString());
                }
                await Task.Delay(20);
            }
            Assert.Fail("restore did not reach a terminal clone-status within the timeout");
        }

        [Test]
        public async Task Create_includes_the_catalog_snapshot_and_counts_its_rows() {
            SeedCatalog("f1", "f2", "f3");

            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            var zip = Directory.GetFiles(Path.Combine(_profileDir, "backups"), "backup-*.zip").Single();
            using (var archive = await ZipFile.OpenReadAsync(zip, CancellationToken.None)) {
                Assert.That(archive.GetEntry("db/openastroara.db"), Is.Not.Null,
                    "§43.4 lists the catalog snapshot unconditionally");
            }
            var manifestJson = await File.ReadAllTextAsync(
                Directory.GetFiles(Path.Combine(_profileDir, "backups"), "backup-*.meta.json").Single());
            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                manifestJson, OpenAstroAra.Server.AraJsonSerializerContext.Default.BackupManifest)!;
            Assert.That(manifest.IncludedAreas, Does.Contain("frames_metadata"));
            Assert.That(manifest.FramesMetadataRows, Is.EqualTo(3), "the §43.7 contents count");
        }

        [Test]
        public async Task Create_without_a_catalog_stays_config_only_and_says_so() {
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            var manifestJson = await File.ReadAllTextAsync(
                Directory.GetFiles(Path.Combine(_profileDir, "backups"), "backup-*.meta.json").Single());
            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                manifestJson, OpenAstroAra.Server.AraJsonSerializerContext.Default.BackupManifest)!;
            Assert.That(manifest.IncludedAreas, Does.Not.Contain("frames_metadata"),
                "the area list is honest — a fresh daemon has no catalog to capture");
            Assert.That(manifest.FramesMetadataRows, Is.Null);
        }

        [Test]
        public async Task Restore_swaps_the_catalog_back_and_removes_stale_wal_sidecars() {
            SeedCatalog("original");
            var url = await CreateSnapshotAsync();

            // The catalog moves on after the snapshot…
            SeedCatalog("added-later");
            // …and a stale WAL/SHM pair sits next to it (the live catalog runs WAL). Restoring
            // the snapshot must move these aside too — SQLite would replay the old WAL into the
            // swapped-in file.
            await File.WriteAllTextAsync(DbPath + "-wal", "stale wal");
            await File.WriteAllTextAsync(DbPath + "-shm", "stale shm");

            await _svc.RestoreZipAsync(
                new RestoreRequestDto(url, RestoreSequences: false, RestoreProfiles: false,
                    RestoreFrameMetadata: true, RestoreLogs: false),
                idempotencyKey: null, CancellationToken.None);
            await WaitForRestoreDoneAsync();

            Assert.That(CatalogIds(), Has.Length.EqualTo(1).And.Contain("original"),
                "the catalog is back at the snapshot state");
            Assert.That(File.Exists(DbPath + "-wal"), Is.False, "the stale WAL moved aside with the old db");
            Assert.That(File.Exists(DbPath + "-shm"), Is.False, "the stale SHM moved aside with the old db");
            Assert.That(Directory.GetFileSystemEntries(_profileDir, ".restore-*"), Is.Empty,
                "a successful restore leaves no scratch dirs behind");
        }

        [Test]
        public async Task Restore_without_the_flag_leaves_the_catalog_alone() {
            SeedCatalog("original");
            var url = await CreateSnapshotAsync();
            SeedCatalog("added-later");

            await _svc.RestoreZipAsync(
                new RestoreRequestDto(url, RestoreSequences: false, RestoreProfiles: true,
                    RestoreFrameMetadata: false, RestoreLogs: false),
                idempotencyKey: null, CancellationToken.None);
            await WaitForRestoreDoneAsync();

            Assert.That(CatalogIds(), Has.Length.EqualTo(2).And.Contain("added-later").And.Contain("original"),
                "restore_frame_metadata=false must not touch the live catalog");
        }
    }
}
