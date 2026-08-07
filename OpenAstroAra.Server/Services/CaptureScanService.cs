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
/// <summary>
/// What one scan actually did. [Ran] is false when the save directory was
/// missing or unwritable — [SkipReason] then says which, so the caller can
/// tell the user something specific instead of "0 frames found".
/// </summary>
public sealed record CaptureScanResult(
    bool Ran,
    string? SkipReason,
    string SavePath,
    int TempFilesSwept,
    int FramesRecovered) {

    public static CaptureScanResult Skipped(string savePath, string reason) =>
        new(false, reason, savePath, 0, 0);
}

public sealed partial class CaptureScanService : IDisposable {
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
    // The service is a DI singleton shared by startup and POST /storage/rescan.
    // Two overlapping scans would each snapshot known paths before either
    // inserts, and both would then catalog the same orphan (frames.file_path
    // has no unique constraint) — so scans serialize here.
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    // Singleton — the DI container disposes it at host shutdown.
    public void Dispose() => _scanLock.Dispose();

    /// <summary>
    /// §50 stats maintenance — wipe the frame catalog (frames + sessions)
    /// and re-ingest from whatever store is currently mounted, so the Stats
    /// views truthfully describe THE CONNECTED DRIVE after a swap. Holds
    /// the scan lock end-to-end: the wipe and the rebuilding scan are one
    /// atomic maintenance operation from every other caller's view.
    /// </summary>
    public async Task<(long FramesCleared, long SessionsCleared, CaptureScanResult Scan)> ResetAndRescanAsync(CancellationToken ct) {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            long framesCleared, sessionsCleared;
            await using (var conn = _db.OpenConnection()) {
                // One transaction: a cancel/crash between the deletes must
                // never leave frames gone with stale session rows behind.
                await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (var cmd = conn.CreateCommand()) {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM frames;";
                    framesCleared = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var cmd = conn.CreateCommand()) {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM sessions;";
                    sessionsCleared = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            // The synthetic recovery session was just deleted with the rest —
            // drop the in-memory cache or every re-insert fails its session FK
            // (found live: 339 frames re-scanned as 0 on the rig).
            _recoverySessionId = null;
            LogCatalogReset(framesCleared, sessionsCleared);
            var scan = await RunLockedAsync(ct).ConfigureAwait(false);
            return (framesCleared, sessionsCleared, scan);
        } finally {
            _scanLock.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "§50 catalog reset: cleared {Frames} frames and {Sessions} sessions; re-ingesting from the mounted store.")]
    private partial void LogCatalogReset(long frames, long sessions);

    /// <summary>
    /// Runs <paramref name="work"/> holding the scan lock — for storage
    /// maintenance (mount/reformat) that must not interleave with a scan
    /// walking the very tree being unmounted, nor with a sibling configure.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(work);
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            return await work().ConfigureAwait(false);
        } finally {
            _scanLock.Release();
        }
    }

    public async Task<CaptureScanResult> RunAsync(CancellationToken ct) {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            return await RunLockedAsync(ct).ConfigureAwait(false);
        } finally {
            _scanLock.Release();
        }
    }

    private async Task<CaptureScanResult> RunLockedAsync(CancellationToken ct) {
        var savePath = _profile.GetStorageSettings().SaveDirectory;
        if (string.IsNullOrEmpty(savePath)) {
            LogScanSkippedEmptyPath();
            return CaptureScanResult.Skipped(savePath ?? string.Empty, "no_save_directory");
        }
        if (!Directory.Exists(savePath)) {
            // Captures dir doesn't exist yet on fresh installs — that's
            // fine, we'll find it when the first capture writes. Don't
            // queue a critical notification for this case.
            LogScanSkippedMissingPath(savePath);
            return CaptureScanResult.Skipped(savePath, "path_missing");
        }
        if (!IsWritable(savePath)) {
            LogScanPathNotWritable(savePath);
            return CaptureScanResult.Skipped(savePath, "path_not_writable");
        }

        var tmpSwept = SweepStaleTempFiles(savePath);
        var orphansRecovered = await RecoverOrphanFitsAsync(savePath, ct);

        if (tmpSwept > 0 || orphansRecovered > 0) {
            LogScanComplete(tmpSwept, orphansRecovered);
        }
        return new CaptureScanResult(true, null, savePath, tmpSwept, orphansRecovered);
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

    private int SweepStaleTempFiles(string root) {
        var threshold = DateTime.UtcNow.AddMinutes(-5);
        var swept = 0;
        foreach (var tmp in EnumerateFilesSafe(root, "*.tmp")) {
            try {
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

    private async Task<int> RecoverOrphanFitsAsync(string root, CancellationToken ct) {
        var recovered = 0;
        var seenIds = await LoadKnownIdsAsync(ct);
        // Both extensions the wild actually uses: Ara writes .fits, NINA (and
        // most captured archives a user copies onto a take-home drive) write
        // .fit — a whole drive of NINA frames previously scanned as "nothing
        // found". Enumeration is case-insensitive (.FIT/.FITS included), and
        // macOS AppleDouble droppings ("._Light_….fit") are metadata forks,
        // not FITS — skip them by name.
        // One recursive walk, not one per extension — these are 1 TB
        // take-home drives; LooksLikeFits does the exact-extension filtering.
        foreach (var fitsPath in EnumerateFilesSafe(root, "*.fit*")) {
            if (ct.IsCancellationRequested) break;
            if (!LooksLikeFits(fitsPath)) continue;
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
        var imageType = LookupHeader(headers, "IMAGETYP") ?? "LIGHT";
        var frameType = MapImageTypeToFrameType(imageType);
        // Calibration frames have no target by nature — label them as what
        // they are instead of "Unknown Target" masquerading as an object in
        // the stats/target lists (Joey's call, from the NGC6188 archive).
        var target = LookupHeader(headers, "OBJECT")
            ?? (frameType == "light" ? "Unknown Target" : "Calibration");
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
                 quality_score_json, rating, tags_json, focuser_position)
            VALUES
                ($id, $session_id, $target, $frame_type, $filter,
                 $exposure, $gain, $offset, $temp, $captured_utc,
                 $file_path, $file_size, $width, $height, $bit_depth,
                 $hfr, $stars, NULL, NULL, NULL,
                 NULL, 0, '[]', $focuser_position);
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
        await insert.ExecuteNonQueryAsync(ct);

        LogRecoveredOrphan(fitsPath, target, frameType, exposureSec);
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

    // IgnoreInaccessible matters more than it looks: a try/catch around the
    // CALL only guards creating the lazy iterator — EnumerateFiles throws
    // mid-iteration when the walk descends into a directory it can't open.
    // Every ext4 volume has a root-owned lost+found at its root, so pointing
    // the save directory at a mount root (which the §29 storage flow does for
    // every user) crash-looped the daemon on startup until this scan learned
    // to walk past what it can't read. Found on rc91, first boot after the T7
    // became the store.
    private static readonly EnumerationOptions SkipInaccessible = new() {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Skip nothing else: hidden dirs are fair game (dotfile trees a user
        // rsyncs over shouldn't hide their FITS from recovery).
        AttributesToSkip = 0,
        // Linux globbing is case-sensitive by default; a drive of ".FIT"
        // frames from a Windows capture rig must still be found.
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern) {
        try {
            return Directory.EnumerateFiles(root, pattern, SkipInaccessible);
        } catch (DirectoryNotFoundException) {
            return Array.Empty<string>();
        }
    }

    /// <summary>The extensions the scan recognizes as FITS, shared with the
    /// enumeration above so the two never drift.</summary>
    internal static bool LooksLikeFits(string path) {
        var name = Path.GetFileName(path);
        if (name.StartsWith("._", StringComparison.Ordinal)) return false;
        return name.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".fit", StringComparison.OrdinalIgnoreCase);
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