#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §54 real <see cref="IBugReportService"/> — bundles the diagnostic context a
/// developer needs into a self-contained ZIP under <c>{profileDir}/bug-reports/</c>:
/// the daemon's rolling log files (<c>logs/</c>), the active <c>profile.json</c>,
/// and a generated <c>system-info.json</c> (OS / runtime / app version). The user
/// downloads it from the §54 "Send me a bug report" UI and attaches it to a report.
///
/// <para><see cref="PrepareAsync"/> packages the bundle within the request (config +
/// logs are kilobytes-to-megabytes, not the frame library) and returns a "ready"
/// record with the real size + a download URL. <see cref="OpenDownloadAsync"/>
/// resolves the bundle by its server-minted id (embedded in the file name, so the
/// download survives a daemon restart and carries no caller-controlled path) and
/// streams it. Packaging writes to a hidden <c>.tmp-</c> file and renames it into
/// place, so a crash mid-zip never leaves a half-written bundle a reader could
/// resolve. Replaces the former placeholder (synthetic record, 404 download).</para>
/// </summary>
public sealed partial class BugReportService : IBugReportService {

    private const string BugReportsDirName = "bug-reports";
    private const string LogsDirName = "logs";
    private const string ProfileFileName = "profile.json";
    private const string ZipPrefix = "bugreport-";
    private const string ZipExtension = ".zip";
    private const string TempPrefix = ".tmp-";
    private const string LogGlob = "openastroara-*.log";
    // Keep only the newest N bundles — each prepare stages a fresh ZIP and there's
    // no other reaper, so without a cap repeated prepares (UI retries, operator
    // debugging) would grow {profileDir}/bug-reports/ without bound.
    internal const int MaxRetainedBundles = 10;

    private readonly string _profileDir;
    private readonly string _bugReportsDir;
    private readonly ILogger<BugReportService> _logger;

