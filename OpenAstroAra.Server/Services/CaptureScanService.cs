#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System.Security.Cryptography;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28.8 startup scan + orphan FITS recovery. Runs once at server boot
/// before the daemon serves traffic. Three responsibilities:
///
/// 1. <b>Mount + writability</b>: probe the configured save path; if not
///    writable, log critical + skip (server still starts so that profile
///    edits + non-storage endpoints work; user fixes storage and restarts).
/// 2. <b>Interrupted lifecycle reconciliation</b>: stale tracked attempts are
///    marked Partial/Failed. Tracked temp bytes remain for review; only stale
///    untracked <c>*.fits.tmp</c> files are deleted.
/// 3. <b>Orphan FITS recovery</b>: for each <c>.fits</c> not in the catalog,
///    parse, hash, and transactionally INSERT a row. A matching lifecycle
///    preserves the original frame/session identity; unrelated files use a
///    synthetic recovered session.
///
/// On a fresh install with no captures yet (typical v0.0.1 state), all
/// three steps are no-ops and the scan returns in < 1ms. Real captures
/// from the §38 sequence orchestrator + §72 FITS writes start populating
/// the directory; from that point this scan auto-heals across crashes.
/// </summary>
public sealed partial class CaptureScanService {
    private readonly IProfileStore _profile;
    private readonly IAraDatabase _db;
    private readonly ILogger<CaptureScanService> _logger;

    public CaptureScanService(IProfileStore profile, IAraDatabase db, ILogger<CaptureScanService>? logger) {
        _profile = profile;
        _db = db;
        _logger = logger ?? NullLogger<CaptureScanService>.Instance;
    }

    /// <summary>
    /// Synchronous because it runs once on startup before the host is
    /// listening, and the work is bounded (typical captures dir has
    /// 0–10k files; §28.8 ceiling is 2s on a Pi 4 with 10k frames).
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        var savePath = _profile.GetStorageSettings().SaveDirectory;
        if (string.IsNullOrEmpty(savePath)) {
            LogScanSkippedEmptyPath();
            return;
        }
        if (!Directory.Exists(savePath)) {
            // Captures dir doesn't exist yet on fresh installs — that's
            // fine, we'll find it when the first capture writes. Don't
            // queue a critical notification for this case.
            LogScanSkippedMissingPath(savePath);
            return;
        }
        if (!IsWritable(savePath)) {
            LogScanPathNotWritable(savePath);
            return;
        }

        await ReconcileInterruptedStorageAsync(ct).ConfigureAwait(false);
        var reviewableTemps = await LoadReviewableTempPathsAsync(ct).ConfigureAwait(false);
        var tmpSwept = SweepStaleTempFiles(savePath, reviewableTemps);
        var orphansRecovered = await RecoverOrphanFitsAsync(savePath, ct);

