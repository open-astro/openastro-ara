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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §43-2 async create — the create worker: 202 returns while packaging is still running, the poll-able
    /// create-status drives idle→running→done/failed, a concurrent create is refused (409) unless it retries
    /// the running key, the disk-space pre-flight refuses up front (507), and the <c>backup.create.*</c>
    /// WS events narrate the worker. (The packaging CONTENT is covered by <see cref="BackupServiceTest"/>.)
    /// </summary>
    [TestFixture]
    public class BackupCreateWorkerTest {

        private string _profileDir = null!;
        private string _backupsDir = null!;
        private BackupService _svc = null!;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), "ara-backup-worker-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_profileDir);
            _backupsDir = Path.Combine(_profileDir, "backups");
            File.WriteAllText(Path.Combine(_profileDir, "profile.json"), "{\"name\":\"default\"}");
            _svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _svc.Dispose();
            if (Directory.Exists(_profileDir)) {
                Directory.Delete(_profileDir, recursive: true);
            }
        }

        private static async Task<string> StateAsync(BackupService svc) =>
            (await svc.GetCreateStatusAsync(CancellationToken.None)).GetProperty("state").GetString()!;

        [Test]
        public async Task Create_returns_202_while_packaging_is_still_running_then_reports_done() {
            using var gate = new ManualResetEventSlim(false);
            _svc.CreatePackagingTestHook = () => gate.Wait(TimeSpan.FromSeconds(15));

            Assert.That(await StateAsync(_svc), Is.EqualTo("idle"), "no create yet");
            var op = await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);

            // The POST already returned while the worker is blocked — the whole point of the rework.
            Assert.That(await StateAsync(_svc), Is.EqualTo("running"));
            Assert.That(Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir, "backup-*.zip") : Array.Empty<string>(),
                Is.Empty, "no archive exists yet while packaging is held");

            gate.Set();
            var (state, _) = await BackupTestOps.AwaitCreateTerminalAsync(_svc);
            Assert.That(state, Is.EqualTo("done"));

            var status = await _svc.GetCreateStatusAsync(CancellationToken.None);
            Assert.That(status.GetProperty("snapshot_id").GetString(),
                Is.EqualTo(op.OperationId.ToString("N")), "done carries the snapshot id (= the operation id)");
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task A_second_create_while_one_runs_is_refused_unless_it_retries_the_same_key() {
            using var gate = new ManualResetEventSlim(false);
            _svc.CreatePackagingTestHook = () => gate.Wait(TimeSpan.FromSeconds(15));
            try {
                var first = await _svc.CreateZipAsync(idempotencyKey: "key-1", CancellationToken.None);

                // Same running key → idempotent re-accept with the SAME operation id (a client retry of a POST
                // whose response it never saw).
                var retry = await _svc.CreateZipAsync(idempotencyKey: "key-1", CancellationToken.None);
                Assert.That(retry.OperationId, Is.EqualTo(first.OperationId));

                // A different key — and no key at all — are genuine second creates: refused.
                Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: "key-2", CancellationToken.None),
                    Throws.InstanceOf<BackupCreateInProgressException>());
                Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None),
                    Throws.InstanceOf<BackupCreateInProgressException>());
            } finally {
                gate.Set();
            }
            var (state, _) = await BackupTestOps.AwaitCreateTerminalAsync(_svc);
            Assert.That(state, Is.EqualTo("done"));
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(1),
                "the idempotent re-accept never started a second archive");

            // After the terminal, the slot is free — a new create (any key) is accepted again.
            _svc.CreatePackagingTestHook = null;
            await BackupTestOps.CreateAndAwaitAsync(_svc, "key-2");
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(2));
        }

        [Test]
        public async Task A_packaging_failure_lands_in_the_failed_terminal_with_no_artifacts() {
            _svc.CreatePackagingTestHook = () => throw new IOException("disk exploded mid-zip");

            await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var (state, message) = await BackupTestOps.AwaitCreateTerminalAsync(_svc);

            Assert.That(state, Is.EqualTo("failed"));
            Assert.That(message, Does.Contain("disk exploded"));
            Assert.That(Directory.Exists(_backupsDir) ? Directory.GetFiles(_backupsDir) : Array.Empty<string>(),
                Is.Empty, "a failed create leaves the backups dir clean");
        }

        [Test]
        public void A_full_disk_is_refused_up_front_with_507_semantics() {
            _svc.FreeBytesProbe = _ => 1024; // 1 KiB free — clearly below any estimate + slack

            Assert.That(async () => await _svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None),
                Throws.InstanceOf<BackupInsufficientStorageException>());
            Assert.That(Directory.Exists(_backupsDir), Is.False, "the refused create never touched the disk");
        }

        [Test]
        public async Task An_unavailable_free_space_probe_skips_the_preflight_rather_than_blocking() {
            _svc.FreeBytesProbe = _ => null;
            await BackupTestOps.CreateAndAwaitAsync(_svc);
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task The_worker_emits_started_then_complete_ws_events() {
            var events = new ConcurrentQueue<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback((string type, JsonElement _, CancellationToken _) => events.Enqueue(type))
                .Returns(Task.CompletedTask);
            using var svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance, ws: ws.Object);

            await BackupTestOps.CreateAndAwaitAsync(svc);

            // The complete event races the status terminal by a hair (it publishes right after); poll briefly.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !events.Contains(BackupService.CreateCompleteEvent)) {
                await Task.Delay(25);
            }
            var ordered = events.ToArray();
            Assert.That(ordered, Does.Contain(BackupService.CreateStartedEvent));
            Assert.That(ordered, Does.Contain(BackupService.CreateCompleteEvent));
            Assert.That(Array.IndexOf(ordered, BackupService.CreateStartedEvent),
                Is.LessThan(Array.IndexOf(ordered, BackupService.CreateCompleteEvent)));
        }

        [Test]
        public async Task A_failed_worker_emits_the_failed_ws_event() {
            var events = new ConcurrentQueue<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback((string type, JsonElement _, CancellationToken _) => events.Enqueue(type))
                .Returns(Task.CompletedTask);
            using var svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance, ws: ws.Object);
            svc.CreatePackagingTestHook = () => throw new IOException("boom");

            await svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            var (state, _) = await BackupTestOps.AwaitCreateTerminalAsync(svc);
            Assert.That(state, Is.EqualTo("failed"));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !events.Contains(BackupService.CreateFailedEvent)) {
                await Task.Delay(25);
            }
            Assert.That(events, Does.Contain(BackupService.CreateFailedEvent));
            Assert.That(events, Does.Not.Contain(BackupService.CreateCompleteEvent));
        }

        [Test]
        public async Task A_create_queues_behind_a_running_restore_not_409s() {
            // Create and restore share the serializing gate but NOT the 409 slot: a create during a restore is
            // accepted (202) and simply waits its turn on the worker. Hold the gate via a blocked restorer.
            using var restoreGate = new ManualResetEventSlim(false);
            using var restoreEntered = new ManualResetEventSlim(false);
            var restorer = new Mock<IBackupRestorer>();
            restorer.Setup(r => r.Restore(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                    It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback(() => {
                    restoreEntered.Set();
                    restoreGate.Wait(TimeSpan.FromSeconds(15));
                })
                .Returns(new System.Collections.Generic.List<string> { "profiles" });
            using var svc = new BackupService(_profileDir, NullLogger<BackupService>.Instance, restorer: restorer.Object);

            var seed = await BackupTestOps.CreateAndAwaitAsync(svc);
            var restoreOp = await svc.RestoreZipAsync(new OpenAstroAra.Server.Contracts.RestoreRequestDto(
                new Uri($"/api/v1/backup/snapshot/{seed.OperationId:D}/download", UriKind.Relative),
                RestoreSequences: false, RestoreProfiles: true, RestoreFrameMetadata: false, RestoreLogs: false),
                null, CancellationToken.None);
            Assert.That(restoreOp, Is.Not.Null);
            // The restore 202s before its worker claims the gate — wait until the restorer is genuinely
            // inside Restore (gate held) so the create below can't win the race and package first.
            Assert.That(restoreEntered.Wait(TimeSpan.FromSeconds(15)), Is.True, "the restore worker never started");

            // While the restore holds the gate: the create 202s and sits at running.
            await svc.CreateZipAsync(idempotencyKey: null, CancellationToken.None);
            Assert.That(await StateAsync(svc), Is.EqualTo("running"));
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(1),
                "the queued create hasn't packaged yet — the restore still owns the gate");

            restoreGate.Set();
            var (state, _) = await BackupTestOps.AwaitCreateTerminalAsync(svc);
            Assert.That(state, Is.EqualTo("done"));
            Assert.That(Directory.GetFiles(_backupsDir, "backup-*.zip"), Has.Length.EqualTo(2));
        }
    }
}