    public BugReportService(string profileDir, ILogger<BugReportService> logger) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        ArgumentNullException.ThrowIfNull(logger);
        _profileDir = profileDir;
        _bugReportsDir = Path.Combine(profileDir, BugReportsDirName);
        _logger = logger;
    }

    public async Task<BugReportPreparationDto> PrepareAsync(string? idempotencyKey, CancellationToken ct) {
        // idempotencyKey is accepted for contract symmetry but NOT enforced: a retried
        // prepare stages a fresh bundle each time (there's no retention pruning yet —
        // these are user-initiated + occasional). Log it so an operator retrying a
        // timed-out POST has a signal that dedup is off.
        if (!string.IsNullOrEmpty(idempotencyKey)) {
            LogIdempotencyNotEnforced();
        }

        var id = Guid.NewGuid();
        var createdUtc = DateTimeOffset.UtcNow;

        // ZipArchive has no async write path; run the synchronous IO off the request
        // thread so a larger logs/ set doesn't tie up the connection's thread-pool slot.
        var sizeBytes = await Task.Run(() => BuildBundleCore(id, createdUtc, ct), ct).ConfigureAwait(false);

        LogPrepared(id, _bugReportsDir);
        return new BugReportPreparationDto(
            PreparationId: id,
            Status: "ready",
            EstimatedSizeBytes: sizeBytes,
            DownloadUrl: DownloadUrl(id),
            CompletedUtc: createdUtc);
    }

    private long BuildBundleCore(Guid id, DateTimeOffset createdUtc, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_bugReportsDir);
        // Reap .tmp-*.zip orphans left by a prepare that crashed mid-zip (before its
        // File.Move reveal) — neither PruneOldBundles nor FindBundlePath sees the temp
        // prefix, so without this they'd accumulate. Runs before this prepare stages its
        // own temp, so it never targets ours; a concurrent prepare holds its temp with
        // FileShare.None, so the delete simply skips that (locked) one.
        SweepStaleTempBundles();

        var baseName = ZipPrefix + createdUtc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
            + "-" + id.ToString("N", CultureInfo.InvariantCulture);
        var tempPath = Path.Combine(_bugReportsDir, TempPrefix + id.ToString("N", CultureInfo.InvariantCulture) + ZipExtension);
        var zipPath = Path.Combine(_bugReportsDir, baseName + ZipExtension);

        try {
            using (var zipStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create)) {
                // system-info.json is always present, so the bundle is never empty even
                // before the daemon has written a log or a profile.
                WriteTextEntry(archive, "system-info.json", BuildSystemInfoJson(id, createdUtc));

                var logsDir = Path.Combine(_profileDir, LogsDirName);
                if (Directory.Exists(logsDir)) {
                    foreach (var logPath in Directory.EnumerateFiles(logsDir, LogGlob, SearchOption.TopDirectoryOnly)) {
                        ct.ThrowIfCancellationRequested();
                        // A single log that vanished (the rolling sink can delete a retained
                        // file mid-bundle) or is unreadable is skipped, not fatal to the bundle.
                        TryAddFileEntry(archive, logPath, LogsDirName + "/" + Path.GetFileName(logPath));
                    }
                }

                var profilePath = Path.Combine(_profileDir, ProfileFileName);
                if (File.Exists(profilePath)) {
                    TryAddFileEntry(archive, profilePath, ProfileFileName);
                }
            }

            ct.ThrowIfCancellationRequested();
            var sizeBytes = new FileInfo(tempPath).Length;
            // Reveal the finished bundle atomically — a reader (OpenDownloadAsync) resolves
            // by the id-suffixed name, so it only ever sees a complete archive.
            File.Move(tempPath, zipPath, overwrite: false);
            PruneOldBundles(zipPath);
            return sizeBytes;
        } catch {
            // tempPath: the staged archive if the rename hadn't happened. zipPath: only
            // exists if File.Move succeeded and a later step (the prune) threw — in which
            // case the caller never got a download URL, so reclaiming it is correct, not a
            // lost bundle. TryDelete is File.Exists-guarded, so it's a no-op otherwise.
            TryDelete(tempPath);
            TryDelete(zipPath);
            throw;
        }
    }

    // Best-effort reaper for .tmp-*.zip orphans from a crashed prepare. Never fails the
    // caller; a still-in-progress prepare's temp is FileShare.None-locked, so TryDelete
    // skips it and only genuinely orphaned temps are removed.
    private void SweepStaleTempBundles() {
        try {
            foreach (var temp in Directory.EnumerateFiles(_bugReportsDir, TempPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)) {
                TryDelete(temp);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogPruneFailed(ex);
        }
    }

    private string BuildSystemInfoJson(Guid id, DateTimeOffset createdUtc) {
        var asm = typeof(BugReportService).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
        var obj = new JsonObject {
            ["report_id"] = id.ToString("D", CultureInfo.InvariantCulture),
            ["generated_utc"] = createdUtc.ToString("O", CultureInfo.InvariantCulture),
            ["app_version"] = version,
            ["os_description"] = RuntimeInformation.OSDescription,
            ["os_architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            // PII note: the absolute profile path can carry the OS username
            // (e.g. /home/alice/.local/share/openastroara). Kept deliberately — it's
            // useful for "can't find the profile" diagnostics and the user downloads
            // the bundle themselves before choosing to share it — but don't surface it
            // anywhere it would be auto-published, and tell users it's in the bundle.
            ["profile_dir"] = _profileDir,
        };
        return obj.ToJsonString();
    }

    // Add one regular file as a ZIP entry, skipping it (not failing the bundle) if it's
    // a symlink, vanished, or unreadable. Symlinks are refused for the same reason backups
    // refuse them — CreateEntryFromFile would follow the link and bundle whatever it points
    // at, possibly outside the profile root.
    private bool TryAddFileEntry(ZipArchive archive, string sourcePath, string entryName) {
        try {
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0) {
                return false;
            }
            // CreateEntryFromFile reads the whole file; a CancellationToken can't abort it
            // mid-copy (a .NET limitation), so cancellation is checked per-file by the caller.
            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Vanished (rolling sink deleted a retained log) or unreadable between the
            // directory scan and now — skip it. FileNotFoundException / DirectoryNotFoundException
            // derive from IOException.
            LogEntrySkipped(sourcePath, ex);
            return false;
        }
    }

    // Keep only the newest MaxRetainedBundles ZIPs (incl. the one just revealed). Best-effort:
    // a failure here must never fail an already-staged bundle. Bundles are named
    // bugreport-{yyyyMMddTHHmmssZ}-{id}.zip, so an ordinal-descending filename sort is
    // chronological (newest first) without touching mtimes.
    private void PruneOldBundles(string justCreated) {
        try {
            // Keep the just-created bundle plus the newest (cap-1) others, so the total
            // never exceeds the cap AND the bundle we just revealed is never the one
            // reaped (guards a same-second tie where an id sort could rank it last).
            var stale = Directory.EnumerateFiles(_bugReportsDir, ZipPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)
                .Where(p => !string.Equals(p, justCreated, StringComparison.Ordinal))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(MaxRetainedBundles - 1)
                .ToList();
            foreach (var path in stale) {
                TryDelete(path);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // best-effort retention; the prepared bundle is already revealed.
            LogPruneFailed(ex);
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content) {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    public async Task<(Stream Stream, string FileName)?> OpenDownloadAsync(Guid preparationId, CancellationToken ct) {
        var path = await Task.Run(() => FindBundlePath(preparationId, ct), ct).ConfigureAwait(false);
        if (path is null) {
            return null;
        }
        try {
            // Owned by the response pipeline (disposed when the response finishes). Holding
            // the open handle closes the resolve→serve window: a delete after this can't
            // turn the stream into a 500.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            return (stream, Path.GetFileName(path));
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException) {
            LogBundleVanished(path, ex);
            return null;
        }
    }

    // Resolve a preparation id to its bundle path by matching the id-suffixed name. The id
    // is a server-minted guid, so the glob carries no caller-controlled wildcard surface
    // beyond the 32-hex id itself.
    private string? FindBundlePath(Guid id, CancellationToken ct) {
        if (!Directory.Exists(_bugReportsDir)) {
            return null;
        }
        var suffix = "-" + id.ToString("N", CultureInfo.InvariantCulture) + ZipExtension;
        foreach (var path in Directory.EnumerateFiles(_bugReportsDir, ZipPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)) {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal)) {
                return path;
            }
        }
        return null;
    }

    private static Uri DownloadUrl(Guid id) =>
        new("/api/v1/bugreport/download?preparationId=" + id.ToString("D", CultureInfo.InvariantCulture), UriKind.Relative);

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best-effort cleanup; never let a cleanup failure replace the exception we're unwinding.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bug-report bundle {ReportId} prepared under {BugReportsDir}")]
    partial void LogPrepared(Guid reportId, string bugReportsDir);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Bug-report bundle {BundlePath} vanished between resolve and open — serving 404")]
    partial void LogBundleVanished(string bundlePath, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Bug-report prepare received an Idempotency-Key but dedup is not implemented — a retry stages another bundle")]
    partial void LogIdempotencyNotEnforced();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping unreadable/vanished bug-report entry {SourcePath}")]
    partial void LogEntrySkipped(string sourcePath, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bug-report bundle retention prune failed (best-effort)")]
    partial void LogPruneFailed(Exception ex);
}
