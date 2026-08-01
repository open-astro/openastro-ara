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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28-backed <see cref="IFrameRepository"/>. Read path queries SQLite;
/// Mutating bulk operations update catalog rows, previews and thumbnails render
/// bounded FITS/XISF source data, downloads stream original files, and the Rank 1 storage
/// ledger makes capture registration transactional and recoverable.
///
/// Seeding: on first init, if the <c>frames</c> table is empty, three
/// fixture rows are inserted with the same Guids the placeholder repo
/// used so existing CI smoke gate probes + UI manual testing continue
/// to find the sample session + sample frames.
/// </summary>
public sealed partial class SqliteFrameRepository : IFrameRepository {
    private static readonly Guid SampleSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid[] SampleFrameIds = new[] {
        Guid.Parse("22222222-2222-2222-2222-222222222221"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("22222222-2222-2222-2222-222222222223"),
    };

    // 1×1 fallback for seeded fixtures and catalog rows whose source is missing.
    private const string PlaceholderJpegBase64 =
        "/9j/4AAQSkZJRgABAQAASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a" +
        "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIy" +
        "MjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCABAAEADASIA" +
        "AhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAr/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEB" +
        "AAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AKgAAAAA" +
        "AAAAA//Z";

    private static readonly byte[] PlaceholderJpegBytes =
        Convert.FromBase64String(PlaceholderJpegBase64);

    private readonly IAraDatabase _db;
    private readonly IProfileStore _profile;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<SqliteFrameRepository> _logger;
    private readonly ISourceImageDataFactory _sourceImages;
    private readonly IPreviewImageService _previewImages;

    public SqliteFrameRepository(IAraDatabase db, IProfileStore profile, IWsBroadcaster? ws = null,
            ILogger<SqliteFrameRepository>? logger = null, ISourceImageDataFactory? sourceImages = null,
            IPreviewImageService? previewImages = null) {
        _db = db;
        _profile = profile;
        _ws = ws;
        _logger = logger ?? NullLogger<SqliteFrameRepository>.Instance;
        _sourceImages = sourceImages ?? new SourceImageDataFactory(new HeadlessProfileService());
        _previewImages = previewImages ?? new PreviewImageService(
            Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "preview-cache"), _sourceImages);
    }

    /// <summary>
    /// Populate the catalog with fixture rows if the frames table is
    /// empty. Idempotent — re-running on a populated catalog is a no-op.
    /// Called from Program.cs after IAraDatabase.InitializeAsync.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM frames;";
        var count = (long)(await checkCmd.ExecuteScalarAsync(ct) ?? 0L);
        if (count > 0) return;

        // Sample session first so the frames' FK constraint is satisfied.
        await using (var sessionCmd = conn.CreateCommand()) {
            sessionCmd.CommandText = """
                INSERT OR IGNORE INTO sessions
                    (id, profile_id, sequence_json, started_at, ended_at,
                     recovery_needed, last_completed_instruction_id,
                     current_target_id, frame_count)
                VALUES
                    ($id, NULL, NULL, $started, $ended, 0, NULL, NULL, 3);
                """;
            sessionCmd.Parameters.AddWithValue("$id", SampleSessionId.ToString());
            sessionCmd.Parameters.AddWithValue("$started",
                new DateTimeOffset(2026, 5, 30, 3, 0, 0, TimeSpan.Zero).ToString("O"));
            sessionCmd.Parameters.AddWithValue("$ended",
                new DateTimeOffset(2026, 5, 30, 4, 30, 0, TimeSpan.Zero).ToString("O"));
            await sessionCmd.ExecuteNonQueryAsync(ct);
        }

        // Three fixture frames: two Lights + one Dark, all in the sample
        // session. Same Guids the prior placeholder used so existing CI
        // smoke probes + WILMA manual tests find the same fixtures.
        var qualityScore = new QualityScoreBreakdownDto(
            Composite: 0.87,
            HfrComponent: 0.92,
            StarCountComponent: 0.84,
            EccentricityComponent: 0.78,
            GuidingRmsComponent: 0.88,
            SnrComponent: 0.91,
            Explanation: "Good seeing + low RMS; HFR comfortably under target.");
        var qualityJson = JsonSerializer.Serialize(
            qualityScore, AraJsonSerializerContext.Default.QualityScoreBreakdownDto);

