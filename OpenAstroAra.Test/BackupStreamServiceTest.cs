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
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §44 — <see cref="BackupStreamService"/> over a real temp-dir catalog: the
    /// single-target claim/reclaim/stale-takeover slot, the oldest-first pending
    /// queue with lazy sha256 backfill, ack bookkeeping (incl. the unverified-ack
    /// refusal), per-target re-queue after a takeover, and the status rollup.
    /// </summary>
    [TestFixture]
    public class BackupStreamServiceTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private BackupStreamService _svc = null!;
        private static readonly Guid Session = Guid.Parse("44444444-4444-4444-4444-444444444401");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-bstream-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            await using (var conn = _db.OpenConnection()) {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
                cmd.Parameters.AddWithValue("$id", Session.ToString());
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync();
            }
            _svc = new BackupStreamService(_db);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private async Task<(Guid Id, string Path, string Sha)> InsertFrameAsync(string name, DateTimeOffset capturedUtc) {
            var id = Guid.NewGuid();
            var path = Path.Combine(_dir, $"{name}.fits");
            var payload = $"FITS-{name}";
            await File.WriteAllTextAsync(path, payload);
            var sha = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                                    exposure_seconds, captured_utc, file_path, file_size_bytes,
                                    width, height, bit_depth)
                VALUES ($id, $sid, 'M31', 'light', 'L', 300, $t, $path, $size, 100, 100, 16);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$sid", Session.ToString());
            cmd.Parameters.AddWithValue("$t", capturedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$size", new FileInfo(path).Length);
            await cmd.ExecuteNonQueryAsync();
            return (id, path, sha);
        }

        [Test]
        public async Task Claim_is_single_target_with_idempotent_reclaim_and_stale_takeover() {
            var first = await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);
            Assert.That(first, Is.Not.Null);

            // A different hostname is refused while the holder is fresh.
            Assert.That(await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-laptop"), CancellationToken.None), Is.Null);
            Assert.That(_svc.ActiveTargetSnapshot, Is.EqualTo("wilma-desk"));

            // The SAME hostname re-claims idempotently (crash recovery).
            var reclaim = await _svc.ClaimAsync(new BackupStreamClaimRequestDto("WILMA-DESK"), CancellationToken.None);
            Assert.That(reclaim, Is.Not.Null);

            // Once the holder goes silent past the stale window, a takeover succeeds.
            _svc.StaleClaimWindow = TimeSpan.Zero;
            var takeover = await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-laptop"), CancellationToken.None);
            Assert.That(takeover, Is.Not.Null);
            Assert.That(_svc.ActiveTargetSnapshot, Is.EqualTo("wilma-laptop"));
        }

        [Test]
        public async Task Queue_serves_oldest_first_with_lazy_sha_backfill_and_ack_removes() {
            var older = await InsertFrameAsync("older", DateTimeOffset.UtcNow.AddMinutes(-10));
            var newer = await InsertFrameAsync("newer", DateTimeOffset.UtcNow);
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);

            var queue = await _svc.GetQueueAsync("wilma-desk", 10, CancellationToken.None);
            Assert.That(queue, Is.Not.Null);
            Assert.That(queue!, Has.Count.EqualTo(2));
            Assert.That(queue[0].Id, Is.EqualTo(older.Id), "oldest first per §44.5");
            Assert.That(queue[0].Sha256, Is.EqualTo(older.Sha), "sha computed lazily on first serve");
            Assert.That(queue[1].Sha256, Is.EqualTo(newer.Sha));

            Assert.That(await _svc.AckAsync("wilma-desk", new BackupStreamAckRequestDto(older.Id, Sha256Verified: true), CancellationToken.None), Is.True);
            var after = await _svc.GetQueueAsync("wilma-desk", 10, CancellationToken.None);
            Assert.That(after!, Has.Count.EqualTo(1));
            Assert.That(after[0].Id, Is.EqualTo(newer.Id));

            var status = await _svc.GetStatusAsync(CancellationToken.None);
            Assert.That(status.Enabled, Is.True);
            Assert.That(status.ActiveTarget, Is.EqualTo("wilma-desk"));
            Assert.That(status.PendingCount, Is.EqualTo(1));
            Assert.That(status.SyncedCount, Is.EqualTo(1));
            Assert.That(status.QueueSizeBytes, Is.GreaterThan(0));
        }

        [Test]
        public async Task Unverified_ack_is_refused_and_the_frame_stays_queued() {
            var frame = await InsertFrameAsync("f", DateTimeOffset.UtcNow);
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);

            Assert.That(await _svc.AckAsync("wilma-desk", new BackupStreamAckRequestDto(frame.Id, Sha256Verified: false), CancellationToken.None), Is.False);
            var queue = await _svc.GetQueueAsync("wilma-desk", 10, CancellationToken.None);
            Assert.That(queue!, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Non_holder_gets_no_queue_and_cannot_ack() {
            var frame = await InsertFrameAsync("f", DateTimeOffset.UtcNow);
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);

            Assert.That(await _svc.GetQueueAsync("wilma-laptop", 10, CancellationToken.None), Is.Null);
            Assert.That(await _svc.AckAsync("wilma-laptop", new BackupStreamAckRequestDto(frame.Id, Sha256Verified: true), CancellationToken.None), Is.False);
        }

        [Test]
        public async Task A_new_target_after_takeover_re_queues_frames_synced_to_the_old_one() {
            var frame = await InsertFrameAsync("f", DateTimeOffset.UtcNow);
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);
            await _svc.AckAsync("wilma-desk", new BackupStreamAckRequestDto(frame.Id, Sha256Verified: true), CancellationToken.None);
            Assert.That((await _svc.GetQueueAsync("wilma-desk", 10, CancellationToken.None))!, Is.Empty);

            _svc.StaleClaimWindow = TimeSpan.Zero;
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-laptop"), CancellationToken.None);
            var queue = await _svc.GetQueueAsync("wilma-laptop", 10, CancellationToken.None);
            Assert.That(queue!, Has.Count.EqualTo(1), "each target mirrors the full catalog (§44.1)");
        }

        [Test]
        public async Task Release_frees_the_slot_only_for_the_holder() {
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);
            Assert.That(await _svc.ReleaseAsync(new BackupStreamClaimRequestDto("wilma-laptop"), CancellationToken.None), Is.False);
            Assert.That(await _svc.ReleaseAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None), Is.True);
            Assert.That((await _svc.GetStatusAsync(CancellationToken.None)).Enabled, Is.False);
        }

        [Test]
        public async Task A_missing_fits_file_serves_null_sha_but_does_not_fail_the_page() {
            var ok = await InsertFrameAsync("ok", DateTimeOffset.UtcNow.AddMinutes(-1));
            var gone = await InsertFrameAsync("gone", DateTimeOffset.UtcNow);
            File.Delete(gone.Path);
            await _svc.ClaimAsync(new BackupStreamClaimRequestDto("wilma-desk"), CancellationToken.None);

            var queue = await _svc.GetQueueAsync("wilma-desk", 10, CancellationToken.None);
            Assert.That(queue!, Has.Count.EqualTo(2));
            Assert.That(queue[0].Sha256, Is.EqualTo(ok.Sha));
            Assert.That(queue[1].Sha256, Is.Null, "unreadable file → null sha, entry still listed");
        }
    }
}
