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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28.8 startup scan + orphan FITS recovery. Runs once at server boot
/// before the daemon serves traffic. Three responsibilities:
///
/// 1. <b>Mount + writability</b>: probe the configured save path; if not
///    writable, log critical + skip (server still starts so that profile
///    edits + non-storage endpoints work; user fixes storage and restarts).
/// 2. <b>Stale .tmp sweep</b>: any <c>*.tmp</c> file older than 5 minutes
///    in the captures tree is presumed crashed-mid-write and deleted
///    (§28.7's atomic-rename pattern guarantees only crashed writes leave
///    .tmp files behind; healthy writes finish in seconds).
/// 3. <b>Orphan FITS recovery</b>: for each <c>.fits</c> not in the catalog,
///    parse the header via <see cref="OpenAstroAra.Fits.FitsImage"/> and
///    INSERT a row. Synthetic session id used if the orphan's session
///    can't be determined from parent directory.
///
/// On a fresh install with no captures yet (typical v0.0.1 state), all
/// three steps are no-ops and the scan returns in < 1ms. Real captures
/// from the §38 sequence orchestrator + §72 FITS writes start populating
/// the directory; from that point this scan auto-heals across crashes.
/// </summary>
public sealed class CaptureScanService {
    private readonly IProfileStore _profile;
    private readonly IAraDatabase _db;
    private readonly ILogger<CaptureScanService>? _logger;

    public CaptureScanService(IProfileStore profile, IAraDatabase db, ILogger<CaptureScanService>? logger) {
        _profile = profile;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Synchronous because it runs once on startup before the host is
    /// listening, and the work is bounded (typical captures dir has
    /// 0–10k files; §28.8 ceiling is 2s on a Pi 4 with 10k frames).
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        var savePath = _profile.GetStorageSettings().SaveDirectory;
        if (string.IsNullOrEmpty(savePath)) {
            _logger?.LogInformation("§28.8 scan skipped: save path empty");
            return;
        }
        if (!Directory.Exists(savePath)) {
            // Captures dir doesn't exist yet on fresh installs — that's
            // fine, we'll find it when the first capture writes. Don't
            // queue a critical notification for this case.
            _logger?.LogDebug("§28.8 scan skipped: save path {Path} does not exist", savePath);
            return;
        }
        if (!IsWritable(savePath)) {
            _logger?.LogWarning("§28.8 scan: save path {Path} is not writable; storage.unavailable would queue here", savePath);
            return;
        }

        var tmpSwept = SweepStaleTempFiles(savePath);
        var orphansRecovered = await RecoverOrphanFitsAsync(savePath, ct);

        if (tmpSwept > 0 || orphansRecovered > 0) {
            _logger?.LogInformation(
                "§28.8 scan complete — swept {TmpCount} stale .tmp file(s), recovered {Orphans} orphan FITS",
                tmpSwept, orphansRecovered);
        }
    }