        if (tmpSwept > 0 || orphansRecovered > 0) {
            LogScanComplete(tmpSwept, orphansRecovered);
        }
    }

    private static bool IsWritable(string dir) {
        try {
            var probe = Path.Combine(dir, $".oara-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            return false;
        }
    }

    private int SweepStaleTempFiles(string root, HashSet<string> reviewableTemps) {
        var threshold = DateTime.UtcNow.AddMinutes(-5);
        var swept = 0;
        foreach (var tmp in EnumerateFilesSafe(root, "*.fits.tmp")) {
            try {
                if (reviewableTemps.Contains(Path.GetFullPath(tmp))) {
                    continue; // lifecycle evidence remains until explicit operator cleanup lands
                }
                var info = new FileInfo(tmp);
                if (info.LastWriteTimeUtc < threshold) {
                    info.Delete();
                    swept++;
                    LogSweptStaleTemp(tmp);
                }
            } catch (IOException ex) {
                LogCouldNotDeleteTemp(ex, tmp);
            }
        }
        return swept;
    }

    private async Task<HashSet<string>> LoadReviewableTempPathsAsync(CancellationToken ct) {
        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT temporary_path
            FROM frame_storage_lifecycle
            WHERE state = 'partial' AND temporary_path IS NOT NULL;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            paths.Add(Path.GetFullPath(reader.GetString(0)));
        }
        return paths;
    }

    /// <summary>
    /// Close crash-stranded lifecycle rows before file reconciliation. Rows with
    /// remaining bytes become Partial; rows with no bytes become Failed. A frame
    /// row and lifecycle row can only diverge on legacy/manual inserts, so heal
    /// that case to Complete first.
    /// </summary>
    private async Task ReconcileInterruptedStorageAsync(CancellationToken ct) {
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var conn = _db.OpenConnection();

        await using (var completed = conn.CreateCommand()) {
            completed.CommandText = """
                UPDATE frame_storage_lifecycle
                SET state = 'complete',
                    completed_utc = COALESCE(completed_utc,
                        (SELECT captured_utc FROM frames WHERE frames.id = frame_storage_lifecycle.frame_id)),
                    byte_count = COALESCE(byte_count,
                        (SELECT file_size_bytes FROM frames WHERE frames.id = frame_storage_lifecycle.frame_id)),
                    checksum_sha256 = COALESCE(checksum_sha256,
                        (SELECT sha256 FROM frames WHERE frames.id = frame_storage_lifecycle.frame_id)),
                    temporary_path = NULL,
                    failure_code = NULL,
                    failure_message = NULL,
                    updated_utc = $now
                WHERE state IN ('accepted', 'exposing', 'downloading', 'persisting')
                  AND EXISTS (SELECT 1 FROM frames WHERE frames.id = frame_storage_lifecycle.frame_id);
                """;
            completed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await completed.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var interrupted = new List<(Guid FrameId, string? TemporaryPath, string FinalPath, string State)>();
        await using (var select = conn.CreateCommand()) {
            select.CommandText = """
                SELECT frame_id, temporary_path, final_path, state
                FROM frame_storage_lifecycle
                WHERE state IN ('accepted', 'exposing', 'downloading', 'persisting', 'failed')
                  AND updated_utc < $threshold;
                """;
            select.Parameters.AddWithValue("$threshold", threshold.ToString("O"));
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
                interrupted.Add((
                    Guid.Parse(reader.GetString(0)),
                    await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        foreach (var item in interrupted) {
            ct.ThrowIfCancellationRequested();
            var finalExists = File.Exists(item.FinalPath);
            var tempExists = item.TemporaryPath is not null && File.Exists(item.TemporaryPath);
            if (item.State == "failed" && !finalExists && !tempExists) {
                continue; // keep the original durable failure reason when no bytes survived
            }
            var state = finalExists || tempExists ? "partial" : "failed";
            var code = finalExists ? "catalog_registration_interrupted"
                : tempExists ? "write_interrupted"
                : "capture_interrupted";
            var message = finalExists
                ? "A committed source file survived a daemon interruption and is awaiting catalog recovery."
                : tempExists
                    ? "A temporary source file survived a daemon interruption and requires review."
                    : "Capture was interrupted before durable source bytes were committed.";
            await using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE frame_storage_lifecycle
                SET state = $state,
                    failure_code = $failure_code,
                    failure_message = $failure_message,
                    updated_utc = $now
                WHERE frame_id = $frame_id
                  AND state IN ('accepted', 'exposing', 'downloading', 'persisting', 'failed');
                """;
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$failure_code", code);
            update.Parameters.AddWithValue("$failure_message", message);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$frame_id", item.FrameId.ToString("D"));
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<int> RecoverOrphanFitsAsync(string root, CancellationToken ct) {
        var recovered = 0;
        var seenIds = await LoadKnownIdsAsync(ct);
        foreach (var fitsPath in EnumerateFilesSafe(root, "*.fits")) {
            if (ct.IsCancellationRequested) break;
            try {
                var inserted = await TryRecoverAsync(fitsPath, seenIds, ct);
                if (inserted) recovered++;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) {
                // Single bad file shouldn't abort the whole scan.
                LogCouldNotRecover(ex, fitsPath);
            }
        }
        return recovered;
    }

    private async Task<HashSet<string>> LoadKnownIdsAsync(CancellationToken ct) {
        // §28.8 step 4 says "look up by file path"; we look up by id parsed
        // from the FITS header (more durable across user-renames) but fall
        // back to file_path equality if the header has no id. For v0.0.1
        // we cheat and load all file_paths into memory — typical catalog
        // is well under 100k rows so the set fits cheaply.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM frames;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            paths.Add(reader.GetString(0));
        }
        return paths;
    }

    private async Task<bool> TryRecoverAsync(string fitsPath, HashSet<string> knownPaths, CancellationToken ct) {
        if (knownPaths.Contains(fitsPath)) return false;  // already cataloged

        // Header-only parse to avoid loading megabytes of pixel data per
        // orphan. We still open the file via FitsImage.Open + ReadHeaders.
        IReadOnlyDictionary<string, string> headers;
        int width, height;
        long fileSize;
        try {
            using var fits = OpenAstroAra.Fits.FitsImage.Open(fitsPath);
            headers = fits.ReadHeaders();
            (width, height) = fits.GetDimensions();
            fileSize = new FileInfo(fitsPath).Length;
        } catch (OpenAstroAra.Fits.FitsException ex) {
            LogSkipCorruptFits(ex, fitsPath);
            return false;
        }

        var capturedUtc = ParseDateObs(headers) ?? File.GetLastWriteTimeUtc(fitsPath);
        var exposureSec = ParseExposure(headers) ?? 0.0;
        var target = LookupHeader(headers, "OBJECT") ?? "Unknown Target";
        var imageType = LookupHeader(headers, "IMAGETYP") ?? "LIGHT";
        var frameType = MapImageTypeToFrameType(imageType);
        var filter = LookupHeader(headers, "FILTER");
        // §28: a FITS without a GAIN header records null (unknown), not a fake 0.
        var gain = ParseInt(LookupHeader(headers, "GAIN"));
        var offset = ParseInt(LookupHeader(headers, "OFFSET"));
        // Null when the FITS carries no CCD-TEMP — recorded honestly, no sentinel.
        var temp = ParseDouble(LookupHeader(headers, "CCD-TEMP"));
        var bitDepth = ParseInt(LookupHeader(headers, "BITPIX"))
            ?? (LookupHeader(headers, "BSCALE") != null ? 16 : 16);
        var hfr = ParseDouble(LookupHeader(headers, "HFR"));
        var stars = ParseInt(LookupHeader(headers, "STARS"));
        // §38: focuser step position for the §50.4 focus-vs-temperature view.
        // FOCUSPOS is the NINA/ARA write keyword; FOCPOS is the legacy alias.
        var focuserPos = ParseInt(LookupHeader(headers, "FOCUSPOS"))
            ?? ParseInt(LookupHeader(headers, "FOCPOS"));

        // A lifecycle row survives a crash before catalog registration. Preserve
        // its original frame/session identity; unrelated orphan files use the
        // synthetic recovered-session bucket.
        var pending = await FindPendingStorageAsync(fitsPath, ct).ConfigureAwait(false);
        var sessionId = pending?.SessionId ?? await EnsureRecoverySessionAsync(ct).ConfigureAwait(false);
        var frameId = pending?.FrameId ?? Guid.NewGuid();
        string checksum;
        await using (var stream = new FileStream(fitsPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan)) {
            checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        var cfaPattern = LookupHeader(headers, "BAYERPAT")?.Trim().ToUpperInvariant();
        var completedUtc = DateTimeOffset.UtcNow;

        await using var conn = _db.OpenConnection();
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var insert = conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO frames
                (id, session_id, target_name, frame_type, filter_name,
                 exposure_seconds, gain, "offset", temperature_c, captured_utc,
                 file_path, file_size_bytes, width, height, bit_depth,
                 hfr, star_count, eccentricity, guiding_rms_arcsec, snr_estimate,
                 quality_score_json, rating, tags_json, focuser_position, sha256)
            VALUES
                ($id, $session_id, $target, $frame_type, $filter,
                 $exposure, $gain, $offset, $temp, $captured_utc,
                 $file_path, $file_size, $width, $height, $bit_depth,
                 $hfr, $stars, NULL, NULL, NULL,
                 NULL, 0, '[]', $focuser_position, $sha256);
            """;
        insert.Parameters.AddWithValue("$id", frameId.ToString());
        insert.Parameters.AddWithValue("$session_id", sessionId.ToString());
        insert.Parameters.AddWithValue("$target", target);
        insert.Parameters.AddWithValue("$frame_type", frameType);
        insert.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
        insert.Parameters.AddWithValue("$exposure", exposureSec);
        insert.Parameters.AddWithValue("$gain", gain is null ? DBNull.Value : gain.Value);
        insert.Parameters.AddWithValue("$offset", DbValue(offset));
        insert.Parameters.AddWithValue("$temp", temp is null ? DBNull.Value : temp.Value);
        insert.Parameters.AddWithValue("$captured_utc", capturedUtc.ToString("O"));
        insert.Parameters.AddWithValue("$file_path", fitsPath);
        insert.Parameters.AddWithValue("$file_size", fileSize);
        insert.Parameters.AddWithValue("$width", width);
        insert.Parameters.AddWithValue("$height", height);
        insert.Parameters.AddWithValue("$bit_depth", Math.Abs(bitDepth));
        insert.Parameters.AddWithValue("$hfr", DbValue(hfr));
        insert.Parameters.AddWithValue("$stars", DbValue(stars));
        insert.Parameters.AddWithValue("$focuser_position", DbValue(focuserPos));
        insert.Parameters.AddWithValue("$sha256", checksum);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var lifecycle = conn.CreateCommand();
        lifecycle.Transaction = transaction;
        lifecycle.CommandText = """
            INSERT INTO frame_storage_lifecycle
                (frame_id, session_id, accepted_utc, completed_utc,
                 temporary_path, final_path, byte_count, checksum_sha256,
                 image_format, cfa_pattern, state, failure_code,
                 failure_message, updated_utc)
            VALUES
                ($frame_id, $session_id, $accepted_utc, $completed_utc,
                 NULL, $final_path, $byte_count, $checksum_sha256,
                 'fits', $cfa_pattern, 'complete', NULL, NULL, $completed_utc)
            ON CONFLICT(frame_id) DO UPDATE SET
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
        lifecycle.Parameters.AddWithValue("$frame_id", frameId.ToString("D"));
        lifecycle.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        lifecycle.Parameters.AddWithValue("$accepted_utc", (pending?.AcceptedUtc ?? capturedUtc).ToString("O"));
        lifecycle.Parameters.AddWithValue("$completed_utc", completedUtc.ToString("O"));
        lifecycle.Parameters.AddWithValue("$final_path", fitsPath);
        lifecycle.Parameters.AddWithValue("$byte_count", fileSize);
        lifecycle.Parameters.AddWithValue("$checksum_sha256", checksum);
        lifecycle.Parameters.AddWithValue("$cfa_pattern", (object?)cfaPattern ?? DBNull.Value);
        await lifecycle.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        LogRecoveredOrphan(fitsPath, target, frameType, exposureSec);
        return true;
    }

    private sealed record PendingStorage(Guid FrameId, Guid SessionId, DateTimeOffset AcceptedUtc);

    private async Task<PendingStorage?> FindPendingStorageAsync(string finalPath, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT frame_id, session_id, accepted_utc
            FROM frame_storage_lifecycle
            WHERE final_path = $final_path
              AND state <> 'complete'
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$final_path", Path.GetFullPath(finalPath));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new PendingStorage(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private Guid? _recoverySessionId;
    private async Task<Guid> EnsureRecoverySessionAsync(CancellationToken ct) {
        if (_recoverySessionId.HasValue) return _recoverySessionId.Value;
        var sid = Guid.NewGuid();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions
                (id, profile_id, sequence_json, started_at, ended_at,
                 recovery_needed, last_completed_instruction_id,
                 current_target_id, frame_count)
            VALUES
                ($id, NULL, NULL, $now, $now, 0, NULL, NULL, 0);
            """;
        cmd.Parameters.AddWithValue("$id", sid.ToString());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        _recoverySessionId = sid;
        return sid;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern) {
        try {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        } catch (UnauthorizedAccessException) {
            return Array.Empty<string>();
        } catch (DirectoryNotFoundException) {
            return Array.Empty<string>();
        }
    }

    // Boxes a nullable value type for an ADO.NET parameter, mapping null to DBNull.
    // (A direct '(object?)value ?? DBNull.Value' trips CA1508, which does not model
    // Nullable<T> boxing returning null.)
    private static object DbValue<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static string? LookupHeader(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // internal (not private) so the §50/§28 DATE-OBS→UTC contract is unit-tested directly; see CaptureScanDateObsTest.
    internal static DateTimeOffset? ParseDateObs(IReadOnlyDictionary<string, string> headers) {
        var raw = LookupHeader(headers, "DATE-OBS");
        if (raw is null) return null;
        // FITS defines DATE-OBS as UTC, but the value is usually written without a zone designator. A bare
        // DateTimeOffset.TryParse assumes *local* for a zoneless value — that both mis-shifts the instant (the
        // recovered frame's captured_utc would be off by this machine's UTC offset) and stores a non-UTC offset
        // suffix that breaks the lexicographic captured_utc comparisons (the `since` bound, ORDER BY). AssumeUniversal
        // reads a zoneless value as UTC; AdjustToUniversal normalizes an explicitly-offset one (e.g. `…-07:00`) to UTC
        // — so captured_utc is always written as `…+00:00`, matching the SqliteFrameRepository path.
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt)
            ? dt
            : null;
    }

    // §28: EXPTIME/EXPOSURE are FITS doubles — a 0.5 s bias header used to parse
    // as null (int.TryParse fails on "0.5") and record 0; now it records 0.5.
    private static double? ParseExposure(IReadOnlyDictionary<string, string> headers) =>
        ParseDouble(LookupHeader(headers, "EXPOSURE"))
            ?? ParseDouble(LookupHeader(headers, "EXPTIME"));

    private static int? ParseInt(string? s) =>
        s is not null && int.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static double? ParseDouble(string? s) =>
        s is not null && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static string MapImageTypeToFrameType(string imageType) =>
        imageType.Trim().ToUpperInvariant() switch {
            "LIGHT" => "light",
            "DARK" => "dark",
            "BIAS" => "bias",
            "FLAT" => "flat",
            "DARKFLAT" => "darkflat",
            "DARK FLAT" => "darkflat",
            _ => "light",
        };

    #region LoggerMessage delegates (CA1848)

    [LoggerMessage(Level = LogLevel.Information, Message = "§28.8 scan skipped: save path empty")]
    private partial void LogScanSkippedEmptyPath();

    [LoggerMessage(Level = LogLevel.Debug, Message = "§28.8 scan skipped: save path {Path} does not exist")]
    private partial void LogScanSkippedMissingPath(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§28.8 scan: save path {Path} is not writable; storage.unavailable would queue here")]
    private partial void LogScanPathNotWritable(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "§28.8 scan complete — swept {TmpCount} stale .tmp file(s), recovered {Orphans} orphan FITS")]
    private partial void LogScanComplete(int tmpCount, int orphans);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§28.8 swept stale temp: {Path}")]
    private partial void LogSweptStaleTemp(string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§28.8 could not delete stale temp {Path}")]
    private partial void LogCouldNotDeleteTemp(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§28.8 could not recover {Path}")]
    private partial void LogCouldNotRecover(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§28.8 skip non-FITS or corrupt: {Path}")]
    private partial void LogSkipCorruptFits(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "§28.8 recovered orphan FITS: {Path} (target={Target}, frame_type={FrameType}, exposure={Exposure}s)")]
    private partial void LogRecoveredOrphan(string path, string target, string frameType, double exposure);

    #endregion
}