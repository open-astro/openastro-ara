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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2b(b) — restore from a REMOTE http(s) BackupSourceUrl: download to a staged temp,
    /// verify against the request's REQUIRED out-of-band sha256, then run the same extract+swap
    /// worker a local restore uses. Uses an injected <see cref="IBackupSourceFetcher"/> fake
    /// (in-memory bytes, no network) and an injected <see cref="IBackupRestorer"/> fake.
    /// </summary>
    [TestFixture]
    public class BackupRemoteRestoreTest {

        private static readonly Uri RemoteUrl = new("http://other-daemon:5555/api/v1/backup/snapshot/"
            + Guid.NewGuid().ToString("D") + "/download");

        private string _profileDir = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-remote-restore-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private sealed class FakeFetcher : IBackupSourceFetcher {
            public byte[] Bytes = Array.Empty<byte>();
            public long? AdvertisedLength;
            public bool AdvertiseTrueLength = true;
            public int OpenCalls;
            public Task<SkyDataFetch> OpenAsync(Uri source, CancellationToken ct) {
                OpenCalls++;
                var advertised = AdvertiseTrueLength ? Bytes.Length : AdvertisedLength;
                return Task.FromResult(new SkyDataFetch(new MemoryStream(Bytes), advertised));
            }
        }

        private sealed class RecordingRestorer : IBackupRestorer {
            public int Calls;
            public IReadOnlyList<string> Restore(string zipPath, string profileDir, bool profiles, bool sequences, CancellationToken ct) {
                Calls++;
                return new[] { "profiles" };
            }
        }

        private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static RestoreRequestDto Req(Uri src, string? sha) =>
            new(src, RestoreSequences: true, RestoreProfiles: true, RestoreFrameMetadata: false, RestoreLogs: false, Sha256: sha);

        private BackupService NewService(FakeFetcher? fetcher, RecordingRestorer restorer) =>
            new(_profileDir, NullLogger<BackupService>.Instance, restorer, fetcher);

        private static async Task<string?> WaitForTerminalAsync(BackupService svc) {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline) {
                var status = await svc.GetCloneStatusAsync(CancellationToken.None);
                var s = status.GetProperty("state").GetString();
                if (s is "done" or "failed") {
                    return s;
                }
                await Task.Delay(20);
            }
            Assert.Fail("restore did not reach a terminal clone-status within the timeout");
            return null;
        }

        private string[] StagedTemps() =>
            Directory.Exists(Path.Combine(_profileDir, "backups"))
                ? Directory.GetFiles(Path.Combine(_profileDir, "backups"), ".tmp-*")
                : Array.Empty<string>();

        [Test]
        public async Task Remote_restore_downloads_verifies_and_restores() {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var fetcher = new FakeFetcher { Bytes = payload };
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);

            var accepted = await svc.RestoreZipAsync(Req(RemoteUrl, Sha256Hex(payload)), null, CancellationToken.None);
            Assert.That(accepted.OperationType, Is.EqualTo("backup.restore-zip"));
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("done"));
            Assert.Multiple(() => {
                Assert.That(fetcher.OpenCalls, Is.EqualTo(1));
                Assert.That(restorer.Calls, Is.EqualTo(1));
                Assert.That(StagedTemps(), Is.Empty, "the downloaded temp must be reclaimed after the restore");
            });
        }

        [Test]
        public async Task Uppercase_sha256_matches_case_insensitively() {
            var payload = new byte[] { 9, 9, 9 };
            var fetcher = new FakeFetcher { Bytes = payload };
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);

            await svc.RestoreZipAsync(Req(RemoteUrl, Sha256Hex(payload).ToUpperInvariant()), null, CancellationToken.None);
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("done"));
        }

        [Test]
        public async Task Checksum_mismatch_fails_without_touching_live_config_and_reclaims_the_temp() {
            var fetcher = new FakeFetcher { Bytes = new byte[] { 1, 2, 3 } };
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);

            await svc.RestoreZipAsync(Req(RemoteUrl, new string('a', 64)), null, CancellationToken.None);
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("failed"));
            var status = await svc.GetCloneStatusAsync(CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(status.GetProperty("message").GetString(), Does.Contain("checksum"));
                Assert.That(restorer.Calls, Is.Zero, "an unverified archive must never reach the swap");
                Assert.That(StagedTemps(), Is.Empty, "a failed download must not stay staged");
            });
        }

        [Test]
        public void Remote_restore_without_sha256_is_rejected_synchronously() {
            using var svc = NewService(new FakeFetcher(), new RecordingRestorer());
            Assert.ThrowsAsync<BackupRestoreSourceUnsupportedException>(
                () => svc.RestoreZipAsync(Req(RemoteUrl, sha: null), null, CancellationToken.None));
        }

        [TestCase("nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!")] // 64 chars, not hex
        [TestCase("abc123")] // too short
        public void Remote_restore_with_a_malformed_sha256_is_rejected_synchronously(string sha) {
            using var svc = NewService(new FakeFetcher(), new RecordingRestorer());
            Assert.ThrowsAsync<BackupRestoreSourceUnsupportedException>(
                () => svc.RestoreZipAsync(Req(RemoteUrl, sha), null, CancellationToken.None));
        }

        [Test]
        public void Remote_restore_without_a_configured_fetcher_is_rejected_synchronously() {
            using var svc = NewService(fetcher: null, new RecordingRestorer());
            Assert.ThrowsAsync<BackupRestoreSourceUnsupportedException>(
                () => svc.RestoreZipAsync(Req(RemoteUrl, new string('a', 64)), null, CancellationToken.None));
        }

        [Test]
        public async Task An_archive_advertising_more_than_the_cap_is_refused_before_downloading() {
            var fetcher = new FakeFetcher { Bytes = new byte[] { 1 }, AdvertiseTrueLength = false, AdvertisedLength = long.MaxValue };
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);
            svc.MaxRemoteArchiveBytes = 16;

            await svc.RestoreZipAsync(Req(RemoteUrl, new string('a', 64)), null, CancellationToken.None);
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("failed"));
            var status = await svc.GetCloneStatusAsync(CancellationToken.None);
            Assert.That(status.GetProperty("message").GetString(), Does.Contain("cap"));
            Assert.That(restorer.Calls, Is.Zero);
        }

        [Test]
        public async Task An_archive_exceeding_the_cap_mid_download_is_refused_even_with_no_advertised_length() {
            var fetcher = new FakeFetcher { Bytes = new byte[64], AdvertiseTrueLength = false, AdvertisedLength = null };
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);
            svc.MaxRemoteArchiveBytes = 16;

            await svc.RestoreZipAsync(Req(RemoteUrl, Sha256Hex(new byte[64])), null, CancellationToken.None);
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("failed"));
            Assert.Multiple(() => {
                Assert.That(restorer.Calls, Is.Zero);
                Assert.That(StagedTemps(), Is.Empty);
            });
        }

        [Test]
        public async Task An_absolute_url_whose_snapshot_exists_locally_restores_locally_without_fetching() {
            // Pre-(b) compat: a host-blind absolute URL that names an on-disk snapshot keeps restoring the
            // local copy — no network fetch, manifest checksum gate as before.
            await File.WriteAllTextAsync(Path.Combine(_profileDir, "profile.json"), "{\"v\":1}");
            var fetcher = new FakeFetcher();
            var restorer = new RecordingRestorer();
            using var svc = NewService(fetcher, restorer);
            await svc.CreateZipAsync(null, CancellationToken.None);
            var snapshot = (await svc.ListSnapshotsAsync(CancellationToken.None)).Single();
            var absolute = new Uri(new Uri("http://some-host:5555"), snapshot.DownloadUrl);

            await svc.RestoreZipAsync(Req(absolute, sha: null), null, CancellationToken.None);
            Assert.That(await WaitForTerminalAsync(svc), Is.EqualTo("done"));
            Assert.Multiple(() => {
                Assert.That(fetcher.OpenCalls, Is.Zero, "a locally-resolvable snapshot must not be re-downloaded");
                Assert.That(restorer.Calls, Is.EqualTo(1));
            });
        }

        [Test]
        public void A_relative_unknown_snapshot_url_is_still_a_404_not_a_remote_attempt() {
            using var svc = NewService(new FakeFetcher(), new RecordingRestorer());
            var relative = new Uri("/api/v1/backup/snapshot/" + Guid.NewGuid().ToString("D") + "/download", UriKind.Relative);
            Assert.ThrowsAsync<BackupSnapshotNotFoundException>(
                () => svc.RestoreZipAsync(Req(relative, sha: null), null, CancellationToken.None));
        }

        [Test]
        public void A_non_http_scheme_is_not_a_remote_source() {
            using var svc = NewService(new FakeFetcher(), new RecordingRestorer());
            Assert.ThrowsAsync<BackupRestoreSourceUnsupportedException>(
                () => svc.RestoreZipAsync(Req(new Uri("ftp://host/backup.zip"), new string('a', 64)), null, CancellationToken.None));
        }
    }
}
