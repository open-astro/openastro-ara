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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2b restore worker: the restore runs on a background worker and clone-status reports its live state
    /// (idle → running → done/failed), with a second concurrent restore rejected (409). Uses an injected
    /// <see cref="IBackupRestorer"/> fake to drive the worker deterministically (block / throw) without a real swap.
    /// </summary>
    [TestFixture]
    public class BackupRestoreWorkerTest {

        private string _profileDir = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-restore-worker-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private static readonly string[] BothAreas = { "profiles", "sequences" };

        private static RestoreRequestDto Req(Uri src) =>
            new(src, RestoreSequences: true, RestoreProfiles: true, RestoreFrameMetadata: false, RestoreLogs: false);

        // A real snapshot so the synchronous validation (snapshot-exists + checksum) passes before the worker runs.
        private async Task<(BackupService svc, Uri url)> NewServiceWithSnapshotAsync(IBackupRestorer restorer) {
            await File.WriteAllTextAsync(Path.Combine(_profileDir, "profile.json"), "{\"v\":1}");
            var svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance, restorer);
            await svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var url = (await svc.ListSnapshotsAsync(CancellationToken.None)).Single().DownloadUrl;
            return (svc, url);
        }

        private static async Task<string?> StateAsync(BackupService svc) {
            var status = await svc.GetCloneStatusAsync(CancellationToken.None);
            return status.GetProperty("state").GetString();
        }

        private static async Task WaitForTerminalAsync(BackupService svc) {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline) {
                var s = await StateAsync(svc);
                if (s is "done" or "failed") {
                    return;
                }
                await Task.Delay(20);
            }
            Assert.Fail("restore did not reach a terminal clone-status within the timeout");
        }

        [Test]
        public async Task Clone_status_is_idle_before_any_restore() {
            var svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance);
            using (svc) {
                Assert.That(await StateAsync(svc), Is.EqualTo("idle"));
            }
        }

        [Test]
        public async Task A_successful_restore_drives_clone_status_to_done_with_the_restored_areas() {
            var (svc, url) = await NewServiceWithSnapshotAsync(new _ImmediateRestorer(BothAreas));
            using (svc) {
                await svc.RestoreZipAsync(Req(url), idempotencyKey: null, CancellationToken.None);
                await WaitForTerminalAsync(svc);

                var status = await svc.GetCloneStatusAsync(CancellationToken.None);
                Assert.That(status.GetProperty("state").GetString(), Is.EqualTo("done"));
                Assert.That(status.GetProperty("progress_pct").GetDouble(), Is.EqualTo(100));
                Assert.That(status.GetProperty("message").GetString(), Does.Contain("profiles").And.Contains("sequences"));
            }
        }

        [Test]
        public async Task A_restore_whose_worker_throws_drives_clone_status_to_failed() {
            var (svc, url) = await NewServiceWithSnapshotAsync(new _ThrowingRestorer("disk gone"));
            using (svc) {
                // The 202 still returns — the failure happens on the worker, surfaced via clone-status, not the POST.
                await svc.RestoreZipAsync(Req(url), idempotencyKey: null, CancellationToken.None);
                await WaitForTerminalAsync(svc);

                var status = await svc.GetCloneStatusAsync(CancellationToken.None);
                Assert.That(status.GetProperty("state").GetString(), Is.EqualTo("failed"));
                Assert.That(status.GetProperty("message").GetString(), Does.Contain("disk gone"));
            }
        }

        [Test]
        public async Task A_second_restore_while_one_is_running_is_rejected_with_in_progress() {
            using var gated = new _GatedRestorer();
            var (svc, url) = await NewServiceWithSnapshotAsync(gated);
            using (svc) {
                // First restore: the worker enters the fake and blocks, so clone-status is "running".
                await svc.RestoreZipAsync(Req(url), idempotencyKey: null, CancellationToken.None);
                try {
                    Assert.That(await gated.Started.WaitAsync(TimeSpan.FromSeconds(5)), Is.True, "worker should have started");
                    Assert.That(await StateAsync(svc), Is.EqualTo("running"));

                    // Second restore while the first is in flight → 409.
                    Assert.That(
                        async () => await svc.RestoreZipAsync(Req(url), idempotencyKey: null, CancellationToken.None),
                        Throws.InstanceOf<BackupRestoreInProgressException>());
                } finally {
                    // Always unblock the worker — even if an assertion above failed — so the test fails fast instead
                    // of leaving the worker parked on Release.Wait while teardown disposes the semaphores.
                    gated.Release.Release();
                }

                await WaitForTerminalAsync(svc);
                Assert.That(await StateAsync(svc), Is.EqualTo("done"));
            }
        }

        private sealed class _ImmediateRestorer : IBackupRestorer {
            private readonly IReadOnlyList<string> _areas;
            public _ImmediateRestorer(IReadOnlyList<string> areas) => _areas = areas;
            public IReadOnlyList<string> Restore(string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct) => _areas;
        }

        private sealed class _ThrowingRestorer : IBackupRestorer {
            private readonly string _message;
            public _ThrowingRestorer(string message) => _message = message;
            public IReadOnlyList<string> Restore(string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct) =>
                throw new InvalidOperationException(_message);
        }

        // Blocks inside Restore until Release is signalled, so the test can observe the "running" state + assert the
        // concurrent-restore rejection while the first worker is genuinely in flight.
        private sealed class _GatedRestorer : IBackupRestorer, IDisposable {
            public readonly SemaphoreSlim Started = new(0);
            public readonly SemaphoreSlim Release = new(0);
            private static readonly string[] Profiles = { "profiles" };
            public IReadOnlyList<string> Restore(string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct) {
                Started.Release();
                Release.Wait(ct);
                return Profiles;
            }
            public void Dispose() {
                Started.Dispose();
                Release.Dispose();
            }
        }
    }
}
