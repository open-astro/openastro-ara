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
                        // Skip a symlinked log for the same reason backups skip links — don't
                        // follow it out of the profile root. A real log file is a plain file.
                        if (IsReparsePoint(logPath)) {
                            continue;
                        }
                        archive.CreateEntryFromFile(logPath, LogsDirName + "/" + Path.GetFileName(logPath), CompressionLevel.Optimal);
                    }
                }

                var profilePath = Path.Combine(_profileDir, ProfileFileName);
                if (File.Exists(profilePath) && !IsReparsePoint(profilePath)) {
                    archive.CreateEntryFromFile(profilePath, ProfileFileName, CompressionLevel.Optimal);
                }
            }

            ct.ThrowIfCancellationRequested();
            var sizeBytes = new FileInfo(tempPath).Length;
            // Reveal the finished bundle atomically — a reader (OpenDownloadAsync) resolves
            // by the id-suffixed name, so it only ever sees a complete archive.
            File.Move(tempPath, zipPath, overwrite: false);
            return sizeBytes;
        } catch {
            TryDelete(tempPath);
            TryDelete(zipPath);
            throw;
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
            ["profile_dir"] = _profileDir,
        };
        return obj.ToJsonString();
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content) {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    public async Task<(Stream Stream, string FileName)?> OpenDownloadAsync(Guid preparationId, CancellationToken ct) {
        var path = await Task.Run(() => FindBundlePath(preparationId), ct).ConfigureAwait(false);
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
    private string? FindBundlePath(Guid id) {
        if (!Directory.Exists(_bugReportsDir)) {
            return null;
        }
        var suffix = "-" + id.ToString("N", CultureInfo.InvariantCulture) + ZipExtension;
        foreach (var path in Directory.EnumerateFiles(_bugReportsDir, ZipPrefix + "*" + ZipExtension, SearchOption.TopDirectoryOnly)) {
            if (Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal)) {
                return path;
            }
        }
        return null;
    }

    private static Uri DownloadUrl(Guid id) =>
        new("/api/v1/bugreport/download?preparationId=" + id.ToString("D", CultureInfo.InvariantCulture), UriKind.Relative);

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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
}
