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
using OpenAstroAra.Server.Contracts;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>Rank 1 storage-integrity and lifecycle implementation.</summary>
public sealed partial class SqliteFrameRepository {
    private const int MaxFailureCodeLength = 64;
    private const int MaxFailureMessageLength = 512;

    public async Task BeginStorageAsync(FrameStorageAttempt attempt, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.FrameId == Guid.Empty) throw new ArgumentException("FrameId must not be empty.", nameof(attempt));
        if (attempt.SessionId == Guid.Empty) throw new ArgumentException("SessionId must not be empty.", nameof(attempt));
        var temporaryPath = RequirePath(attempt.TemporaryPath, nameof(attempt));
        var finalPath = RequirePath(attempt.FinalPath, nameof(attempt));
        var imageFormat = NormalizeImageFormat(attempt.ImageFormat, nameof(attempt));
        var acceptedUtc = attempt.AcceptedUtc.ToUniversalTime();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO frame_storage_lifecycle
                (frame_id, session_id, accepted_utc, completed_utc,
                 temporary_path, final_path, byte_count, checksum_sha256,
                 image_format, cfa_pattern, state, failure_code,
                 failure_message, updated_utc)
            VALUES
                ($frame_id, $session_id, $accepted_utc, NULL,
                 $temporary_path, $final_path, NULL, NULL,
                 $image_format, NULL, 'accepted', NULL, NULL, $updated_utc);
            """;
        cmd.Parameters.AddWithValue("$frame_id", attempt.FrameId.ToString("D"));
        cmd.Parameters.AddWithValue("$session_id", attempt.SessionId.ToString("D"));
        cmd.Parameters.AddWithValue("$accepted_utc", acceptedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$temporary_path", temporaryPath);
        cmd.Parameters.AddWithValue("$final_path", finalPath);
        cmd.Parameters.AddWithValue("$image_format", imageFormat);
        cmd.Parameters.AddWithValue("$updated_utc", acceptedUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AdvanceStorageAsync(Guid frameId, FrameStorageState state, CancellationToken ct) {
        if (frameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        if (state is not (FrameStorageState.Exposing or FrameStorageState.Downloading or FrameStorageState.Persisting)) {
            throw new ArgumentOutOfRangeException(nameof(state), state,
                "AdvanceStorageAsync only accepts Exposing, Downloading, or Persisting.");
        }

        await using var conn = _db.OpenConnection();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await ReadStateAsync(conn, transaction, frameId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No storage lifecycle exists for frame {frameId:D}.");

        if (current == state) {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        if (IsTerminal(current)) {
            throw new InvalidOperationException(
                $"Frame {frameId:D} storage is terminal ({StateToString(current)}); it cannot advance.");
        }
        if (StateRank(state) <= StateRank(current)) {
            throw new InvalidOperationException(
                $"Frame {frameId:D} storage cannot move backward from {StateToString(current)} to {StateToString(state)}.");
        }

        await using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE frame_storage_lifecycle
            SET state = $next, updated_utc = $updated_utc
            WHERE frame_id = $frame_id AND state = $current;
            """;
        update.Parameters.AddWithValue("$next", StateToString(state));
        update.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        update.Parameters.AddWithValue("$current", StateToString(current));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) {
            throw new InvalidOperationException($"Frame {frameId:D} storage changed concurrently; retry the operation.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteStorageAsync(FrameDto frame, FrameStorageCompletion completion, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(completion);
        if (frame.Id == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frame));
        if (completion.ByteCount <= 0 || completion.ByteCount != frame.FileSizeBytes) {
            throw new ArgumentException("Completion byte count must be positive and equal FrameDto.FileSizeBytes.", nameof(completion));
        }
        var checksum = NormalizeChecksum(completion.ChecksumSha256, nameof(completion));
        var imageFormat = NormalizeImageFormat(completion.ImageFormat, nameof(completion));
        var completedUtc = completion.CompletedUtc.ToUniversalTime();
        var cfaPattern = NormalizeCfaPattern(completion.CfaPattern);

        await using var conn = _db.OpenConnection();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await ReadCompletionGateAsync(conn, transaction, frame.Id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No storage lifecycle exists for frame {frame.Id:D}.");

        if (existing.SessionId != frame.SessionId) {
            throw new InvalidOperationException("Completed frame session does not match its storage lifecycle session.");
        }
        if (!PathsEqual(existing.FinalPath, frame.FilePath)) {
            throw new InvalidOperationException("Completed frame path does not match its storage lifecycle final path.");
        }
        if (!string.Equals(existing.ImageFormat, imageFormat, StringComparison.Ordinal)) {
            throw new InvalidOperationException("Completed frame format does not match its storage lifecycle format.");
        }
        if (existing.State == FrameStorageState.Complete) {
            if (await CompletedFrameMatchesAsync(conn, transaction, frame, checksum, ct).ConfigureAwait(false)) {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException($"Frame {frame.Id:D} has conflicting completed storage metadata.");
        }
        if (IsTerminal(existing.State)) {
            throw new InvalidOperationException(
                $"Frame {frame.Id:D} storage is terminal ({StateToString(existing.State)}); it cannot complete.");
        }

        await InsertFrameAsync(conn, frame, ct, transaction, checksum).ConfigureAwait(false);
        await using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE frame_storage_lifecycle
            SET completed_utc = $completed_utc,
                temporary_path = NULL,
                byte_count = $byte_count,
                checksum_sha256 = $checksum_sha256,
                image_format = $image_format,
                cfa_pattern = $cfa_pattern,
                state = 'complete',
                failure_code = NULL,
                failure_message = NULL,
                updated_utc = $completed_utc
            WHERE frame_id = $frame_id
              AND state IN ('accepted', 'exposing', 'downloading', 'persisting');
            """;
        update.Parameters.AddWithValue("$completed_utc", completedUtc.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$byte_count", completion.ByteCount);
        update.Parameters.AddWithValue("$checksum_sha256", checksum);
        update.Parameters.AddWithValue("$image_format", imageFormat);
        update.Parameters.AddWithValue("$cfa_pattern", (object?)cfaPattern ?? DBNull.Value);
        update.Parameters.AddWithValue("$frame_id", frame.Id.ToString("D"));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) {
            throw new InvalidOperationException($"Frame {frame.Id:D} storage changed concurrently; completion was rolled back.");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await PublishFrameCompleteAsync(frame, ct).ConfigureAwait(false);
    }

    public async Task FailStorageAsync(Guid frameId, FrameStorageFailure failure, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(failure);
        if (frameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        var code = NormalizeFailureCode(failure.Code);
        var message = NormalizeFailureMessage(failure.Message);
        var failedUtc = failure.FailedUtc.ToUniversalTime();

        await using var conn = _db.OpenConnection();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await ReadStateAsync(conn, transaction, frameId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No storage lifecycle exists for frame {frameId:D}.");
        if (current == FrameStorageState.Failed) {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }
        if (current is FrameStorageState.Complete or FrameStorageState.Partial) {
            throw new InvalidOperationException(
                $"Frame {frameId:D} storage is terminal ({StateToString(current)}); it cannot fail.");
        }

        await using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE frame_storage_lifecycle
            SET state = 'failed',
                failure_code = $failure_code,
                failure_message = $failure_message,
                updated_utc = $failed_utc
            WHERE frame_id = $frame_id
              AND state IN ('accepted', 'exposing', 'downloading', 'persisting');
            """;
        update.Parameters.AddWithValue("$failure_code", code);
        update.Parameters.AddWithValue("$failure_message", message);
        update.Parameters.AddWithValue("$failed_utc", failedUtc.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) {
            throw new InvalidOperationException($"Frame {frameId:D} storage changed concurrently; failure was not recorded.");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<FrameStorageRecord?> GetStorageAsync(Guid frameId, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT frame_id, session_id, accepted_utc, completed_utc,
                   temporary_path, final_path, byte_count, checksum_sha256,
                   image_format, cfa_pattern, state, failure_code,
                   failure_message, updated_utc
            FROM frame_storage_lifecycle
            WHERE frame_id = $frame_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new FrameStorageRecord(
            FrameId: Guid.Parse(reader.GetString(0)),
            SessionId: Guid.Parse(reader.GetString(1)),
            AcceptedUtc: ParseUtc(reader.GetString(2)),
            CompletedUtc: await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : ParseUtc(reader.GetString(3)),
            TemporaryPath: await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4),
            FinalPath: reader.GetString(5),
            ByteCount: await reader.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : reader.GetInt64(6),
            ChecksumSha256: await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetString(7),
            ImageFormat: reader.GetString(8),
            CfaPattern: await reader.IsDBNullAsync(9, ct).ConfigureAwait(false) ? null : reader.GetString(9),
            State: ParseState(reader.GetString(10)),
            FailureCode: await reader.IsDBNullAsync(11, ct).ConfigureAwait(false) ? null : reader.GetString(11),
            FailureMessage: await reader.IsDBNullAsync(12, ct).ConfigureAwait(false) ? null : reader.GetString(12),
            UpdatedUtc: ParseUtc(reader.GetString(13)));
    }

    private static async Task InsertCompletedStorageAsync(SqliteConnection conn, SqliteTransaction transaction,
            FrameDto frame, string? checksumSha256, string imageFormat, string? cfaPattern,
            DateTimeOffset completedUtc, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO frame_storage_lifecycle
                (frame_id, session_id, accepted_utc, completed_utc,
                 temporary_path, final_path, byte_count, checksum_sha256,
                 image_format, cfa_pattern, state, failure_code,
                 failure_message, updated_utc)
            VALUES
                ($frame_id, $session_id, $accepted_utc, $completed_utc,
                 NULL, $final_path, $byte_count, $checksum_sha256,
                 $image_format, $cfa_pattern, 'complete', NULL, NULL, $completed_utc)
            ON CONFLICT(frame_id) DO UPDATE SET
                session_id = excluded.session_id,
                completed_utc = excluded.completed_utc,
                temporary_path = NULL,
                final_path = excluded.final_path,
                byte_count = excluded.byte_count,
                checksum_sha256 = excluded.checksum_sha256,
                image_format = excluded.image_format,
                cfa_pattern = excluded.cfa_pattern,
                state = 'complete',
                failure_code = NULL,
                failure_message = NULL,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$frame_id", frame.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$session_id", frame.SessionId.ToString("D"));
        cmd.Parameters.AddWithValue("$accepted_utc", frame.CapturedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$completed_utc", completedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$final_path", frame.FilePath);
        cmd.Parameters.AddWithValue("$byte_count", frame.FileSizeBytes);
        cmd.Parameters.AddWithValue("$checksum_sha256", (object?)checksumSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$image_format", imageFormat);
        cmd.Parameters.AddWithValue("$cfa_pattern", (object?)cfaPattern ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task BackfillCompletedStorageAsync(SqliteConnection conn, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO frame_storage_lifecycle
                (frame_id, session_id, accepted_utc, completed_utc,
                 temporary_path, final_path, byte_count, checksum_sha256,
                 image_format, cfa_pattern, state, failure_code,
                 failure_message, updated_utc)
            SELECT id, session_id, captured_utc, captured_utc,
                   NULL, file_path, file_size_bytes, sha256,
                   'fits', NULL, 'complete', NULL, NULL, captured_utc
            FROM frames;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<FrameStorageState?> ReadStateAsync(SqliteConnection conn,
            SqliteTransaction transaction, Guid frameId, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT state FROM frame_storage_lifecycle WHERE frame_id = $frame_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string text ? ParseState(text) : null;
    }

    private sealed record CompletionGate(
        FrameStorageState State,
        Guid SessionId,
        string FinalPath,
        string ImageFormat);

    private static async Task<CompletionGate?> ReadCompletionGateAsync(SqliteConnection conn,
            SqliteTransaction transaction, Guid frameId, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT state, session_id, final_path, image_format FROM frame_storage_lifecycle WHERE frame_id = $frame_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new CompletionGate(ParseState(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static async Task<bool> CompletedFrameMatchesAsync(SqliteConnection conn,
            SqliteTransaction transaction, FrameDto frame, string checksum, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT sha256, file_path, session_id, file_size_bytes FROM frames WHERE id = $frame_id LIMIT 1;";
        cmd.Parameters.AddWithValue("$frame_id", frame.Id.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            && !await reader.IsDBNullAsync(0, ct).ConfigureAwait(false)
            && string.Equals(reader.GetString(0), checksum, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(reader.GetString(1), frame.FilePath)
            && Guid.Parse(reader.GetString(2)) == frame.SessionId
            && reader.GetInt64(3) == frame.FileSizeBytes;
    }

    private static bool IsTerminal(FrameStorageState state) =>
        state is FrameStorageState.Complete or FrameStorageState.Failed or FrameStorageState.Partial;

    private static int StateRank(FrameStorageState state) => state switch {
        FrameStorageState.Accepted => 0,
        FrameStorageState.Exposing => 1,
        FrameStorageState.Downloading => 2,
        FrameStorageState.Persisting => 3,
        _ => int.MaxValue,
    };

    private static string StateToString(FrameStorageState state) => state switch {
        FrameStorageState.Accepted => "accepted",
        FrameStorageState.Exposing => "exposing",
        FrameStorageState.Downloading => "downloading",
        FrameStorageState.Persisting => "persisting",
        FrameStorageState.Complete => "complete",
        FrameStorageState.Failed => "failed",
        FrameStorageState.Partial => "partial",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static FrameStorageState ParseState(string state) => state switch {
        "accepted" => FrameStorageState.Accepted,
        "exposing" => FrameStorageState.Exposing,
        "downloading" => FrameStorageState.Downloading,
        "persisting" => FrameStorageState.Persisting,
        "complete" => FrameStorageState.Complete,
        "failed" => FrameStorageState.Failed,
        "partial" => FrameStorageState.Partial,
        _ => throw new InvalidDataException($"Unknown frame storage state '{state}'."),
    };

    private static string RequirePath(string value, string paramName) {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Storage path must not be empty.", paramName);
        return Path.GetFullPath(value.Trim());
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeImageFormat(string value, string paramName) {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Image format must not be empty.", paramName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 16 || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')) {
            throw new ArgumentException("Image format must use 1-16 ASCII letters, digits, or underscores.", paramName);
        }
        return normalized;
    }

    private static string NormalizeChecksum(string value, string paramName) {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch))) {
            throw new ArgumentException("ChecksumSha256 must contain exactly 64 hexadecimal characters.", paramName);
        }
        return value.ToLowerInvariant();
    }

    private static string? NormalizeCfaPattern(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > 16 || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch))) {
            throw new ArgumentException("CfaPattern must use at most 16 ASCII letters or digits.", nameof(value));
        }
        return normalized;
    }

    private static string NormalizeFailureCode(string value) {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Failure code must not be empty.", nameof(value));
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > MaxFailureCodeLength
            || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-' or '.'))) {
            throw new ArgumentException("Failure code must use at most 64 ASCII letters, digits, '_', '-', or '.'.", nameof(value));
        }
        return normalized;
    }

    private static string NormalizeFailureMessage(string value) {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Failure message must not be empty.", nameof(value));
        var normalized = value.Trim();
        return normalized.Length <= MaxFailureMessageLength
            ? normalized
            : normalized[..MaxFailureMessageLength];
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}