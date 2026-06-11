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
using OpenAstroAra.Server.Contracts;
using System.Text.Json;

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
public sealed partial class SqliteFrameRepository : IFrameRepository {
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
    private readonly IProfileStore _profile;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<SqliteFrameRepository> _logger;

    public SqliteFrameRepository(IAraDatabase db, IProfileStore profile, IWsBroadcaster? ws = null, ILogger<SqliteFrameRepository>? logger = null) {
        _db = db;
        _profile = profile;
        _ws = ws;
        _logger = logger ?? NullLogger<SqliteFrameRepository>.Instance;
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

        LogSeededFrames();
    }

    /// <inheritdoc />
    public async Task InsertAsync(FrameDto frame, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(frame);
        await using var conn = _db.OpenConnection();
        await InsertFrameAsync(conn, frame, ct).ConfigureAwait(false);
    }

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
        cmd.Parameters.AddWithValue("$offset", DbValue(f.Offset));
        cmd.Parameters.AddWithValue("$temperature_c", f.TemperatureC);
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
                ExposureSeconds: reader.GetInt32(5),
                CapturedUtc: DateTimeOffset.Parse(reader.GetString(6)),
                Hfr: await reader.IsDBNullAsync(7, ct) ? null : reader.GetDouble(7),
                StarCount: await reader.IsDBNullAsync(8, ct) ? null : reader.GetInt32(8),
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
            ExposureSeconds: reader.GetInt32(5),
            Gain: reader.GetInt32(6),
            Offset: await reader.IsDBNullAsync(7, ct) ? null : reader.GetInt32(7),
            TemperatureC: reader.GetDouble(8),
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
            Tags: tags);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct) {
        var (filePath, frameType) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return (PlaceholderJpegBytes, "image/jpeg");
        }

        var stretchDefaults = _profile.GetStretchDefaults();
        var algorithm = ResolveAlgorithm(request.StretchPalette, frameType, stretchDefaults.LightDefault);
        var stretchParams = BuildParams(request, algorithm, stretchDefaults);

        // §65.4 variant cache: look for an existing JPEG on disk before
        // re-running the stretch + encode. Cache key includes the algorithm
        // ID + a hash of the manual stretch params (rounded to 3 decimal
        // places so rapid slider drags don't blow the cache).
        var cachePath = ComputeCacheKey(filePath, algorithm, stretchParams);
        if (TryServeFromCache(cachePath) is byte[] cached) {
            // Touch atime so the LRU sweep keeps the most-recently-served
            // variants warm.
            try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
            return (cached, "image/jpeg");
        }

        var (pixels, width, height, bayerPat) = LoadFitsPixels(filePath);
        byte[] jpeg;
        if (OpenAstroAra.Stretch.Debayer.TryParse(bayerPat, out var pattern)) {
            var (rgb, ow, oh) = DebayerAndStretch(pixels, width, height, pattern, algorithm, stretchParams);
            jpeg = OpenAstroAra.Stretch.JpegEncoder.EncodeColor(rgb, ow, oh);
        } else {
            var stretched = OpenAstroAra.Stretch.Stretcher.Apply(algorithm, pixels, stretchParams);
            jpeg = OpenAstroAra.Stretch.JpegEncoder.EncodeGray(stretched, width, height);
        }
        TryWriteCache(cachePath, jpeg);
        EvictVariantsIfNeeded(filePath);
        return (jpeg, "image/jpeg");
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct) {
        var (filePath, frameType) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return (PlaceholderJpegBytes, "image/jpeg");
        }
        var (pixels, width, height, bayerPat) = LoadFitsPixels(filePath);
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
        // §18.I — reuse the proven preview FITS read path, then wrap the raw 16-bit pixels as IImageData for
        // the plate-solver. The real profileService is passed in (the CLI solver writes a temp FITS via the
        // image's SaveToDisk, which reads the profile for the file pattern). Star detection/annotator are
        // genuinely untouched by solving → null. FITS frames are 16-bit; isBayered preserves the CFA flag.
        var (filePath, _) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return null;
        }
        // A corrupt/truncated FITS lets LoadFitsPixels throw → 500, consistent with GetPreviewAsync/
        // GetThumbnailAsync on the same file (a malformed catalogued frame is an exceptional, not user-input, case).
        var (pixels, width, height, bayerPat) = LoadFitsPixels(filePath);
        return new OpenAstroAra.Image.ImageData.BaseImageData(
            pixels, width, height, bitDepth: 16, isBayered: !string.IsNullOrEmpty(bayerPat),
            new OpenAstroAra.Image.ImageData.ImageMetaData(), profileService, null!, null!);
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

    private static (ushort[] Pixels, int Width, int Height, string? BayerPattern) LoadFitsPixels(string filePath) {
        using var fits = OpenAstroAra.Fits.FitsImage.Open(filePath);
        var (w, h) = fits.GetDimensions();
        var pixels = fits.ReadImageData16();
        var bayer = fits.ReadHeaders().TryGetValue("BAYERPAT", out var bp) ? bp : null;
        return (pixels, w, h, bayer);
    }

    // §65 OSC color: super-pixel debayer the raw mosaic → 3 half-res channels, stretch each, and
    // interleave to RGB. Per-channel auto-stretch incidentally auto-white-balances the preview; the
    // stored FITS stays the raw, undebayered mosaic.
    //
    // WB caveat: with a user-supplied stretchParams (manual black/white points), applying the same
    // params per channel can shift white balance — those points were chosen against the mosaic's
    // combined luminance, not per-channel. Acceptable for a preview; revisit if manual OSC stretch
    // looks off.
    private static (byte[] Rgb, int Width, int Height) DebayerAndStretch(
        ushort[] mosaic, int width, int height, OpenAstroAra.Stretch.BayerPattern pattern,
        OpenAstroAra.Stretch.StretchAlgorithm algorithm, OpenAstroAra.Stretch.StretchParams? stretchParams) {
        var (r, g, b, ow, oh) = OpenAstroAra.Stretch.Debayer.SuperPixel(mosaic, width, height, pattern);
        var rs = OpenAstroAra.Stretch.Stretcher.Apply(algorithm, r, stretchParams);
        var gs = OpenAstroAra.Stretch.Stretcher.Apply(algorithm, g, stretchParams);
        var bs = OpenAstroAra.Stretch.Stretcher.Apply(algorithm, b, stretchParams);
        var rgb = new byte[rs.Length * 3];
        for (int i = 0, d = 0; i < rs.Length; i++, d += 3) {
            rgb[d] = rs[i];
            rgb[d + 1] = gs[i];
            rgb[d + 2] = bs[i];
        }
        return (rgb, ow, oh);
    }

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
        return ParseAlgorithm(requested) ?? ParseAlgorithm(profileLightDefault) ?? OpenAstroAra.Stretch.StretchAlgorithm.AutoStf;
    }

    private static OpenAstroAra.Stretch.StretchAlgorithm? ParseAlgorithm(string? value) =>
        value?.ToLowerInvariant() switch {
            "auto_stf" => OpenAstroAra.Stretch.StretchAlgorithm.AutoStf,
            "linear" => OpenAstroAra.Stretch.StretchAlgorithm.Linear,
            "log" => OpenAstroAra.Stretch.StretchAlgorithm.Log,
            "asinh" => OpenAstroAra.Stretch.StretchAlgorithm.Asinh,
            "sqrt" => OpenAstroAra.Stretch.StretchAlgorithm.Sqrt,
            "equalized" => OpenAstroAra.Stretch.StretchAlgorithm.Equalized,
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
            Beta: profileDefaults.AsinhDefaultBeta,
            LinearClipLow: profileDefaults.LinearClipPercentilesLow,
            LinearClipHigh: profileDefaults.LinearClipPercentilesHigh);
    }

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

    public async Task<bool> DeletePreviewVariantsAsync(Guid id, CancellationToken ct) {
        var (filePath, _) = await GetPathAndTypeAsync(id, ct);
        if (string.IsNullOrEmpty(filePath)) return false;
        // Frame exists in the catalog; missing FITS on disk doesn't make
        // this 404 (user may have rotated storage). Variants live next to
        // the FITS via the §65.4 cache-key pattern.
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return true;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var pattern = $"{stem}.preview.*.jpg";
        try {
            foreach (var variant in Directory.EnumerateFiles(dir, pattern)) {
                try { File.Delete(variant); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip locked */ }
            }
        } catch (DirectoryNotFoundException) { /* nothing to delete */ } catch (UnauthorizedAccessException) { /* read-only mount */ }
        return true;
    }

    // §65.4 cache cap. Six alt-stretch variants per frame plus the default
    // = seven total. Settings → Image Processing → Preview Cache will expose
    // this knob (range 1-20) in a future sub-PR; for now it's a const.
    private const int MaxVariantsPerFrame = 6;

    // §29 storage-pressure threshold. When the captures volume free space
    // drops below this, the §65.4 variant-eviction path nukes all alt-
    // stretch variants for the current frame (NOT defaults, NOT FITS, NOT
    // thumbnails). 1 GB is the v0.0.1 fixed value; §29.4 settings panel
    // exposes it as a tunable in a future sub-PR.
    private const long StoragePressureBytes = 1L * 1024L * 1024L * 1024L;

    private static string ComputeCacheKey(string fitsPath, OpenAstroAra.Stretch.StretchAlgorithm algorithm,
            OpenAstroAra.Stretch.StretchParams parameters) {
        var stretchId = algorithm switch {
            OpenAstroAra.Stretch.StretchAlgorithm.AutoStf => "auto_stf",
            OpenAstroAra.Stretch.StretchAlgorithm.Linear => "linear",
            OpenAstroAra.Stretch.StretchAlgorithm.Log => "log",
            OpenAstroAra.Stretch.StretchAlgorithm.Asinh => "asinh",
            OpenAstroAra.Stretch.StretchAlgorithm.Sqrt => "sqrt",
            OpenAstroAra.Stretch.StretchAlgorithm.Equalized => "equalized",
            OpenAstroAra.Stretch.StretchAlgorithm.Manual => "manual",
            _ => "default",
        };
        var dir = Path.GetDirectoryName(fitsPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(fitsPath);
        // Manual stretch params hash so slider drags coalesce in the cache
        // (§65.4 rounds to 3 decimal places to bound entry count).
        if (algorithm == OpenAstroAra.Stretch.StretchAlgorithm.Manual) {
            var bp = Math.Round(parameters.Blackpoint, 3);
            var mp = Math.Round(parameters.Midpoint, 3);
            var wp = Math.Round(parameters.Whitepoint, 3);
            var hash = $"{bp:F3}_{mp:F3}_{wp:F3}".Replace('.', 'p');
            return Path.Combine(dir, $"{stem}.preview.{stretchId}.{hash}.jpg");
        }
        return Path.Combine(dir, $"{stem}.preview.{stretchId}.jpg");
    }

    private static byte[]? TryServeFromCache(string cachePath) {
        if (!File.Exists(cachePath)) return null;
        try {
            return File.ReadAllBytes(cachePath);
        } catch (IOException) {
            return null;
        }
    }

    private static void TryWriteCache(string cachePath, byte[] bytes) {
        try {
            // Atomic write per §28.7 — temp + rename so a concurrent reader
            // never sees a partial JPEG.
            var tmp = cachePath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, cachePath, overwrite: true);
        } catch (IOException) {
            // Cache write best-effort; serving the bytes inline is what
            // matters. Storage pressure / read-only mounts shouldn't
            // 500 the request.
        }
    }

    private void EvictVariantsIfNeeded(string fitsPath) {
        var dir = Path.GetDirectoryName(fitsPath);
        if (string.IsNullOrEmpty(dir)) return;

        // §65.4 storage-pressure check. If the captures volume is below the
        // §29 critical threshold, evict ALL alt-stretch variants for this
        // frame — not just past the per-frame cap. They're recoverable
        // cache; FITS + defaults + thumbnails are not.
        var aggressiveEviction = IsUnderStoragePressure(dir);

        var stem = Path.GetFileNameWithoutExtension(fitsPath);
        var pattern = $"{stem}.preview.*.jpg";
        try {
            var variants = Directory.EnumerateFiles(dir, pattern)
                // Default stretch is the un-suffixed "preview.jpg" (legacy
                // path); explicit stretch variants always have a .preview.<id>.
                // Both compete for the same per-frame budget — but per §65.4
                // thumbnails are excluded (named .thumb.jpg). Our naming
                // above always includes .preview. so all matches are
                // variant entries.
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastAccessTimeUtc)
                .ToList();

            // Storage pressure: keep nothing. LRU cap: keep top N.
            var keep = aggressiveEviction ? 0 : MaxVariantsPerFrame;
            if (variants.Count <= keep) return;
            foreach (var stale in variants.Skip(keep)) {
                try {
                    stale.Delete();
                    EmitVariantEvictedAsync(fitsPath, stale.Name,
                        reason: aggressiveEviction ? "storage_pressure" : "lru_eviction");
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
            }
        } catch (DirectoryNotFoundException) { /* nothing to evict */ } catch (UnauthorizedAccessException) { /* read-only mount; skip */ }
    }

    private static bool IsUnderStoragePressure(string dir) {
        try {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return false;
            var info = new DriveInfo(root);
            return info.IsReady && info.AvailableFreeSpace < StoragePressureBytes;
        } catch (ArgumentException) {
            return false;
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    private void EmitVariantEvictedAsync(string fitsPath, string variantFileName, string reason) {
        if (_ws is null) return;
        var stretchId = ExtractStretchIdFromVariantName(variantFileName);
        // Fire-and-forget; WS publish failure mustn't abort cache management.
        _ = Task.Run(async () => {
            try {
                var json = $$"""
                    {"frame_path":"{{fitsPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}}","stretch_id":"{{stretchId}}","reason":"{{reason}}"}
                    """;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                await _ws.PublishAsync("frame.preview.variant.evicted", doc.RootElement.Clone(), CancellationToken.None);
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException or ObjectDisposedException) { /* swallow */ }
        });
    }

    private static string ExtractStretchIdFromVariantName(string fileName) {
        // <stem>.preview.<stretch-id>.jpg → return <stretch-id>
        // <stem>.preview.manual.<hash>.jpg → return "manual"
        var parts = fileName.Split('.');
        // … some files have extra dots in the stem; locate the "preview"
        // segment and take the next one as the stretch id.
        for (var i = 0; i < parts.Length - 1; i++) {
            if (parts[i].Equals("preview", StringComparison.OrdinalIgnoreCase)) {
                return parts[i + 1];
            }
        }
        return "unknown";
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

    private static readonly string[] SampleTags = { "good-seeing" };

    // Boxes a nullable value type for an ADO.NET parameter, mapping null to DBNull.
    // (A direct '(object?)value ?? DBNull.Value' trips CA1508, which does not model
    // Nullable<T> boxing returning null.)
    private static object DbValue<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded sample session + 3 sample frames into catalog")]
    private partial void LogSeededFrames();
}