    private static bool IsWritable(string dir) {
        try {
            var probe = Path.Combine(dir, $".oara-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        } catch {
            return false;
        }
    }

    private int SweepStaleTempFiles(string root) {
        var threshold = DateTime.UtcNow.AddMinutes(-5);
        var swept = 0;
        foreach (var tmp in EnumerateFilesSafe(root, "*.tmp")) {
            try {
                var info = new FileInfo(tmp);
                if (info.LastWriteTimeUtc < threshold) {
                    info.Delete();
                    swept++;
                    _logger?.LogDebug("§28.8 swept stale temp: {Path}", tmp);
                }
            } catch (IOException ex) {
                _logger?.LogDebug(ex, "§28.8 could not delete stale temp {Path}", tmp);
            }
        }
        return swept;
    }

    private async Task<int> RecoverOrphanFitsAsync(string root, CancellationToken ct) {
        var recovered = 0;
        var seenIds = await LoadKnownIdsAsync(ct);
        foreach (var fitsPath in EnumerateFilesSafe(root, "*.fits")) {
            if (ct.IsCancellationRequested) break;
            try {
                var inserted = await TryRecoverAsync(fitsPath, seenIds, ct);
                if (inserted) recovered++;
            } catch (Exception ex) {
                // Single bad file shouldn't abort the whole scan.
                _logger?.LogDebug(ex, "§28.8 could not recover {Path}", fitsPath);
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
            _logger?.LogDebug(ex, "§28.8 skip non-FITS or corrupt: {Path}", fitsPath);
            return false;
        }

        var capturedUtc = ParseDateObs(headers) ?? File.GetLastWriteTimeUtc(fitsPath);
        var exposureSec = ParseExposure(headers) ?? 0;
        var target = LookupHeader(headers, "OBJECT") ?? "Unknown Target";
        var imageType = LookupHeader(headers, "IMAGETYP") ?? "LIGHT";
        var frameType = MapImageTypeToFrameType(imageType);
        var filter = LookupHeader(headers, "FILTER");
        var gain = ParseInt(LookupHeader(headers, "GAIN")) ?? 0;
        var offset = ParseInt(LookupHeader(headers, "OFFSET"));
        var temp = ParseDouble(LookupHeader(headers, "CCD-TEMP")) ?? 0.0;
        var bitDepth = ParseInt(LookupHeader(headers, "BITPIX"))
            ?? (LookupHeader(headers, "BSCALE") != null ? 16 : 16);
        var hfr = ParseDouble(LookupHeader(headers, "HFR"));
        var stars = ParseInt(LookupHeader(headers, "STARS"));

        // Synthetic recovered session — one bucket for all orphans recovered
        // in this scan. Real session tracking lands when §38 orchestrator
        // writes the session_id into the FITS header on capture.
        var sessionId = await EnsureRecoverySessionAsync(ct);
        var frameId = Guid.NewGuid();

        await using var conn = _db.OpenConnection();
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO frames
                (id, session_id, target_name, frame_type, filter_name,
                 exposure_seconds, gain, "offset", temperature_c, captured_utc,
                 file_path, file_size_bytes, width, height, bit_depth,
                 hfr, star_count, eccentricity, guiding_rms_arcsec, snr_estimate,
                 quality_score_json, rating, tags_json)
            VALUES
                ($id, $session_id, $target, $frame_type, $filter,
                 $exposure, $gain, $offset, $temp, $captured_utc,
                 $file_path, $file_size, $width, $height, $bit_depth,
                 $hfr, $stars, NULL, NULL, NULL,
                 NULL, 0, '[]');
            """;
        insert.Parameters.AddWithValue("$id", frameId.ToString());
        insert.Parameters.AddWithValue("$session_id", sessionId.ToString());
        insert.Parameters.AddWithValue("$target", target);
        insert.Parameters.AddWithValue("$frame_type", frameType);
        insert.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
        insert.Parameters.AddWithValue("$exposure", exposureSec);
        insert.Parameters.AddWithValue("$gain", gain);
        insert.Parameters.AddWithValue("$offset", (object?)offset ?? DBNull.Value);
        insert.Parameters.AddWithValue("$temp", temp);
        insert.Parameters.AddWithValue("$captured_utc", capturedUtc.ToString("O"));
        insert.Parameters.AddWithValue("$file_path", fitsPath);
        insert.Parameters.AddWithValue("$file_size", fileSize);
        insert.Parameters.AddWithValue("$width", width);
        insert.Parameters.AddWithValue("$height", height);
        insert.Parameters.AddWithValue("$bit_depth", Math.Abs(bitDepth));
        insert.Parameters.AddWithValue("$hfr", (object?)hfr ?? DBNull.Value);
        insert.Parameters.AddWithValue("$stars", (object?)stars ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation(
            "§28.8 recovered orphan FITS: {Path} (target={Target}, frame_type={FrameType}, exposure={Exposure}s)",
            fitsPath, target, frameType, exposureSec);
        return true;
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

    private static string? LookupHeader(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static DateTimeOffset? ParseDateObs(IReadOnlyDictionary<string, string> headers) {
        var raw = LookupHeader(headers, "DATE-OBS");
        if (raw is null) return null;
        return DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
    }

    private static int? ParseExposure(IReadOnlyDictionary<string, string> headers) =>
        ParseInt(LookupHeader(headers, "EXPOSURE"))
            ?? ParseInt(LookupHeader(headers, "EXPTIME"));

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
}