        await InsertFrameAsync(conn, new FrameDto(
            Id: SampleFrameIds[0],
            SessionId: SampleSessionId,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "L",
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 3, 14, 0, TimeSpan.Zero),
            FilePath: "/media/openastroara/M31/2026-05-30/light_180s_L_001.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: 1.85, StarCount: 412, Eccentricity: 0.32,
            GuidingRmsArcsec: 0.74, SnrEstimate: 45.2,
            QualityScore: qualityScore,
            Rating: 4,
            Tags: SampleTags),
            ct);

        await InsertFrameAsync(conn, new FrameDto(
            Id: SampleFrameIds[1],
            SessionId: SampleSessionId,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "R",
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 3, 17, 30, TimeSpan.Zero),
            FilePath: "/media/openastroara/M31/2026-05-30/light_180s_R_002.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: 2.10, StarCount: 388, Eccentricity: 0.41,
            GuidingRmsArcsec: 0.82, SnrEstimate: 38.5,
            QualityScore: null,
            Rating: 3,
            Tags: Array.Empty<string>()),
            ct);

        await InsertFrameAsync(conn, new FrameDto(
            Id: SampleFrameIds[2],
            SessionId: SampleSessionId,
            TargetName: "Dark library",
            FrameType: FrameType.Dark,
            FilterName: null,
            ExposureSeconds: 180,
            Gain: 100,
            Offset: 50,
            TemperatureC: -10.0,
            CapturedUtc: new DateTimeOffset(2026, 5, 30, 4, 0, 0, TimeSpan.Zero),
            FilePath: "/media/openastroara/darks/2026-05/dark_180s_001.fits",
            FileSizeBytes: 33_554_432,
            Width: 4144, Height: 2822, BitDepth: 16,
            Hfr: null, StarCount: null, Eccentricity: null,
            GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: Array.Empty<string>()),
            ct);

        await BackfillCompletedStorageAsync(conn, ct).ConfigureAwait(false);

        LogSeededFrames();
    }

    /// <inheritdoc />
    public async Task InsertAsync(FrameDto frame, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(frame);
        await using var conn = _db.OpenConnection();
        await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertFrameAsync(conn, frame, ct, transaction).ConfigureAwait(false);
        await InsertCompletedStorageAsync(conn, transaction, frame, checksumSha256: null,
            imageFormat: "fits", cfaPattern: null, completedUtc: frame.CapturedUtc, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await PublishFrameCompleteAsync(frame, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAnalysisAsync(Guid frameId, FrameAnalysisMeasurement measurement, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(measurement);
        ValidateAnalysis(measurement);
        await using var conn = _db.OpenConnection();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE frames
            SET hfr = $hfr,
                star_count = $star_count,
                eccentricity = $eccentricity,
                snr_estimate = $snr_estimate,
                analysis_version = $analysis_version
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$hfr", measurement.Hfr);
        cmd.Parameters.AddWithValue("$star_count", measurement.StarCount);
        cmd.Parameters.AddWithValue("$eccentricity", DbValue(measurement.Eccentricity));
        cmd.Parameters.AddWithValue("$snr_estimate", DbValue(measurement.SnrEstimate));
        cmd.Parameters.AddWithValue("$analysis_version", measurement.AnalysisVersion);
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        var updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (updated == 0) {
            // The frame vanished between capture and analysis (user delete) — nothing to report.
            return;
        }
        await PublishFrameAnalyzedAsync(frameId, measurement, ct).ConfigureAwait(false);
    }

    private static void ValidateAnalysis(FrameAnalysisMeasurement measurement) {
        if (!double.IsFinite(measurement.Hfr) || measurement.Hfr <= 0) {
            throw new ArgumentOutOfRangeException(nameof(measurement), "HFR must be finite and positive.");
        }
        if (measurement.StarCount < 0) {
            throw new ArgumentOutOfRangeException(nameof(measurement), "Star count must be non-negative.");
        }
        if (measurement.Eccentricity is { } eccentricity
            && (!double.IsFinite(eccentricity) || eccentricity is < 0 or > 1)) {
            throw new ArgumentOutOfRangeException(nameof(measurement),
                "Eccentricity must be finite and between 0 and 1.");
        }
        if (measurement.SnrEstimate is { } snr
            && (!double.IsFinite(snr) || snr < 0)) {
            throw new ArgumentOutOfRangeException(nameof(measurement),
                "SNR estimate must be finite and non-negative.");
        }
        if (string.IsNullOrWhiteSpace(measurement.AnalysisVersion)
            || measurement.AnalysisVersion.Length > 64) {
            throw new ArgumentException("Analysis version must contain 1 to 64 characters.",
                nameof(measurement));
        }
    }

    // §59.5 frame.analyzed — the post-capture star analysis landed; live listeners (library
    // strips showing the HFR badge) refresh the row without polling. Same raw-JSON posture
    // as frame.complete: numbers + a Guid are literal-safe.
    private async Task PublishFrameAnalyzedAsync(Guid frameId,
            FrameAnalysisMeasurement measurement, CancellationToken ct) {
        if (_ws is null) return;
        try {
            var eccentricity = measurement.Eccentricity?.ToString("0.####",
                System.Globalization.CultureInfo.InvariantCulture) ?? "null";
            var snr = measurement.SnrEstimate?.ToString("0.####",
                System.Globalization.CultureInfo.InvariantCulture) ?? "null";
            var version = JsonSerializer.Serialize(measurement.AnalysisVersion,
                AraJsonSerializerContext.Default.String);
            var json = $$"""
                {"frame_id":"{{frameId:D}}","hfr":{{measurement.Hfr.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}},"star_count":{{measurement.StarCount}},"eccentricity":{{eccentricity}},"snr_estimate":{{snr}},"analysis_version":{{version}}}
                """;
            using var doc = JsonDocument.Parse(json);
            await _ws.PublishAsync(WsEventCatalog.FrameAnalyzed, doc.RootElement.Clone(), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogFrameEventFailed(ex);
        }
    }

    // §60.9 frame.complete — catalogued in WsEventCatalog since Phase 7 but
    // never emitted until the WS-refresh slice: every frame landing through
    // the capture path (this method; the §28.8 rescan emits recovered_orphan
    // separately) notifies live listeners (WILMA calibration coverage,
    // library strips) so they refresh without polling.
    private async Task PublishFrameCompleteAsync(FrameDto frame, CancellationToken ct) {
        if (_ws is null) return;
        try {
            // Raw JSON like EmitVariantEvictedAsync below — anonymous types
            // aren't in the source-gen context (AOT). Guids/enum are literal-safe;
            // the filter name is user-influenced and gets escaped.
            var filter = frame.FilterName is null
                ? "null"
                : JsonSerializer.Serialize(frame.FilterName, AraJsonSerializerContext.Default.String);
            var json = $$"""
                {"frame_id":"{{frame.Id:D}}","session_id":"{{frame.SessionId:D}}","frame_type":"{{frame.FrameType.ToString().ToLowerInvariant()}}","filter_name":{{filter}}}
                """;
            using var doc = JsonDocument.Parse(json);
            await _ws.PublishAsync(WsEventCatalog.FrameComplete, doc.RootElement.Clone(), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Broadcasting is best-effort — a WS hiccup must not fail the catalog insert.
            LogFrameEventFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "frame.complete broadcast failed")]
    private partial void LogFrameEventFailed(Exception ex);

    // Lazily-created "manual capture" session for REST-initiated exposures. All callers share ONE
    // creation task, so no caller can observe the id before the INSERT committed (the sessions FK
    // on frames is enforced — PRAGMA foreign_keys=ON). A failed creation resets so the next call
    // retries.
    private Task<Guid>? _manualSessionTask;
    private readonly object _manualSessionGate = new();

    /// <inheritdoc />
    public Task<Guid> EnsureManualCaptureSessionAsync(CancellationToken ct) {
        Task<Guid> task;
        lock (_manualSessionGate) {
            _manualSessionTask ??= CreateManualCaptureSessionAsync();
            task = _manualSessionTask;
        }
        // Honor the caller's token for the WAIT (the shared creation itself is not cancellable —
        // a second caller must not have its session yanked by the first caller's timeout).
        return task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateRunSessionAsync(CancellationToken ct) {
        var sid = Guid.NewGuid();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions
                (id, profile_id, sequence_json, started_at, ended_at,
                 recovery_needed, last_completed_instruction_id,
                 current_target_id, frame_count)
            VALUES
                ($id, NULL, NULL, $now, NULL, 0, NULL, NULL, 0);
            """;
        cmd.Parameters.AddWithValue("$id", sid.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return sid;
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(Guid sessionId, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // ended_at IS NULL keeps this idempotent — a retry can't move the end time.
        cmd.CommandText = "UPDATE sessions SET ended_at = $now WHERE id = $id AND ended_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", sessionId.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<Guid> CreateManualCaptureSessionAsync() {
        try {
            var sid = Guid.NewGuid();
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions
                    (id, profile_id, sequence_json, started_at, ended_at,
                     recovery_needed, last_completed_instruction_id,
                     current_target_id, frame_count)
                VALUES
                    ($id, NULL, NULL, $now, NULL, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$id", sid.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return sid;
        } catch {
            lock (_manualSessionGate) {
                _manualSessionTask = null; // retry on the next call
            }
            throw;
        }
    }

    private static async Task InsertFrameAsync(SqliteConnection conn, FrameDto f, CancellationToken ct,
            SqliteTransaction? transaction = null, string? checksumSha256 = null) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO frames
                (id, session_id, target_name, frame_type, filter_name,
                 exposure_seconds, gain, "offset", temperature_c, captured_utc,
                 file_path, file_size_bytes, width, height, bit_depth,
                 hfr, star_count, eccentricity, guiding_rms_arcsec, snr_estimate,
                 quality_score_json, rating, tags_json, focuser_position, sha256)
            VALUES
                ($id, $session_id, $target_name, $frame_type, $filter_name,
                 $exposure_seconds, $gain, $offset, $temperature_c, $captured_utc,
                 $file_path, $file_size_bytes, $width, $height, $bit_depth,
                 $hfr, $star_count, $eccentricity, $guiding_rms_arcsec, $snr_estimate,
                 $quality_score_json, $rating, $tags_json, $focuser_position, $sha256);
            """;
        cmd.Parameters.AddWithValue("$id", f.Id.ToString());
        cmd.Parameters.AddWithValue("$session_id", f.SessionId.ToString());
        cmd.Parameters.AddWithValue("$target_name", f.TargetName);
        cmd.Parameters.AddWithValue("$frame_type", FrameTypeToString(f.FrameType));
        cmd.Parameters.AddWithValue("$filter_name", (object?)f.FilterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exposure_seconds", f.ExposureSeconds);
        cmd.Parameters.AddWithValue("$gain", DbValue(f.Gain));
        cmd.Parameters.AddWithValue("$offset", DbValue(f.Offset));
        cmd.Parameters.AddWithValue("$temperature_c", DbValue(f.TemperatureC));
        cmd.Parameters.AddWithValue("$captured_utc", f.CapturedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$file_path", f.FilePath);
        cmd.Parameters.AddWithValue("$file_size_bytes", f.FileSizeBytes);
        cmd.Parameters.AddWithValue("$width", f.Width);
        cmd.Parameters.AddWithValue("$height", f.Height);
        cmd.Parameters.AddWithValue("$bit_depth", f.BitDepth);
        cmd.Parameters.AddWithValue("$hfr", DbValue(f.Hfr));
        cmd.Parameters.AddWithValue("$star_count", DbValue(f.StarCount));
        cmd.Parameters.AddWithValue("$eccentricity", DbValue(f.Eccentricity));
        cmd.Parameters.AddWithValue("$guiding_rms_arcsec", DbValue(f.GuidingRmsArcsec));
        cmd.Parameters.AddWithValue("$snr_estimate", DbValue(f.SnrEstimate));
        cmd.Parameters.AddWithValue("$quality_score_json", f.QualityScore is null
            ? DBNull.Value
            : JsonSerializer.Serialize(f.QualityScore, AraJsonSerializerContext.Default.QualityScoreBreakdownDto));
        cmd.Parameters.AddWithValue("$rating", f.Rating);
        cmd.Parameters.AddWithValue("$tags_json",
            JsonSerializer.Serialize(f.Tags, AraJsonSerializerContext.Default.IReadOnlyListString));
        cmd.Parameters.AddWithValue("$focuser_position", DbValue(f.FocuserPosition));
        cmd.Parameters.AddWithValue("$sha256", (object?)checksumSha256 ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CursorPage<FrameListItemDto>> ListAsync(int limit, string? cursor, Guid? sessionId, string? targetName, CancellationToken ct) {
        // v0.0.1 cursor pagination: offset-based encoded as base-10 int.
        // Real keyset pagination over captured_utc lands when the catalog
        // gets large enough to matter (§60.2 leaves the cursor opaque).
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT id, session_id, target_name, frame_type, filter_name,
                   exposure_seconds, captured_utc, hfr, star_count,
                   quality_score_json, rating, synced_at, sync_target
            FROM frames
            WHERE 1=1
            """;
        if (sessionId is Guid sid) {
            sql += " AND session_id = $session_id";
            cmd.Parameters.AddWithValue("$session_id", sid.ToString());
        }
        if (!string.IsNullOrEmpty(targetName)) {
            sql += " AND target_name = $target_name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$target_name", targetName);
        }
        // pageSize + 1 so we know whether there's another page without a
        // separate COUNT query.
        sql += " ORDER BY captured_utc ASC LIMIT $limit OFFSET $offset;";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<FrameListItemDto>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        // Count check BEFORE the read — the reversed order consumed the LIMIT
        // pageSize+1 sentinel row inside the loop, so the has-more read below
        // always came up empty and every page reported has_more=false.
        while (items.Count < pageSize && await reader.ReadAsync(ct)) {
            double? composite = null;
            if (!await reader.IsDBNullAsync(9, ct)) {
                try {
                    var qs = JsonSerializer.Deserialize(
                        reader.GetString(9), AraJsonSerializerContext.Default.QualityScoreBreakdownDto);
                    composite = qs?.Composite;
                } catch (JsonException) { /* corrupt JSON → null composite */ }
            }
            items.Add(new FrameListItemDto(
                Id: Guid.Parse(reader.GetString(0)),
                SessionId: Guid.Parse(reader.GetString(1)),
                TargetName: reader.GetString(2),
                FrameType: ParseFrameType(reader.GetString(3)),
                FilterName: await reader.IsDBNullAsync(4, ct) ? null : reader.GetString(4),
                ExposureSeconds: reader.GetDouble(5),
                CapturedUtc: DateTimeOffset.Parse(reader.GetString(6)),
                Hfr: await reader.IsDBNullAsync(7, ct) ? null : reader.GetDouble(7),
                StarCount: await reader.IsDBNullAsync(8, ct) ? null : reader.GetInt32(8),
                CompositeQualityScore: composite,
                Rating: reader.GetInt32(10),
                SyncedAt: await reader.IsDBNullAsync(11, ct)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(11)),
                SyncTarget: await reader.IsDBNullAsync(12, ct) ? null : reader.GetString(12)));
        }
        var hasMore = await reader.ReadAsync(ct);  // the pageSize+1 row
        var nextCursor = hasMore ? (offset + pageSize).ToString() : null;
        return new CursorPage<FrameListItemDto>(items, NextCursor: nextCursor, HasMore: hasMore);
    }

    public async Task<FrameDto?> GetAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, target_name, frame_type, filter_name,
                   exposure_seconds, gain, "offset", temperature_c, captured_utc,
                   file_path, file_size_bytes, width, height, bit_depth,
                   hfr, star_count, eccentricity, guiding_rms_arcsec, snr_estimate,
                   quality_score_json, rating, tags_json, focuser_position,
                   analysis_version
            FROM frames WHERE id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        QualityScoreBreakdownDto? quality = null;
        if (!await reader.IsDBNullAsync(20, ct)) {
            try {
                quality = JsonSerializer.Deserialize(
                    reader.GetString(20), AraJsonSerializerContext.Default.QualityScoreBreakdownDto);
            } catch (JsonException) { /* corrupt JSON → null */ }
        }
        IReadOnlyList<string> tags = Array.Empty<string>();
        try {
            tags = JsonSerializer.Deserialize(
                reader.GetString(22), AraJsonSerializerContext.Default.IReadOnlyListString)
                ?? Array.Empty<string>();
        } catch (JsonException) { /* corrupt tags → empty */ }

        return new FrameDto(
            Id: Guid.Parse(reader.GetString(0)),
            SessionId: Guid.Parse(reader.GetString(1)),
            TargetName: reader.GetString(2),
            FrameType: ParseFrameType(reader.GetString(3)),
            FilterName: await reader.IsDBNullAsync(4, ct) ? null : reader.GetString(4),
            ExposureSeconds: reader.GetDouble(5),
            Gain: await reader.IsDBNullAsync(6, ct) ? null : reader.GetInt32(6),
            Offset: await reader.IsDBNullAsync(7, ct) ? null : reader.GetInt32(7),
            TemperatureC: await reader.IsDBNullAsync(8, ct) ? null : reader.GetDouble(8),
            CapturedUtc: DateTimeOffset.Parse(reader.GetString(9)),
            FilePath: reader.GetString(10),
            FileSizeBytes: reader.GetInt64(11),
            Width: reader.GetInt32(12),
            Height: reader.GetInt32(13),
            BitDepth: reader.GetInt32(14),
            Hfr: await reader.IsDBNullAsync(15, ct) ? null : reader.GetDouble(15),
            StarCount: await reader.IsDBNullAsync(16, ct) ? null : reader.GetInt32(16),
            Eccentricity: await reader.IsDBNullAsync(17, ct) ? null : reader.GetDouble(17),
            GuidingRmsArcsec: await reader.IsDBNullAsync(18, ct) ? null : reader.GetDouble(18),
            SnrEstimate: await reader.IsDBNullAsync(19, ct) ? null : reader.GetDouble(19),
            QualityScore: quality,
            Rating: reader.GetInt32(21),
            Tags: tags,
            FocuserPosition: await reader.IsDBNullAsync(23, ct) ? null : reader.GetInt32(23),
            AnalysisVersion: await reader.IsDBNullAsync(24, ct) ? null : reader.GetString(24));
    }

    public async Task<FramePreviewResult?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var previewSource = await GetPreviewSourceAsync(id, ct).ConfigureAwait(false);
        if (previewSource is null) return null;

        var stretchDefaults = _profile.GetStretchDefaults();
        var algorithm = ResolveAlgorithm(request.StretchPalette, previewSource.FrameType, stretchDefaults.LightDefault);
        var stretchParams = BuildParams(request, algorithm, stretchDefaults);
        if (!File.Exists(previewSource.FilePath)) {
            var placeholder = new FramePreviewResult(PlaceholderJpegBytes, "image/jpeg",
                new PreviewCacheMetadata(2, id, new string('0', 64), "missing-source", 1, 1,
                    AlgorithmToWire(algorithm), stretchParams, "none", "luminance",
                    request.Invert, request.Saturation ?? 1, DateTimeOffset.UtcNow,
                    Annotated: request.AnnotateStars,
                    AnnotationColor: request.AnnotateStars
                        ? NormalizeAnnotationColor(request.AnnotationColor).Wire
                        : null,
                    AnnotationLabels: request.AnnotateStars && request.ShowAnnotationLabels), CacheHit: false);
            return placeholder;
        }

        StarAnnotationOptions? annotationOptions = null;
        if (request.AnnotateStars) {
            var annotationColor = NormalizeAnnotationColor(request.AnnotationColor);
            annotationOptions = new StarAnnotationOptions(
                Red: annotationColor.Red,
                Green: annotationColor.Green,
                Blue: annotationColor.Blue,
                StrokeWidth: ToFiniteFloat(request.AnnotationStrokeWidth ?? 2, "AnnotationStrokeWidth"),
                FontSize: ToFiniteFloat(request.AnnotationFontSize ?? 12, "AnnotationFontSize"),
                FontFamily: request.AnnotationFontFamily,
                ShowLabels: request.ShowAnnotationLabels,
                MaxAnnotations: request.MaxAnnotatedStars ?? 250);
        }

        return await _previewImages.RenderAsync(new PreviewRenderRequest(
            FrameId: id,
            SourcePath: previewSource.FilePath,
            SourceChecksumSha256: previewSource.ChecksumSha256,
            Algorithm: algorithm,
            Parameters: stretchParams,
            MaxDimension: request.MaxDimensionPx ?? PreviewMaxDim,
            ApplyDebayer: request.ApplyDebayer,
            ChannelMode: ParseChannelMode(request.ChannelMode),
            Invert: request.Invert,
            Saturation: request.Saturation ?? 1,
            CropX: request.CropX,
            CropY: request.CropY,
            CropWidth: request.CropWidth,
            CropHeight: request.CropHeight,
            AnnotateStars: request.AnnotateStars,
            AnnotationOptions: annotationOptions,
            StarSensitivity: request.StarSensitivity ?? 8.0,
            StarNoiseReduction: request.StarNoiseReduction ?? 0), ct).ConfigureAwait(false);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct) {
        var (filePath, frameType) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return (PlaceholderJpegBytes, "image/jpeg");
        }
        var source = await _sourceImages.LoadAsync(filePath, ct).ConfigureAwait(false);
        var (pixels, width, height, bayerPat) =
            (source.Data.FlatArray, source.Width, source.Height, source.CfaPattern);
        // Thumbnail: §65.4 always uses the default stretch (re-stretch on
        // thumbnails is not supported in v0.0.1). Per-frame-type override
        // still applies — calibration frames get linear.
        var stretchDefaults = _profile.GetStretchDefaults();
        var algorithm = ResolveAlgorithm(null, frameType, stretchDefaults.LightDefault);
        byte[] jpeg;
        if (OpenAstroAra.Stretch.Debayer.TryParse(bayerPat, out var pattern)) {
            var (rgb, ow, oh) = DebayerAndStretch(pixels, width, height, pattern, algorithm, null);
            jpeg = OpenAstroAra.Stretch.JpegEncoder.EncodeColorThumbnail(rgb, ow, oh);
        } else {
            var stretched = OpenAstroAra.Stretch.Stretcher.Apply(algorithm, pixels);
            jpeg = OpenAstroAra.Stretch.JpegEncoder.EncodeThumbnail(stretched, width, height);
        }
        return (jpeg, "image/jpeg");
    }

    public async Task<OpenAstroAra.Image.Interfaces.IImageData?> LoadImageDataAsync(
            Guid id, OpenAstroAra.Profile.Interfaces.IProfileService profileService, CancellationToken ct) {
        // §18.I — use the same bounded, signature-detected FITS/XISF path as preview.
        // The caller's profile service remains authoritative for plate-solver temp-file settings.
        var (filePath, _) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return null;
        }
        var source = await _sourceImages.LoadAsync(filePath, ct).ConfigureAwait(false);
        return _sourceImages.CreateImageData(source, profileService);
    }

    public async Task<(double RaDegrees, double DecDegrees)?> TryReadTargetCoordinatesAsync(Guid id, CancellationToken ct) {
        // §18.I — a header-only read (no pixel decode) of the frame's stored pointing. OBJCTRA/OBJCTDEC are
        // written by the capture path as FITS "H M S" / "D M S" strings from the target's J2000 coordinates
        // (FITSHeader.cs). AstroUtil.HMSToDegrees/DMSToDegrees invert those formats; a frame with no target
        // (e.g. a manually-framed light, or any frame that predates targeted capture) simply lacks the cards.
        var (filePath, _) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return null;
        }
        // The whole read is best-effort: a header hint is optional, so a FITS open/read fault (a corrupt
        // card, or a TOCTOU delete between the File.Exists check and Open) must degrade to a blind solve —
        // never surface as a 500 (unlike LoadImageDataAsync, where a corrupt frame IS the exceptional case).
        try {
            IReadOnlyDictionary<string, string> headers;
            using (var fits = OpenAstroAra.Fits.FitsImage.Open(filePath)) {
                headers = fits.ReadHeaders();
            }
            headers.TryGetValue("OBJCTRA", out var raHms);
            headers.TryGetValue("OBJCTDEC", out var decDms);
            return ParseTargetCoordinates(raHms, decDms);
        } catch (Exception ex) when (ex is OpenAstroAra.Fits.FitsException or IOException) {
            return null;
        }
    }

    /// <summary>
    /// §18.I — parse a frame's OBJCTRA/OBJCTDEC FITS cards ("H M S" / "D M S", J2000) into (RA°, Dec°), or
    /// null when they aren't a usable pointing. The digit guard is load-bearing: <c>AstroUtil.DMSToDegrees</c>
    /// returns 0 rather than throwing when its regex finds no numbers, so a blank/"N/A"/garbage card would
    /// otherwise resolve to a bogus (0h, 0°) hint instead of falling through to a blind solve. A parsed value
    /// outside a sane sky range is likewise rejected.
    /// </summary>
    internal static (double RaDegrees, double DecDegrees)? ParseTargetCoordinates(string? raHms, string? decDms) {
        if (string.IsNullOrEmpty(raHms) || string.IsNullOrEmpty(decDms)
            || !raHms.Any(char.IsDigit) || !decDms.Any(char.IsDigit)) {
            return null;
        }
        try {
            var raDeg = OpenAstroAra.Astrometry.AstroUtil.HMSToDegrees(raHms);
            var decDeg = OpenAstroAra.Astrometry.AstroUtil.DMSToDegrees(decDms);
            if (raDeg is < 0 or >= 360 || decDeg is < -90 or > 90) {
                return null;
            }
            return (raDeg, decDeg);
        } catch (FormatException) {
            return null;
        }
    }

    private async Task<(string? FilePath, FrameType FrameType)> GetPathAndTypeAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, frame_type FROM frames WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, FrameType.Light);
        return (reader.GetString(0), ParseFrameType(reader.GetString(1)));
    }

    private async Task<PreviewSource?> GetPreviewSourceAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, frame_type, sha256 FROM frames WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new PreviewSource(reader.GetString(0), ParseFrameType(reader.GetString(1)),
            await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2));
    }

    // §65 OSC color — the mosaic→RGB preview recipe lives in Debayer.SuperPixelStretched, shared
    // with the §64 Live View render (see the helper's doc for the manual-stretch WB caveat).
    private static (byte[] Rgb, int Width, int Height) DebayerAndStretch(
        ushort[] mosaic, int width, int height, OpenAstroAra.Stretch.BayerPattern pattern,
        OpenAstroAra.Stretch.StretchAlgorithm algorithm, OpenAstroAra.Stretch.StretchParams? stretchParams) =>
        OpenAstroAra.Stretch.Debayer.SuperPixelStretched(mosaic, width, height, pattern, algorithm, stretchParams);

    /// <summary>
    /// §65.2 defaults policy: frame-type auto-override beats request
    /// palette beats profile default. Calibration frames (Dark/Bias/Flat)
    /// always render `linear`. Light frames use the request palette if
    /// provided, otherwise the profile's `light_default`.
    /// </summary>
    private static OpenAstroAra.Stretch.StretchAlgorithm ResolveAlgorithm(string? requested, FrameType frameType, string profileLightDefault) {
        if (frameType is FrameType.Dark or FrameType.Bias or FrameType.Flat or FrameType.DarkFlat) {
            return OpenAstroAra.Stretch.StretchAlgorithm.Linear;
        }
        if (!string.IsNullOrWhiteSpace(requested)) {
            return ParseAlgorithm(requested) ?? throw new ArgumentException(
                $"Unknown stretch palette '{requested}'. Supported palettes: auto_stf, linear, log, asinh, sqrt, equalized, manual.",
                nameof(requested));
        }
        return ParseAlgorithm(profileLightDefault) ?? OpenAstroAra.Stretch.StretchAlgorithm.AutoStf;
    }

    private static OpenAstroAra.Stretch.StretchAlgorithm? ParseAlgorithm(string? value) =>
        value?.ToLowerInvariant() switch {
            "auto_stf" or "stf" or "auto" => OpenAstroAra.Stretch.StretchAlgorithm.AutoStf,
            "linear" => OpenAstroAra.Stretch.StretchAlgorithm.Linear,
            "log" => OpenAstroAra.Stretch.StretchAlgorithm.Log,
            "asinh" => OpenAstroAra.Stretch.StretchAlgorithm.Asinh,
            "sqrt" => OpenAstroAra.Stretch.StretchAlgorithm.Sqrt,
            "equalized" or "histogram" => OpenAstroAra.Stretch.StretchAlgorithm.Equalized,
            "manual" => OpenAstroAra.Stretch.StretchAlgorithm.Manual,
            _ => null,
        };

    private static OpenAstroAra.Stretch.StretchParams BuildParams(
            FramePreviewRequestDto request,
            OpenAstroAra.Stretch.StretchAlgorithm algorithm,
            StretchDefaultsDto profileDefaults) {
        // Manual + asinh + linear thread per-profile defaults through when
        // the request doesn't override; auto_stf + log + sqrt + equalized
        // don't consume these parameters.
        var manualSeeds = profileDefaults.ManualDefaultParams;
        return new OpenAstroAra.Stretch.StretchParams(
            Blackpoint: request.BlackPoint ?? manualSeeds.Blackpoint,
            Midpoint: request.MidtonePoint ?? manualSeeds.Midpoint,
            Whitepoint: request.WhitePoint ?? manualSeeds.Whitepoint,
            Beta: request.AsinhBeta ?? profileDefaults.AsinhDefaultBeta,
            LinearClipLow: request.LinearClipLow ?? profileDefaults.LinearClipPercentilesLow,
            LinearClipHigh: request.LinearClipHigh ?? profileDefaults.LinearClipPercentilesHigh);
    }

    private static PreviewChannelMode ParseChannelMode(string? value) => value?.Trim().ToLowerInvariant() switch {
        null or "" or "rgb" or "color" => PreviewChannelMode.Rgb,
        "luminance" or "gray" or "mono" => PreviewChannelMode.Luminance,
        "red" => PreviewChannelMode.Red,
        "green" => PreviewChannelMode.Green,
        "blue" => PreviewChannelMode.Blue,
        _ => throw new ArgumentException(
            $"Unknown preview channel mode '{value}'. Supported modes: rgb, luminance, red, green, blue.",
            nameof(value)),
    };

    private static (byte Red, byte Green, byte Blue, string Wire) NormalizeAnnotationColor(string? value) {
        var normalized = value?.Trim().ToLowerInvariant();
        normalized = normalized switch {
            null or "" or "green" => "#00ff00",
            "red" => "#ff0000",
            "yellow" => "#ffff00",
            "cyan" => "#00ffff",
            "white" => "#ffffff",
            _ => normalized,
        };
        if (normalized.Length != 7 || normalized[0] != '#'
            || !byte.TryParse(normalized.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(normalized.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(normalized.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var blue)) {
            throw new ArgumentException(
                $"Unknown annotation color '{value}'. Use #RRGGBB, green, red, yellow, cyan, or white.",
                nameof(value));
        }
        return (red, green, blue, normalized);
    }

    private static float ToFiniteFloat(double value, string field) {
        if (!double.IsFinite(value) || value is > float.MaxValue or < -float.MaxValue) {
            throw new ArgumentOutOfRangeException(field, value, $"{field} must be finite.");
        }
        return (float)value;
    }

    private static string AlgorithmToWire(OpenAstroAra.Stretch.StretchAlgorithm algorithm) => algorithm switch {
        OpenAstroAra.Stretch.StretchAlgorithm.AutoStf => "auto_stf",
        OpenAstroAra.Stretch.StretchAlgorithm.Linear => "linear",
        OpenAstroAra.Stretch.StretchAlgorithm.Log => "log",
        OpenAstroAra.Stretch.StretchAlgorithm.Asinh => "asinh",
        OpenAstroAra.Stretch.StretchAlgorithm.Sqrt => "sqrt",
        OpenAstroAra.Stretch.StretchAlgorithm.Equalized => "equalized",
        OpenAstroAra.Stretch.StretchAlgorithm.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
    };

    public async Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct) {
        // §72: serve the captured FITS bytes from the path stored in the
        // catalog. Two failure modes both map to 404 at the endpoint:
        //   - Frame id not in the catalog
        //   - File missing on disk (deleted out-of-band, drive not mounted,
        //     or just never written yet for the seeded sample frames)
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM frames WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var filePath = (string?)(await cmd.ExecuteScalarAsync(ct));
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return null;
        }
        // FileStream owned by the response pipeline — ASP.NET Core
        // disposes it when the response finishes sending.
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return (stream, Path.GetFileName(filePath));
    }

    // Bulk ops now actually mutate the catalog. Execution is synchronous
    // (sub-ms for typical batches up to a few hundred frames); the 202
    // OperationAccepted shape is preserved so future async-job-queue
    // refactors (real workers with WS event emission) stay wire-compat.
    // Idempotency-Key dedup at the persistence layer is a separate concern
    // (lands when the §60.5 in-memory dedup cache PR lands).

    public async Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct) {
        if (request.FrameIds.Count > 0) {
            await using var conn = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE frames SET rating = $rating WHERE id = $id;";
            var ratingParam = cmd.Parameters.Add("$rating", Microsoft.Data.Sqlite.SqliteType.Integer);
            var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            ratingParam.Value = request.Rating;
            foreach (var frameId in request.FrameIds) {
                idParam.Value = frameId.ToString();
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-rate", idempotencyKey);
    }

    public async Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct) {
        if (request.FrameIds.Count > 0 && (request.AddTags.Count > 0 || request.RemoveTags.Count > 0)) {
            await using var conn = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            // Tags is a JSON-blob column; SQLite has no set ops on JSON
            // arrays in v3.x portably, so read-merge-write per row. Set
            // ordering preserved as insertion order via LinkedHashSet via
            // List + Contains check.
            foreach (var frameId in request.FrameIds) {
                IReadOnlyList<string> current = await ReadTagsAsync(conn, tx, frameId, ct);
                var merged = new List<string>(current);
                foreach (var rem in request.RemoveTags) {
                    merged.RemoveAll(t => string.Equals(t, rem, StringComparison.OrdinalIgnoreCase));
                }
                foreach (var add in request.AddTags) {
                    if (!merged.Any(t => string.Equals(t, add, StringComparison.OrdinalIgnoreCase))) {
                        merged.Add(add);
                    }
                }
                await WriteTagsAsync(conn, tx, frameId, merged, ct);
            }
            await tx.CommitAsync(ct);
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-tag", idempotencyKey);
    }

    public async Task<OperationAcceptedDto> BulkMoveAsync(BulkMoveRequestDto request, string? idempotencyKey, CancellationToken ct) {
        if (request.FrameIds.Count > 0) {
            await using var conn = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            // The target session must exist — silently filing frames under a
            // nonexistent id would orphan them from every session view.
            await using (var probe = conn.CreateCommand()) {
                probe.Transaction = tx;
                probe.CommandText = "SELECT 1 FROM sessions WHERE id = $sid LIMIT 1;";
                probe.Parameters.AddWithValue("$sid", request.TargetSessionId.ToString());
                if (await probe.ExecuteScalarAsync(ct) is null) {
                    throw new ArgumentException($"Target session {request.TargetSessionId:D} does not exist.", nameof(request));
                }
            }
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE frames SET session_id = $sid WHERE id = $id;";
            cmd.Parameters.AddWithValue("$sid", request.TargetSessionId.ToString());
            var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var frameId in request.FrameIds) {
                idParam.Value = frameId.ToString();
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-move", idempotencyKey);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The composed fragment is only '$id0,$id1,...' parameter placeholders generated from the chunk COUNT; every value is bound. No user input reaches the command text.")]
    public async Task<FrameExportPrep?> PrepareExportAsync(BulkExportRequestDto request, CancellationToken ct) {
        if (request.FrameIds.Count == 0) return null;

        // Path resolution in bound-parameter-safe chunks (SQLite's parameter
        // limit would throw on very large selections otherwise), mapped back
        // to request order so entry naming stays deterministic.
        const int ChunkSize = 500;
        var byId = new Dictionary<Guid, string>();
        await using (var conn = _db.OpenConnection()) {
            for (var offset = 0; offset < request.FrameIds.Count; offset += ChunkSize) {
                var chunk = request.FrameIds.Skip(offset).Take(ChunkSize).ToList();
                await using var cmd = conn.CreateCommand();
                var parts = new List<string>(chunk.Count);
                for (var i = 0; i < chunk.Count; i++) {
                    parts.Add($"$id{i}");
                    cmd.Parameters.AddWithValue($"$id{i}", chunk[i].ToString());
                }
                cmd.CommandText = $"SELECT id, file_path FROM frames WHERE id IN ({string.Join(',', parts)});";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) {
                    if (Guid.TryParse(reader.GetString(0), out var id) && !await reader.IsDBNullAsync(1, ct)) {
                        byId[id] = reader.GetString(1);
                    }
                }
            }
        }

        // Plan entries for files present NOW (no handles opened here — see
        // FrameExportPrep: the endpoint opens one at a time while streaming).
        var entries = new List<(string Path, string EntryName)>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frameId in request.FrameIds) {
            if (!byId.TryGetValue(frameId, out var path) || path.Length == 0 || !File.Exists(path)) continue;
            var name = Path.GetFileName(path);
            if (!seenNames.Add(name)) {
                // Suffix until genuinely unique — a single rename can itself
                // collide (frame_1/frame/frame), silently clobbering on extract.
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var suffix = 1;
                string candidate;
                do {
                    candidate = $"{stem}_{suffix++}{ext}";
                } while (!seenNames.Add(candidate));
                name = candidate;
            }
            entries.Add((path, name));
        }
        if (entries.Count == 0) return null;
        return new FrameExportPrep(entries, $"openastroara-frames-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar");
    }

    public async Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct) {
        if (request.FrameIds.Count > 0) {
            // Collect file paths BEFORE the rows go — after the delete there's
            // nothing left to resolve them from. Only needed for disk deletion.
            var paths = new List<string>();
            await using var conn = _db.OpenConnection();
            await using (var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)) {
                if (request.DeleteFromDisk) {
                    await using var pathCmd = conn.CreateCommand();
                    pathCmd.Transaction = tx;
                    pathCmd.CommandText = "SELECT file_path FROM frames WHERE id = $id LIMIT 1;";
                    var pathParam = pathCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                    foreach (var frameId in request.FrameIds) {
                        pathParam.Value = frameId.ToString();
                        if (await pathCmd.ExecuteScalarAsync(ct) is string path && path.Length > 0) {
                            paths.Add(path);
                        }
                    }
                }
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM frames WHERE id = $id;";
                var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var frameId in request.FrameIds) {
                    idParam.Value = frameId.ToString();
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            // Disk deletion is best-effort AFTER the catalog commit: a frame the
            // user asked to remove must leave the catalog even if its file is on
            // a detached volume. FITS + the §65.4 sidecars (default/variant
            // previews, thumbnail) all go; locked/missing files are skipped.
            foreach (var fitsPath in paths) {
                DeleteFrameFilesBestEffort(fitsPath);
            }
            foreach (var frameId in request.FrameIds) {
                await _previewImages.DeleteFrameEntriesAsync(frameId, ct).ConfigureAwait(false);
            }
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-delete", idempotencyKey);
    }

    private static void DeleteFrameFilesBestEffort(string fitsPath) {
        try { File.Delete(fitsPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { /* skip */ }
        var dir = Path.GetDirectoryName(fitsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var stem = Path.GetFileNameWithoutExtension(fitsPath);
        try {
            // <stem>.preview.jpg (default), <stem>.preview.<stretch>[.<hash>].jpg
            // (variants) and <stem>.thumb.jpg (thumbnail) — same naming the §65.4
            // cache helpers use above.
            foreach (var sidecar in Directory.EnumerateFiles(dir, $"{stem}.preview*.jpg")
                         .Concat(Directory.EnumerateFiles(dir, $"{stem}.thumb.jpg"))) {
                try { File.Delete(sidecar); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip locked */ }
            }
        } catch (DirectoryNotFoundException) { /* volume detached — nothing to delete */
        } catch (UnauthorizedAccessException) { /* read-only mount */ }
    }

    private static async Task<IReadOnlyList<string>> ReadTagsAsync(
            SqliteConnection conn, SqliteTransaction tx, Guid frameId, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT tags_json FROM frames WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", frameId.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull) return Array.Empty<string>();
        try {
            return JsonSerializer.Deserialize(
                (string)result, AraJsonSerializerContext.Default.IReadOnlyListString)
                ?? Array.Empty<string>();
        } catch (JsonException) {
            return Array.Empty<string>();
        }
    }

    private static async Task WriteTagsAsync(
            SqliteConnection conn, SqliteTransaction tx, Guid frameId,
            IReadOnlyList<string> tags, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE frames SET tags_json = $tags WHERE id = $id;";
        cmd.Parameters.AddWithValue("$tags",
            JsonSerializer.Serialize(tags, AraJsonSerializerContext.Default.IReadOnlyListString));
        cmd.Parameters.AddWithValue("$id", frameId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeletePreviewVariantsAsync(Guid id, CancellationToken ct) {
        var (filePath, _) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath)) return false;
        await _previewImages.DeleteFrameEntriesAsync(id, ct).ConfigureAwait(false);
        // Remove legacy sidecars created before the external bounded cache landed.
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return true;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var pattern = $"{stem}.preview.*.jpg";
        try {
            try { File.Delete(Path.Combine(dir, $"{stem}.preview.jpg")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var variant in Directory.EnumerateFiles(dir, pattern)) {
                try { File.Delete(variant); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip locked */ }
            }
        } catch (DirectoryNotFoundException) { /* nothing to delete */ } catch (UnauthorizedAccessException) { /* read-only mount */ }
        return true;
    }

    // §65 preview payload cap. The full preview is a viewer aid, not the full-res download, so
    // cap its longest edge — a 60 MP OSC frame would otherwise debayer to a ~15 MP JPEG. 2048 px
    // keeps it crisp on any display while bounding payload/encode time; the FITS + the eventual
    // full-res export are unaffected. (Settings → Image Processing could expose this later.)
    private const int PreviewMaxDim = 2048;


    // Frame type ↔ string. The enum is JSON-serialized as lowercase via
    // the global JsonStringEnumConverter, but we don't have a JSON
    // round-trip available cheaply inside ADO.NET reader code — duplicate
    // the mapping here so the DB column stays human-readable.
    private static string FrameTypeToString(FrameType t) => t switch {
        FrameType.Light => "light",
        FrameType.Dark => "dark",
        FrameType.Flat => "flat",
        FrameType.Bias => "bias",
        FrameType.DarkFlat => "darkflat",
        _ => t.ToString().ToLowerInvariant(),
    };

    private static FrameType ParseFrameType(string s) => s.ToLowerInvariant() switch {
        "light" => FrameType.Light,
        "dark" => FrameType.Dark,
        "flat" => FrameType.Flat,
        "bias" => FrameType.Bias,
        "darkflat" => FrameType.DarkFlat,
        _ => Enum.TryParse<FrameType>(s, ignoreCase: true, out var ft) ? ft : FrameType.Light,
    };

    private sealed record PreviewSource(string FilePath, FrameType FrameType,
        string? ChecksumSha256);

    private static readonly string[] SampleTags = { "good-seeing" };

    // Boxes a nullable value type for an ADO.NET parameter, mapping null to DBNull.
    // (A direct '(object?)value ?? DBNull.Value' trips CA1508, which does not model
    // Nullable<T> boxing returning null.)
    private static object DbValue<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded sample session + 3 sample frames into catalog")]
    private partial void LogSeededFrames();
}