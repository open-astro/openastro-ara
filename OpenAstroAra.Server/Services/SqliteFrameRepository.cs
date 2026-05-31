#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28-backed <see cref="IFrameRepository"/>. Read path queries SQLite;
/// the mutating Bulk* operations still return placeholder
/// <c>Accepted</c> responses — a follow-up sub-PR makes them actually
/// mutate rows. Preview + thumbnail still return the 1×1 PNG placeholder
/// until §65 stretch pipeline lands. <c>OpenDownloadAsync</c> returns
/// null until §72 FITS storage lands.
///
/// Seeding: on first init, if the <c>frames</c> table is empty, three
/// fixture rows are inserted with the same Guids the placeholder repo
/// used so existing CI smoke gate probes + UI manual testing continue
/// to find the sample session + sample frames.
/// </summary>
public sealed class SqliteFrameRepository : IFrameRepository {
    private static readonly Guid SampleSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid[] SampleFrameIds = new[] {
        Guid.Parse("22222222-2222-2222-2222-222222222221"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("22222222-2222-2222-2222-222222222223"),
    };

    // 1×1 JPEG, used until §65 stretch pipeline produces real previews.
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
    private readonly ILogger<SqliteFrameRepository>? _logger;

    public SqliteFrameRepository(IAraDatabase db, ILogger<SqliteFrameRepository>? logger) {
        _db = db;
        _logger = logger;
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

        // Three fixture frames mirroring PlaceholderFrameRepository.SampleFrames.
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
            Tags: new[] { "good-seeing" }),
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

        _logger?.LogInformation("Seeded sample session + 3 sample frames into catalog");
    }

    private static async Task InsertFrameAsync(SqliteConnection conn, FrameDto f, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO frames
                (id, session_id, target_name, frame_type, filter_name,
                 exposure_seconds, gain, "offset", temperature_c, captured_utc,
                 file_path, file_size_bytes, width, height, bit_depth,
                 hfr, star_count, eccentricity, guiding_rms_arcsec, snr_estimate,
                 quality_score_json, rating, tags_json)
            VALUES
                ($id, $session_id, $target_name, $frame_type, $filter_name,
                 $exposure_seconds, $gain, $offset, $temperature_c, $captured_utc,
                 $file_path, $file_size_bytes, $width, $height, $bit_depth,
                 $hfr, $star_count, $eccentricity, $guiding_rms_arcsec, $snr_estimate,
                 $quality_score_json, $rating, $tags_json);
            """;
        cmd.Parameters.AddWithValue("$id", f.Id.ToString());
        cmd.Parameters.AddWithValue("$session_id", f.SessionId.ToString());
        cmd.Parameters.AddWithValue("$target_name", f.TargetName);
        cmd.Parameters.AddWithValue("$frame_type", FrameTypeToString(f.FrameType));
        cmd.Parameters.AddWithValue("$filter_name", (object?)f.FilterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exposure_seconds", f.ExposureSeconds);
        cmd.Parameters.AddWithValue("$gain", f.Gain);
        cmd.Parameters.AddWithValue("$offset", (object?)f.Offset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$temperature_c", f.TemperatureC);
        cmd.Parameters.AddWithValue("$captured_utc", f.CapturedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$file_path", f.FilePath);
        cmd.Parameters.AddWithValue("$file_size_bytes", f.FileSizeBytes);
        cmd.Parameters.AddWithValue("$width", f.Width);
        cmd.Parameters.AddWithValue("$height", f.Height);
        cmd.Parameters.AddWithValue("$bit_depth", f.BitDepth);
        cmd.Parameters.AddWithValue("$hfr", (object?)f.Hfr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$star_count", (object?)f.StarCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eccentricity", (object?)f.Eccentricity ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$guiding_rms_arcsec", (object?)f.GuidingRmsArcsec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$snr_estimate", (object?)f.SnrEstimate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quality_score_json", f.QualityScore is null
            ? DBNull.Value
            : JsonSerializer.Serialize(f.QualityScore, AraJsonSerializerContext.Default.QualityScoreBreakdownDto));
        cmd.Parameters.AddWithValue("$rating", f.Rating);
        cmd.Parameters.AddWithValue("$tags_json",
            JsonSerializer.Serialize(f.Tags, AraJsonSerializerContext.Default.IReadOnlyListString));
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
                   quality_score_json, rating
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
        while (await reader.ReadAsync(ct) && items.Count < pageSize) {
            double? composite = null;
            if (!reader.IsDBNull(9)) {
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
                FilterName: reader.IsDBNull(4) ? null : reader.GetString(4),
                ExposureSeconds: reader.GetInt32(5),
                CapturedUtc: DateTimeOffset.Parse(reader.GetString(6)),
                Hfr: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                StarCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                CompositeQualityScore: composite,
                Rating: reader.GetInt32(10)));
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
                   quality_score_json, rating, tags_json
            FROM frames WHERE id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        QualityScoreBreakdownDto? quality = null;
        if (!reader.IsDBNull(20)) {
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
            FilterName: reader.IsDBNull(4) ? null : reader.GetString(4),
            ExposureSeconds: reader.GetInt32(5),
            Gain: reader.GetInt32(6),
            Offset: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            TemperatureC: reader.GetDouble(8),
            CapturedUtc: DateTimeOffset.Parse(reader.GetString(9)),
            FilePath: reader.GetString(10),
            FileSizeBytes: reader.GetInt64(11),
            Width: reader.GetInt32(12),
            Height: reader.GetInt32(13),
            BitDepth: reader.GetInt32(14),
            Hfr: reader.IsDBNull(15) ? null : reader.GetDouble(15),
            StarCount: reader.IsDBNull(16) ? null : reader.GetInt32(16),
            Eccentricity: reader.IsDBNull(17) ? null : reader.GetDouble(17),
            GuidingRmsArcsec: reader.IsDBNull(18) ? null : reader.GetDouble(18),
            SnrEstimate: reader.IsDBNull(19) ? null : reader.GetDouble(19),
            QualityScore: quality,
            Rating: reader.GetInt32(21),
            Tags: tags);
    }

    public Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct) =>
        // §65 stretch pipeline replaces this with real previews from the
        // captured FITS file.
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<(byte[] Bytes, string ContentType)?>((PlaceholderJpegBytes, "image/jpeg"));

    public Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct) =>
        // §72 FITS storage replaces this with the real file stream.
        Task.FromResult<(Stream FitsStream, string FileName)?>(null);

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

    public async Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct) {
        if (request.FrameIds.Count > 0) {
            await using var conn = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM frames WHERE id = $id;";
            var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var frameId in request.FrameIds) {
                idParam.Value = frameId.ToString();
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            // request.DeleteFromDisk: §72 FITS storage is not yet wired,
            // so there's no file to delete in v0.0.1. The flag is
            // preserved on the wire so clients can opt in once storage
            // lands without a contract change.
        }
        return PlaceholderEquipmentHelpers.Accepted("frames.bulk-delete", idempotencyKey);
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
}
