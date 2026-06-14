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
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-1 backup service: packaging the profile config areas into a ZIP snapshot + sidecar manifest,
    /// listing them, and resolving one for download. Covers the happy path, area selection (only present
    /// areas claimed), torn/corrupt manifests being skipped, and download-path resolution.
    /// </summary>
    [TestFixture]
    public class BackupServiceTest {

        private static readonly string[] ProfileAndSequences = { "profiles", "sequences" };
        private static readonly string[] ProfileOnly = { "profiles" };

        private string _profileDir = null!;
        private string _backupsDir = null!;
        private BackupService _svc = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-backup-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            _backupsDir = Path.Combine(_profileDir, "backups");
            _svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private void WriteProfile(string content = "{\"name\":\"default\"}") =>
            File.WriteAllText(Path.Combine(_profileDir, "profile.json"), content);

        private void WriteSequence(string relative, string content) {
            var path = Path.Combine(_profileDir, "sequences", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        // Non-async helpers keep the synchronous archive/hash IO out of the async test bodies (CA1849).
        private static System.Collections.Generic.List<string> ZipEntryNames(string zipPath) {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Select(e => e.FullName).ToList();
        }

        private static string Sha256OfFile(string path) {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        [Test]
        public async Task Create_packages_profile_and_sequences_into_a_zip_with_a_manifest() {
            WriteProfile();
            WriteSequence(Path.Combine("library", "m31.json"), "{\"target\":\"M31\"}");

            var op = await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            Assert.That(op.OperationType, Is.EqualTo("backup.create-zip"));
            var zips = Directory.GetFiles(_backupsDir, "backup-*.zip");
            Assert.That(zips, Has.Length.EqualTo(1), "exactly one archive is produced");

            var entries = ZipEntryNames(zips[0]);
            Assert.That(entries, Does.Contain("profile.json"));
            Assert.That(entries, Does.Contain("sequences/library/m31.json"));
            Assert.That(Directory.GetFiles(_backupsDir, ".tmp-*"), Is.Empty, "no temp archive is left behind");
        }

        [Test]
        public async Task Create_does_not_follow_a_symlink_out_of_the_backup_root() {
            WriteProfile();
            WriteSequence("real.json", "{}");

            // A secret outside the profile dir, and a symlink inside sequences/ pointing at it. The backup must not
            // bundle the secret — Directory.EnumerateFiles(AllDirectories) would have followed the link.
            var outside = Path.Combine(Path.GetTempPath(), "ara-backup-outside-" + Path.GetRandomFileName());
            Directory.CreateDirectory(outside);
            try {
                var secret = Path.Combine(outside, "secret.txt");
                await File.WriteAllTextAsync(secret, "do-not-back-me-up");
                var link = Path.Combine(_profileDir, "sequences", "linked.txt");
                try {
                    File.CreateSymbolicLink(link, secret);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
                    Assert.Ignore("symlink creation not permitted on this platform/runner");
                }

                await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
                var entries = ZipEntryNames(Directory.GetFiles(_backupsDir, "backup-*.zip").Single());
                Assert.That(entries, Does.Contain("sequences/real.json"), "real files are still backed up");
                Assert.That(entries, Has.None.Contains("linked"), "the symlink itself isn't archived");
                Assert.That(entries.Any(e => e.Contains("secret", StringComparison.Ordinal)), Is.False,
                    "the symlink target is never followed");
            } finally {
                // Covers the Assert.Ignore path too — the outside dir is reclaimed however the body exits.
                if (Directory.Exists(outside)) {
                    Directory.Delete(outside, recursive: true);
                }
            }
        }

        [Test]
        public async Task List_reports_the_created_snapshot_with_a_matching_sha256_and_areas() {
            WriteProfile();
            WriteSequence("active.json", "{}");

            var op = await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var snapshots = await _svc.ListSnapshotsAsync(CancellationToken.None);

            Assert.That(snapshots, Has.Count.EqualTo(1));
            var snap = snapshots[0];
            Assert.That(snap.BackupId, Is.EqualTo(op.OperationId), "the listed id is the create operation id");
            Assert.That(snap.IncludedAreas, Is.EquivalentTo(ProfileAndSequences));
            Assert.That(snap.SizeBytes, Is.GreaterThan(0));

            var zipPath = Directory.GetFiles(_backupsDir, "backup-*.zip").Single();
            var expected = Sha256OfFile(zipPath);
            Assert.That(snap.Sha256, Is.EqualTo(expected), "the manifest sha256 matches the archive bytes");
            Assert.That(snap.DownloadUrl.ToString(), Does.Contain(snap.BackupId.ToString("D")));
        }

        [Test]
        public async Task Create_does_not_follow_a_symlinked_profile_json() {
            WriteSequence("real.json", "{}");
            var outside = Path.Combine(Path.GetTempPath(), "ara-backup-outside-" + Path.GetRandomFileName());
            Directory.CreateDirectory(outside);
            try {
                var secret = Path.Combine(outside, "creds.json");
                await File.WriteAllTextAsync(secret, "{\"password\":\"do-not-leak\"}");
                try {
                    File.CreateSymbolicLink(Path.Combine(_profileDir, "profile.json"), secret);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
                    Assert.Ignore("symlink creation not permitted on this platform/runner");
                }

                await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
                var snap = (await _svc.ListSnapshotsAsync(CancellationToken.None)).Single();
                var entries = ZipEntryNames(Directory.GetFiles(_backupsDir, "backup-*.zip").Single());

                Assert.That(entries, Has.None.EqualTo("profile.json"), "a symlinked profile.json isn't archived");
                Assert.That(snap.IncludedAreas, Has.None.EqualTo("profiles"), "the profiles area isn't claimed");
                Assert.That(snap.IncludedAreas, Does.Contain("sequences"), "real areas are still backed up");
            } finally {
                if (Directory.Exists(outside)) {
                    Directory.Delete(outside, recursive: true);
                }
            }
        }

        [Test]
        public async Task Create_only_claims_areas_that_exist() {
            WriteProfile();
            // No sequences/ dir.

            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var snap = (await _svc.ListSnapshotsAsync(CancellationToken.None)).Single();

            Assert.That(snap.IncludedAreas, Is.EquivalentTo(ProfileOnly),
                "an absent sequences/ tree isn't claimed as a backed-up area");
        }

        [Test]
        public async Task Open_returns_a_readable_stream_for_a_known_id_and_null_otherwise() {
            WriteProfile();
            var op = await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            var snapshot = await _svc.OpenSnapshotAsync(op.OperationId, CancellationToken.None);
            Assert.That(snapshot, Is.Not.Null);
            await using (var stream = snapshot!.Value.Stream) {
                Assert.That(stream.CanRead, Is.True);
                Assert.That(stream.Length, Is.GreaterThan(0), "the opened archive has content");
            }
            Assert.That(Path.GetExtension(snapshot.Value.FileName), Is.EqualTo(".zip"));

            var missing = await _svc.OpenSnapshotAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.That(missing, Is.Null, "an unknown id opens to null (→ 404 at the endpoint)");
        }

        [Test]
        public async Task List_skips_a_manifest_whose_archive_is_gone() {
            WriteProfile();
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            // Delete the archive but leave the manifest — a half-deleted backup shouldn't list a phantom snapshot.
            File.Delete(Directory.GetFiles(_backupsDir, "backup-*.zip").Single());
            var snapshots = await _svc.ListSnapshotsAsync(CancellationToken.None);

            Assert.That(snapshots, Is.Empty, "a manifest with no archive is not surfaced");
        }

        [Test]
        public async Task List_skips_a_corrupt_manifest_and_returns_the_rest() {
            WriteProfile();
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, "garbage.meta.json"), "not json {{{");

            var snapshots = await _svc.ListSnapshotsAsync(CancellationToken.None);

            Assert.That(snapshots, Has.Count.EqualTo(1), "a corrupt manifest is skipped, the valid one survives");
        }

        [Test]
        public async Task List_orders_snapshots_newest_first() {
            WriteProfile();
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            // Guarantee distinct CreatedUtc stamps so the ordering assert is meaningful, not trivially true on a
            // tie — DateTimeOffset.UtcNow resolution can be ~15ms on Windows, enough to tie two back-to-back creates.
            await Task.Delay(25);
            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            var snapshots = await _svc.ListSnapshotsAsync(CancellationToken.None);

            Assert.That(snapshots, Has.Count.EqualTo(2));
            Assert.That(snapshots[0].CreatedUtc, Is.GreaterThan(snapshots[1].CreatedUtc),
                "the most recent snapshot is listed first");
        }

        [Test]
        public async Task List_on_a_fresh_profile_with_no_backups_is_empty() {
            var snapshots = await _svc.ListSnapshotsAsync(CancellationToken.None);
            Assert.That(snapshots, Is.Empty, "no backups dir yet → empty list, not an error");
        }

        [Test]
        public void Create_with_nothing_to_archive_throws_and_leaves_no_snapshot() {
            // No profile.json, no sequences/ — a content-free zip would otherwise list as a phantom snapshot.
            Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None),
                Throws.InstanceOf<BackupNothingToArchiveException>());

            Assert.That(Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir) : Array.Empty<string>(),
                Is.Empty, "the staged temp is reclaimed; no zip or manifest is left");
        }

        [Test]
        public void Create_with_a_pathologically_deep_tree_throws_instead_of_overflowing() {
            WriteProfile();
            // Build a sequences/ tree deeper than the 64-level cap. Without the depth guard the recursive walk would
            // risk a StackOverflowException (uncatchable, crashes the process); with it, an ordinary catchable throw.
            var deep = Path.Combine(_profileDir, "sequences");
            for (var i = 0; i < 80; i++) {
                deep = Path.Combine(deep, "d");
            }
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "leaf.json"), "{}");

            Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>());

            Assert.That(Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir) : Array.Empty<string>(),
                Is.Empty, "the staged temp is reclaimed; no partial snapshot is left");
        }

        [Test]
        public void Create_with_a_cancelled_token_throws_and_leaves_no_artifacts() {
            WriteProfile();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            // A cancelled create reclaims whatever it started — no zip, no temp, no manifest orphaned.
            Assert.That(Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir) : Array.Empty<string>(),
                Is.Empty, "a cancelled create leaves the backups dir clean");
        }
    }
}
