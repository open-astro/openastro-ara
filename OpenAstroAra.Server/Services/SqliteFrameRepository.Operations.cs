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
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>Rank 1 frame metadata, derived-work lifecycle, and quarantine operations.</summary>
public sealed partial class SqliteFrameRepository {
    private sealed class OperationLockState {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int References { get; set; }
    }

    private readonly object _operationLocksGate = new();
    private readonly Dictionary<string, OperationLockState> _operationLocks =
        new(StringComparer.Ordinal);
    private readonly IdempotencyCache<OperationAcceptedDto> _bulkRateOperations = new();
    private readonly IdempotencyCache<OperationAcceptedDto> _bulkTagOperations = new();
    private readonly IdempotencyCache<OperationAcceptedDto> _bulkMoveOperations = new();
    private readonly IdempotencyCache<OperationAcceptedDto> _bulkDeleteOperations = new();
    private readonly IdempotencyCache<OperationAcceptedDto> _bulkQuarantineOperations = new();

    public async Task<FrameMetadataResult?> GetMetadataAsync(Guid id, CancellationToken ct) {
        if (id == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(id));
        var frame = await GetAsync(id, ct).ConfigureAwait(false);
        if (frame is null) return null;

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sha256, analysis_state, analysis_failure_code,
                   analysis_failure_message, preview_state,
                   preview_failure_code, preview_failure_message,
                   preview_checksum, debayer_method, preview_version
            FROM frames
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var storage = await GetStorageAsync(id, ct).ConfigureAwait(false);
        var catalogChecksum = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false)
            ? null
            : reader.GetString(0);
        return new FrameMetadataResult(
            Frame: frame,
            Storage: storage,
            SourceExists: File.Exists(frame.FilePath),
            SourceChecksumSha256: storage?.ChecksumSha256 ?? catalogChecksum,
            ImageFormat: storage?.ImageFormat,
            CfaPattern: storage?.CfaPattern,
            AnalysisState: ReadNullableString(reader, 1),
            AnalysisFailureCode: ReadNullableString(reader, 2),
            AnalysisFailureMessage: ReadNullableString(reader, 3),
            PreviewState: ReadNullableString(reader, 4),
            PreviewFailureCode: ReadNullableString(reader, 5),
            PreviewFailureMessage: ReadNullableString(reader, 6),
            PreviewChecksum: ReadNullableString(reader, 7),
            DebayerMethod: ReadNullableString(reader, 8),
            PreviewVersion: ReadNullableString(reader, 9));
    }

    public async Task BeginAnalysisAsync(Guid frameId, CancellationToken ct) {
        var sessionId = await UpdateAnalysisStateAsync(frameId, "analyzing",
            failureCode: null, failureMessage: null, starCount: null, ct).ConfigureAwait(false);
        if (sessionId is { } session) {
            await PublishFrameStateEventAsync(WsEventCatalog.FrameAnalysisStarted,
                frameId, session, "analyzing", ct).ConfigureAwait(false);
        }
    }

    public async Task RecordAnalysisSkippedAsync(Guid frameId, int starCount,
            string code, string message, CancellationToken ct) {
        ArgumentOutOfRangeException.ThrowIfNegative(starCount);
        var normalizedCode = NormalizeFailureCode(code);
        var normalizedMessage = NormalizeFailureMessage(message);
        var sessionId = await UpdateAnalysisStateAsync(frameId, "skipped",
            normalizedCode, normalizedMessage, starCount, ct).ConfigureAwait(false);
        if (sessionId is not { } session) return;
        var payload = FramePayload(frameId, session);
        payload["state"] = "skipped";
        payload["persisted"] = false;
        payload["star_count"] = starCount;
        payload["code"] = normalizedCode;
        payload["message"] = normalizedMessage;
        await PublishFrameEventAsync(WsEventCatalog.FrameAnalyzed, payload, ct)
            .ConfigureAwait(false);
    }

    public async Task FailAnalysisAsync(Guid frameId, string code, string message,
            CancellationToken ct) {
        var normalizedCode = NormalizeFailureCode(code);
        var normalizedMessage = NormalizeFailureMessage(message);
        var sessionId = await UpdateAnalysisStateAsync(frameId, "failed",
            normalizedCode, normalizedMessage, starCount: null, ct).ConfigureAwait(false);
        if (sessionId is { } session) {
            await PublishFrameFailureAsync(frameId, session, "analysis",
                normalizedCode, normalizedMessage, ct).ConfigureAwait(false);
        }
    }

    public Task<FramePreviewResult?> RebuildPreviewAsync(Guid id,
            FramePreviewRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        FrameOperationService.ValidatePreviewRequest(request);
        return RunFrameOperationLockedAsync("preview", id, async () => {
            var source = await GetPreviewSourceAsync(id, ct).ConfigureAwait(false);
            if (source is null) return null;
            await _previewImages.DeleteFrameEntriesAsync(id, ct).ConfigureAwait(false);
            return await RenderPreviewCoreAsync(id, request, announceStart: true, ct)
                .ConfigureAwait(false);
        }, ct);
    }

    public Task<FrameReanalysisResult?> ReanalyzeAsync(Guid id,
            FrameReanalysisRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        FrameOperationService.ValidateReanalysisRequest(request);
        return RunFrameOperationLockedAsync("analysis", id, async () => {
            var source = await GetPreviewSourceAsync(id, ct).ConfigureAwait(false);
            if (source is null) return null;
            if (!File.Exists(source.FilePath)) throw new FrameSourceUnavailableException(id);

            await BeginAnalysisAsync(id, ct).ConfigureAwait(false);
            try {
                var image = await _sourceImages.LoadAsync(source.FilePath, ct).ConfigureAwait(false);
                var measurement = CameraService.AnalyzePixels(image.Data.FlatArray,
                    image.Width, image.Height, image.CfaPattern,
                    request.StarSensitivity ?? 8.0,
                    request.StarNoiseReduction ?? 0, ct);
                if (measurement.StarCount < CameraService.MinStarsForAnalysis
                    || !double.IsFinite(measurement.Hfr) || measurement.Hfr <= 0) {
                    await RecordAnalysisSkippedAsync(id, measurement.StarCount,
                        "insufficient_stars", "Too few usable stars were detected.",
                        CancellationToken.None).ConfigureAwait(false);
                    return new FrameReanalysisResult(id, measurement, Persisted: false,
                        Warning: "Too few usable stars were detected.");
                }
                await UpdateAnalysisAsync(id, measurement, CancellationToken.None)
                    .ConfigureAwait(false);
                return new FrameReanalysisResult(id, measurement, Persisted: true, Warning: null);
            } catch (OperationCanceledException) {
                await FailAnalysisAsync(id, "analysis_cancelled",
                    "Frame analysis was cancelled.", CancellationToken.None).ConfigureAwait(false);
                throw;
            } catch {
                await FailAnalysisAsync(id, "analysis_failed",
                    "Frame analysis failed.", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, ct);
    }

    public Task<OperationAcceptedDto> BulkQuarantineAsync(BulkQuarantineRequestDto request,
            string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var ids = ValidateFrameIds(request.FrameIds);
        var reason = NormalizeQuarantineReason(request.Reason, request.Quarantined);
        var fingerprint = FingerprintBulk("quarantine", ids,
            request.Quarantined ? "true" : "false", reason ?? "");
        return _bulkQuarantineOperations.GetOrRunAsync(idempotencyKey, fingerprint,
            () => BulkQuarantineCoreAsync(ids, request.Quarantined, reason,
                idempotencyKey, ct));
    }

    private async Task<OperationAcceptedDto> BulkQuarantineCoreAsync(
            IReadOnlyList<Guid> frameIds, bool quarantined, string? reason,
            string? idempotencyKey, CancellationToken ct) {
        var changed = new List<(Guid FrameId, Guid SessionId)>();
        await using var conn = _db.OpenConnection();
        await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)
                .ConfigureAwait(false)) {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE frames
                SET quarantined_utc = $quarantined_utc,
                    quarantine_reason = $quarantine_reason
                WHERE id = $id
                  AND (($quarantined = 1
                        AND (quarantined_utc IS NULL
                             OR quarantine_reason IS NOT $quarantine_reason))
                       OR ($quarantined = 0
                           AND (quarantined_utc IS NOT NULL
                                OR quarantine_reason IS NOT NULL)))
                RETURNING session_id;
                """;
            cmd.Parameters.AddWithValue("$quarantined", quarantined ? 1 : 0);
            cmd.Parameters.AddWithValue("$quarantined_utc",
                quarantined ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$quarantine_reason",
                quarantined ? (object?)reason ?? DBNull.Value : DBNull.Value);
            var idParameter = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var frameId in frameIds) {
                idParameter.Value = frameId.ToString("D");
                var sessionValue = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (sessionValue is string sessionText) {
                    changed.Add((frameId, Guid.Parse(sessionText)));
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        foreach (var item in changed) {
            var payload = FramePayload(item.FrameId, item.SessionId);
            payload["quarantined"] = quarantined;
            payload["reason"] = quarantined ? reason : null;
            await PublishFrameEventAsync(WsEventCatalog.FrameQuarantined, payload,
                CancellationToken.None).ConfigureAwait(false);
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-quarantine", idempotencyKey);
    }

    private async Task<Guid?> UpdateAnalysisStateAsync(Guid frameId, string state,
            string? failureCode, string? failureMessage, int? starCount,
            CancellationToken ct) {
        if (frameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE frames
            SET analysis_state = $state,
                analysis_failure_code = $failure_code,
                analysis_failure_message = $failure_message,
                star_count = CASE WHEN $set_star_count = 1 THEN $star_count ELSE star_count END,
                hfr = CASE WHEN $clear_measurements = 1 THEN NULL ELSE hfr END,
                eccentricity = CASE WHEN $clear_measurements = 1 THEN NULL ELSE eccentricity END,
                snr_estimate = CASE WHEN $clear_measurements = 1 THEN NULL ELSE snr_estimate END,
                analysis_version = CASE WHEN $clear_measurements = 1 THEN $analysis_version ELSE analysis_version END
            WHERE id = $id
            RETURNING session_id;
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$failure_code", (object?)failureCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failure_message", (object?)failureMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$set_star_count", starCount.HasValue ? 1 : 0);
        cmd.Parameters.AddWithValue("$star_count", starCount.HasValue ? starCount.Value : DBNull.Value);
        var clearMeasurements = string.Equals(state, "skipped", StringComparison.Ordinal);
        cmd.Parameters.AddWithValue("$clear_measurements", clearMeasurements ? 1 : 0);
        cmd.Parameters.AddWithValue("$analysis_version", CameraService.ManagedAnalysisVersion);
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string sessionText ? Guid.Parse(sessionText) : null;
    }

    private async Task SetPreviewStateAsync(Guid frameId, string state,
            string? failureCode, string? failureMessage, string? checksum,
            string? debayerMethod, string? previewVersion, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE frames
            SET preview_state = $state,
                preview_failure_code = $failure_code,
                preview_failure_message = $failure_message,
                preview_checksum = $checksum,
                debayer_method = $debayer_method,
                preview_version = $preview_version
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$failure_code", (object?)failureCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failure_message", (object?)failureMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$checksum", (object?)checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$debayer_method", (object?)debayerMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preview_version", (object?)previewVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> RunFrameOperationLockedAsync<T>(string operation, Guid frameId,
            Func<Task<T>> work, CancellationToken ct) {
        if (frameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);
        var key = $"{operation}:{frameId:D}";
        OperationLockState state;
        lock (_operationLocksGate) {
            if (!_operationLocks.TryGetValue(key, out state!)) {
                state = new OperationLockState();
                _operationLocks.Add(key, state);
            }
            state.References++;
        }

        var acquired = false;
        try {
            await state.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            return await work().ConfigureAwait(false);
        } finally {
            if (acquired) state.Semaphore.Release();
            lock (_operationLocksGate) {
                state.References--;
                if (state.References == 0) {
                    _operationLocks.Remove(key);
                    state.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task PublishFrameStateEventAsync(string eventType, Guid frameId,
            Guid sessionId, string state, CancellationToken ct) {
        var payload = FramePayload(frameId, sessionId);
        payload["state"] = state;
        await PublishFrameEventAsync(eventType, payload, ct).ConfigureAwait(false);
    }

    private async Task PublishPreviewReadyAsync(Guid frameId, Guid sessionId,
            FramePreviewResult preview, string checksum, CancellationToken ct) {
        var payload = FramePayload(frameId, sessionId);
        payload["state"] = "ready";
        payload["preview_checksum"] = checksum;
        payload["cache_key"] = preview.Metadata.CacheKey;
        payload["cache_hit"] = preview.CacheHit;
        payload["width"] = preview.Metadata.Width;
        payload["height"] = preview.Metadata.Height;
        payload["debayer_method"] = preview.Metadata.DebayerMode;
        payload["preview_version"] = $"schema-{preview.Metadata.SchemaVersion}";
        await PublishFrameEventAsync(WsEventCatalog.FramePreviewReady, payload, ct)
            .ConfigureAwait(false);
        await PublishFrameEventAsync(WsEventCatalog.FramePreviewReadyLegacy, payload, ct)
            .ConfigureAwait(false);
    }

    private async Task PublishFrameFailureAsync(Guid frameId, Guid sessionId,
            string stage, string code, string message, CancellationToken ct) {
        var payload = FramePayload(frameId, sessionId);
        payload["stage"] = stage;
        payload["code"] = code;
        payload["message"] = message;
        await PublishFrameEventAsync(WsEventCatalog.FrameFailed, payload, ct)
            .ConfigureAwait(false);
    }

    private Task PublishFramePersistStartedAsync(FrameStorageAttempt attempt,
            CancellationToken ct) {
        var payload = FramePayload(attempt.FrameId, attempt.SessionId);
        payload["state"] = "accepted";
        payload["progress"] = 0;
        return PublishFrameEventAsync(WsEventCatalog.FramePersistStarted, payload, ct);
    }

    private Task PublishFramePersistProgressAsync(FrameStorageRecord storage,
            CancellationToken ct) {
        var payload = FramePayload(storage.FrameId, storage.SessionId);
        payload["state"] = StateToString(storage.State);
        payload["progress"] = storage.State switch {
            FrameStorageState.Accepted => 0,
            FrameStorageState.Exposing => 0.1,
            FrameStorageState.Downloading => 0.6,
            FrameStorageState.Persisting => 0.85,
            FrameStorageState.Complete => 1,
            _ => 0,
        };
        payload["byte_count"] = storage.ByteCount;
        return PublishFrameEventAsync(WsEventCatalog.FramePersistProgress, payload, ct);
    }

    private static JsonObject FramePayload(Guid frameId, Guid sessionId) => new() {
        ["frame_id"] = frameId.ToString("D"),
        ["session_id"] = sessionId.ToString("D"),
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WebSocket publication is best-effort; a broadcaster fault must never roll back durable frame state.")]
    private async Task PublishFrameEventAsync(string eventType, JsonObject payload,
            CancellationToken ct) {
        if (_ws is null) return;
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFrameEventFailed(ex);
        }
    }

    private static List<Guid> ValidateFrameIds(IReadOnlyList<Guid>? frameIds) {
        if (frameIds is null || frameIds.Count == 0) {
            throw new ArgumentException("At least one frame id is required.", nameof(frameIds));
        }
        if (frameIds.Count > 1_000) {
            throw new ArgumentException("A bulk operation supports at most 1000 frame ids.",
                nameof(frameIds));
        }
        var normalized = new List<Guid>(frameIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var frameId in frameIds) {
            if (frameId == Guid.Empty) {
                throw new ArgumentException("Frame ids must not contain an empty id.",
                    nameof(frameIds));
            }
            if (seen.Add(frameId)) normalized.Add(frameId);
        }
        return normalized;
    }

    private static List<string> NormalizeTags(IReadOnlyList<string>? tags,
            string parameterName) {
        if (tags is null) throw new ArgumentNullException(parameterName);
        if (tags.Count > 100) {
            throw new ArgumentException("At most 100 tags are supported.", parameterName);
        }
        var normalized = new List<string>(tags.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags) {
            if (string.IsNullOrWhiteSpace(tag)) {
                throw new ArgumentException("Tags must not be empty.", parameterName);
            }
            var value = tag.Trim();
            if (value.Length > 64 || value.Any(char.IsControl)) {
                throw new ArgumentException(
                    "Tags must contain at most 64 non-control characters.", parameterName);
            }
            if (seen.Add(value)) normalized.Add(value);
        }
        return normalized;
    }

    private static string? NormalizeQuarantineReason(string? reason, bool quarantined) {
        if (!quarantined || string.IsNullOrWhiteSpace(reason)) return null;
        var normalized = reason.Trim();
        if (normalized.Length > MaxFailureMessageLength) {
            throw new ArgumentException("Quarantine reason must not exceed 512 characters.",
                nameof(reason));
        }
        return normalized;
    }

    private static string FingerprintBulk(string operation,
            IReadOnlyList<Guid> frameIds, params string[] values) {
        var canonical = new StringBuilder(operation);
        foreach (var frameId in frameIds.Order()) {
            canonical.Append('|').Append(frameId.ToString("D"));
        }
        foreach (var value in values) canonical.Append('|').Append(value);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}