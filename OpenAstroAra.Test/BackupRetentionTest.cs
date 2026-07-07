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
using Moq;
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
    /// §43-2b retention — after every successful create, snapshots beyond
    /// <c>storage.backup_retention_count</c> are pruned oldest-first (manifest
    /// then zip); 0 keeps everything; a profile fault falls back to the default
    /// and never fails the create.
    /// </summary>
    [TestFixture]
    public class BackupRetentionTest {

        private string _profileDir = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-retention-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            await File.WriteAllTextAsync(Path.Combine(_profileDir, "profile.json"), "{\"v\":1}");
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private static IProfileStore StoreWithRetention(int keep) {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetStorageSettings())
                .Returns(new StorageSettingsDto("/tmp", "fits", "rice", "t", BackupRetentionCount: keep));
            return store.Object;
        }

        private BackupService NewService(IProfileStore? profiles) =>
            new(_profileDir, NullLogger<BackupService>.Instance, restorer: null, remoteFetcher: null, profiles: profiles);

        private static async Task CreateN(BackupService svc, int n) {
            for (var i = 0; i < n; i++) {
                await svc.CreateZipAsync(null, CancellationToken.None);
                // CreatedUtc granularity is well below a millisecond, but the on-disk name embeds a
                // whole-second timestamp — ordering comes from the manifest, so no sleep is needed.
            }
        }

        [Test]
        public async Task Prune_keeps_the_newest_N_snapshots() {
            using var svc = NewService(StoreWithRetention(3));
            await CreateN(svc, 5);
            var listed = await svc.ListSnapshotsAsync(CancellationToken.None);
            Assert.That(listed, Has.Count.EqualTo(3));
            // The survivors are the 3 newest (list is newest-first).
            Assert.That(listed.Select(s => s.CreatedUtc), Is.Ordered.Descending);
        }

        [Test]
        public async Task Zero_retention_keeps_everything() {
            using var svc = NewService(StoreWithRetention(0));
            await CreateN(svc, 4);
            Assert.That(await svc.ListSnapshotsAsync(CancellationToken.None), Has.Count.EqualTo(4));
        }

        [Test]
        public async Task No_profile_store_prunes_at_the_default_20() {
            using var svc = NewService(profiles: null);
            await CreateN(svc, 3); // far under the default — nothing pruned
            Assert.That(await svc.ListSnapshotsAsync(CancellationToken.None), Has.Count.EqualTo(3));
        }

        [Test]
        public async Task A_profile_read_fault_never_fails_the_create() {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetStorageSettings()).Throws(new InvalidOperationException("profile torn"));
            using var svc = NewService(store.Object);
            await CreateN(svc, 2); // must not throw
            Assert.That(await svc.ListSnapshotsAsync(CancellationToken.None), Has.Count.EqualTo(2));
        }

        [Test]
        public async Task Pruned_snapshots_lose_both_manifest_and_zip() {
            using var svc = NewService(StoreWithRetention(1));
            await CreateN(svc, 3);
            var backupsDir = Path.Combine(_profileDir, "backups");
            Assert.Multiple(() => {
                Assert.That(Directory.GetFiles(backupsDir, "backup-*.zip"), Has.Length.EqualTo(1));
                Assert.That(Directory.GetFiles(backupsDir, "backup-*.meta.json"), Has.Length.EqualTo(1));
            });
        }
    }
}
