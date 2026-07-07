#region "copyright"

/*
    Copyright (c) 2026 - present Open Astro contributors

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §44 real-time backup stream, server half. The desktop WILMA pulls: it claims the
/// single stream slot (§44.3 — one active target per daemon; the same hostname
/// re-claims idempotently after a crash, a different hostname takes over only when
/// the holder has been silent past <see cref="StaleClaimWindow"/>), polls the pending
/// queue (§44.6 — catalogued frames the active target hasn't acked, oldest first),
/// pulls the bytes via the existing <c>GET /api/v1/frames/{id}/download</c>, verifies
/// sha256, and acks. Frame hashes are computed lazily on first queue serve and cached
/// in the catalog's <c>sha256</c> column — this also backfills frames captured before
/// this feature existed. Frames acked to an EARLIER target re-enter the queue when a
/// different hostname claims (each target gets a complete mirror).
/// </summary>
public sealed partial class BackupStreamService : IBackupStreamService {

    private readonly IAraDatabase _db;
    private readonly ILogger<BackupStreamService> _logger;

    private readonly object _slot = new();
    private string? _activeHostname;
    private Guid _slotId;
    private DateTimeOffset _lastActivityUtc;

    /// <summary>How long a claim survives without queue/ack activity before another
    /// hostname may take over (a crashed WILMA must not wedge the slot forever).</summary>
    internal TimeSpan StaleClaimWindow { get; set; } = TimeSpan.FromMinutes(10);

    public BackupStreamService(IAraDatabase db, ILogger<BackupStreamService>? logger = null) {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? NullLogger<BackupStreamService>.Instance;
    }

    public async Task<BackupStreamStatusDto> GetStatusAsync(CancellationToken ct) {
        string? active;
        lock (_slot) {
            active = _activeHostname;
        }
        var (pending, synced, bytes) = await CountsAsync(active, ct).ConfigureAwait(false);
        return new BackupStreamStatusDto(
            Enabled: active is not null,
            ActiveTarget: active,
            PendingCount: pending,
            SyncedCount: synced,
            QueueSizeBytes: bytes);
    }

    public Task<BackupStreamClaimResultDto?> ClaimAsync(BackupStreamClaimRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var hostname = request.Hostname?.Trim();
        if (string.IsNullOrEmpty(hostname)) {
            throw new ArgumentException("hostname must be non-empty", nameof(request));
        }
        lock (_slot) {
            var now = DateTimeOffset.UtcNow;
            if (_activeHostname is null
                || string.Equals(_activeHostname, hostname, StringComparison.OrdinalIgnoreCase)
                || now - _lastActivityUtc > StaleClaimWindow) {
                var takeover = _activeHostname is not null
                    && !string.Equals(_activeHostname, hostname, StringComparison.OrdinalIgnoreCase);
                _activeHostname = hostname;
                _slotId = Guid.NewGuid();
                _lastActivityUtc = now;
                LogClaimed(hostname, takeover);
                return Task.FromResult<BackupStreamClaimResultDto?>(new BackupStreamClaimResultDto(_slotId, hostname));
            }
            LogClaimRefused(hostname, _activeHostname);
            return Task.FromResult<BackupStreamClaimResultDto?>(null);
        }
    }

    /// <summary>The holder's hostname when a claim is refused — for the endpoint's 409 detail.</summary>
    public string? ActiveTargetSnapshot {
        get { lock (_slot) { return _activeHostname; } }
    }

    public Task<bool> ReleaseAsync(BackupStreamClaimRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        lock (_slot) {
            if (_activeHostname is not null
                && string.Equals(_activeHostname, request.Hostname?.Trim(), StringComparison.OrdinalIgnoreCase)) {
                LogReleased(_activeHostname);
                _activeHostname = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public async Task<IReadOnlyList<BackupStreamQueueEntryDto>?> GetQueueAsync(string hostname, int limit, CancellationToken ct) {
        if (!TouchHolder(hostname)) {
            return null;
        }
        limit = Math.Clamp(limit, 1, 500);
        string active;
        lock (_slot) { active = _activeHostname!; }

        var entries = new List<(Guid Id, string? Sha, long Size, string Captured, Guid Session, string Path)>();
        await using (var conn = _db.OpenConnection()) {
            await using var cmd = conn.CreateCommand();
            // Pending = not yet acked BY THE ACTIVE TARGET: a frame synced to some
            // earlier target still needs to reach this one (§44.1 — the mirror is
            // per-desktop). NULL-safe compare because sync_target is null pre-ack.
            cmd.CommandText = """
                SELECT id, sha256, file_size_bytes, captured_utc, session_id, file_path
                FROM frames
                WHERE synced_at IS NULL OR sync_target IS NULL OR sync_target <> $target
                ORDER BY captured_utc ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$target", active);
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
                entries.Add((
                    Guid.Parse(reader.GetString(0)),
                    await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
                    await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? 0 : reader.GetInt64(2),
                    reader.GetString(3),
                    Guid.Parse(reader.GetString(4)),
                    await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? string.Empty : reader.GetString(5)));
            }
        }

        var result = new List<BackupStreamQueueEntryDto>(entries.Count);
        foreach (var e in entries) {
            ct.ThrowIfCancellationRequested();
            var sha = e.Sha ?? await ComputeAndCacheShaAsync(e.Id, e.Path, ct).ConfigureAwait(false);
            result.Add(new BackupStreamQueueEntryDto(
                Id: e.Id,
                Sha256: sha,
                SizeBytes: e.Size,
                CapturedAt: DateTimeOffset.Parse(e.Captured, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                SessionId: e.Session));
        }
        return result;
    }

    public async Task<bool> AckAsync(string hostname, BackupStreamAckRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (!TouchHolder(hostname)) {
            return false;
        }
        string active;
        lock (_slot) { active = _activeHostname!; }
        if (!request.Sha256Verified) {
            // §44.9 — a mismatch means WILMA re-requests; an unverified ack is a
            // client bug, refused so the frame stays queued.
            LogUnverifiedAckRefused(request.FrameId, active);
            return false;
        }
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE frames
            SET sync_target = $target, synced_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$target", active);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", request.FrameId.ToString());
        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0) {
            LogAcked(request.FrameId, active);
        }
        return rows > 0;
    }

    // Verifies the caller holds the slot and stamps the activity clock (the stale
    // window keys off queue/ack traffic — a live puller is never displaced).
    private bool TouchHolder(string hostname) {
        lock (_slot) {
            if (_activeHostname is null
                || !string.Equals(_activeHostname, hostname?.Trim(), StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            _lastActivityUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Hash backfill is best-effort per frame: a missing/locked FITS file must not fail the whole queue page — the entry is served with a null sha and WILMA skips it until readable. Logged.")]
    private async Task<string?> ComputeAndCacheShaAsync(Guid frameId, string path, CancellationToken ct) {
        try {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                return null;
            }
            string sha;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)) {
                var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
                sha = Convert.ToHexStringLower(hash);
            }
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE frames SET sha256 = $sha WHERE id = $id AND sha256 IS NULL;";
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$id", frameId.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return sha;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogShaFailed(frameId, ex);
            return null;
        }
    }

    private async Task<(int Pending, int Synced, long Bytes)> CountsAsync(string? active, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                SUM(CASE WHEN synced_at IS NULL OR sync_target IS NULL OR sync_target <> $target THEN 1 ELSE 0 END),
                SUM(CASE WHEN synced_at IS NOT NULL AND sync_target = $target THEN 1 ELSE 0 END),
                SUM(CASE WHEN synced_at IS NULL OR sync_target IS NULL OR sync_target <> $target THEN COALESCE(file_size_bytes, 0) ELSE 0 END)
            FROM frames;
            """;
        // With no active target the whole catalog is "pending" for whichever
        // desktop claims next — matching the §44.8 storage-estimate flow.
        cmd.Parameters.AddWithValue("$target", (object?)active ?? "");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            return (
                await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? 0 : reader.GetInt32(0),
                await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? 0 : reader.GetInt32(1),
                await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? 0L : reader.GetInt64(2));
        }
        return (0, 0, 0L);
    }

    [LoggerMessage(EventId = 4401, Level = LogLevel.Information, Message = "§44 backup-stream slot claimed by {Hostname} (takeover={Takeover})")]
    private partial void LogClaimed(string hostname, bool takeover);

    [LoggerMessage(EventId = 4402, Level = LogLevel.Information, Message = "§44 backup-stream claim by {Hostname} refused — {Holder} holds the slot")]
    private partial void LogClaimRefused(string hostname, string holder);

    [LoggerMessage(EventId = 4403, Level = LogLevel.Information, Message = "§44 backup-stream slot released by {Hostname}")]
    private partial void LogReleased(string hostname);

    [LoggerMessage(EventId = 4404, Level = LogLevel.Debug, Message = "§44 frame {FrameId} acked by {Hostname}")]
    private partial void LogAcked(Guid frameId, string hostname);

    [LoggerMessage(EventId = 4405, Level = LogLevel.Warning, Message = "§44 unverified ack for frame {FrameId} from {Hostname} refused — the frame stays queued")]
    private partial void LogUnverifiedAckRefused(Guid frameId, string hostname);

    [LoggerMessage(EventId = 4406, Level = LogLevel.Warning, Message = "§44 sha256 backfill failed for frame {FrameId} (served with null sha; retried next page)")]
    private partial void LogShaFailed(Guid frameId, Exception exception);
}
