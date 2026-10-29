#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OpenAstroAra.Server.Services;

/// <summary>Reads the systemd journal: runs <c>journalctl</c> with <paramref name="args"/>, writing each line to
/// <paramref name="output"/>, and returns its exit code (or null when there is no journal to read).</summary>
public delegate int? BugReportJournalReader(IReadOnlyList<string> args, Action<string> output, CancellationToken ct);

/// <summary>
/// Where the rest of the rig's logs live, beside Ara's own: the bundle that answers "what happened on the
/// rig last night" needs the device bridge, the guider and the system's view too (the night forensics of
/// 2026-10-16 and -18 were pulled by hand over SSH). Every path is optional; a source that is missing or
/// unreadable is listed in the bundle's manifest with why, never a failed bundle.
/// </summary>
/// <param name="AlpacaBridgeLogDir">AlpacaBridge's daily logs (<c>alpacabridge-YYYY-MM-DD.log</c>).</param>
/// <param name="GuiderLogDir">openastro-guider's PHD2 debug and guide logs.</param>
/// <param name="SystemFiles">Small system logs taken whole (package install history).</param>
/// <param name="Journal">The systemd journal, or null where there is none.</param>
/// <param name="JournalUnits">The units whose journal is exported, one file each.</param>
/// <param name="KernelRing">The kernel ring buffer (<c>dmesg</c>): USB drops and power events since boot, which a
/// journald that keeps no kernel messages (the CM4's) would otherwise lose. Its args are ignored.</param>
public sealed record BugReportSources(
    string? AlpacaBridgeLogDir,
    string? GuiderLogDir,
    IReadOnlyList<string> SystemFiles,
    BugReportJournalReader? Journal,
    IReadOnlyList<string> JournalUnits,
    BugReportJournalReader? KernelRing = null) {

    /// <summary>No sources beyond Ara's own (desktop hosts, tests).</summary>
    public static readonly BugReportSources None = new(null, null, [], null, []);

    /// <summary>The Debian rig layout the .deb installs.</summary>
    public static readonly BugReportSources Linux = new(
        AlpacaBridgeLogDir: "/var/log/AlpacaBridge",
        GuiderLogDir: "/var/lib/openastro-guider/PHD2",
        // apt's term.log is root:adm; history.log already records every install and removal.
        SystemFiles: ["/var/log/apt/history.log", "/var/log/dpkg.log"],
        Journal: (args, output, ct) => RunTool("journalctl", args, output, ct),
        // power-watch logs the Pi's under-voltage and throttling changes where a rig installs it (the CM4
        // does): the first thing to read after a brown-out night. Absent elsewhere: "-- No entries --".
        JournalUnits: ["openastroara-server", "alpacabridge", "openastro-guider", "power-watch"],
        KernelRing: (_, output, ct) => RunTool("dmesg", ["--time-format", "iso"], output, ct));

    public static BugReportSources ForHost() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Linux : None;

    // A unit's journal over a night is a few MB; the bound only stops a wedged journalctl holding the bundle.
    private static readonly TimeSpan JournalTimeout = TimeSpan.FromSeconds(90);

    // Runs a read-only system tool, streaming its stdout (then stderr, which carries journalctl's permission
    // hint) line by line; null when the tool is not on this host.
    private static int? RunTool(string tool, IReadOnlyList<string> args, Action<string> output, CancellationToken ct) {
        var start = new ProcessStartInfo(tool) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) {
            start.ArgumentList.Add(arg);
        }
        Process? process;
        try {
            process = Process.Start(start);
        } catch (System.ComponentModel.Win32Exception) {
            return null; // no journalctl on this host
        }
        if (process is null) {
            return null;
        }
        using (process) {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(JournalTimeout);
            using var kill = deadline.Token.Register(() => {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            // stderr carries journalctl's permission hint; it goes into the same file so the reader sees it.
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            while (process.StandardOutput.ReadLine() is { } line) {
                output(line);
            }
            process.WaitForExit();
            foreach (var line in errors.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                output(line);
            }
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
    }
}

/// <summary>One line of the bundle's <c>manifest.json</c>: what a source contributed, or why it didn't.</summary>
public sealed record BugReportManifestEntry(string Source, string Path, string? Entry, string Status, long Bytes, long OmittedBytes, string? Note);

public sealed partial class BugReportService {
    // A PHD2 debug log reaches 245 MB in a night; the newest part is what explains the night's end, and the
    // bundle has to come off a Pi over Wi-Fi. Text compresses ~10x, so a capped file is ~13 MB in the ZIP.
    internal const long MaxFileBytes = 128L * 1024 * 1024;
    // The whole bundle's raw-text budget across every source (~100 MB zipped).
    internal const long MaxTotalRawBytes = 1024L * 1024 * 1024;

    // Test seams for the caps (a 128 MB fixture is no test).
    internal long FileCap { get; init; } = MaxFileBytes;
    internal long TotalCap { get; init; } = MaxTotalRawBytes;

    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Every source of the bundle except the profile and system info. With <paramref name="since"/> null the
    /// bundle is the original one (Ara's newest <see cref="MaxBundledLogs"/> logs); with a range it is the
    /// whole rig: Ara's logs and NINA-layer logs, AlpacaBridge, the guider, package history and the journal,
    /// each file written in the range, each capped to its newest <see cref="MaxFileBytes"/>.
    /// </summary>
    private void AddLogSources(ZipArchive archive, DateTimeOffset? since, BugReportSharingMode mode, List<BugReportManifestEntry> manifest,
            Action<long> progress, CancellationToken ct) {
        var budget = new long[] { TotalCap };
        var logsDir = Path.Combine(_profileDir, LogsDirName);
        AddFolder(archive, "ara", logsDir, LogGlob, LogsDirName, since, since is null ? MaxBundledLogs : null, mode, manifest, budget, progress, ct);
        if (since is not DateTimeOffset from) {
            return;
        }
        AddFolder(archive, "ara-nina", Path.Combine(_profileDir, "NINA", "Logs"), "*.log", "ara-nina-logs", from, null, mode, manifest, budget, progress, ct);
        AddFolder(archive, "alpacabridge", _sources.AlpacaBridgeLogDir, "alpacabridge-*.log", "alpacabridge", from, null, mode, manifest, budget, progress, ct);
        AddFolder(archive, "guider", _sources.GuiderLogDir, "PHD2_*Log_*.txt", "guider", from, null, mode, manifest, budget, progress, ct);
        foreach (var file in _sources.SystemFiles) {
            ct.ThrowIfCancellationRequested();
            AddFile(archive, "system", file, "system/" + System.IO.Path.GetFileName(file), mode, manifest, budget, progress);
        }
        AddJournal(archive, from, mode, manifest, progress, ct);
        AddKernelRing(archive, mode, manifest, progress, ct);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Whatever dmesg or its runner throws becomes a manifest line; the rest of the bundle stands.")]
    // The kernel ring buffer since boot, whatever the range: it is small, and the boot is what it covers.
    private void AddKernelRing(ZipArchive archive, BugReportSharingMode mode, List<BugReportManifestEntry> manifest, Action<long> progress, CancellationToken ct) {
        if (_sources.KernelRing is not { } ring) {
            return;
        }
        const string entryName = "system/dmesg.txt";
        long bytes = 0;
        int? exit;
        string? failure = null;
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using (var entryStream = entry.Open())
        using (var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))) {
            try {
                exit = ring([], line => {
                    bytes += line.Length + 1;
                    writer.WriteLine(BugReportRedactor.RedactText(line, mode));
                }, ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                exit = -1;
                failure = ex.Message;
            }
        }
        progress(bytes);
        manifest.Add(new("system", "dmesg --time-format iso", entryName,
            exit is null ? "missing" : exit != 0 ? "partial" : "included", bytes, 0,
            failure is not null ? "dmesg could not be read: " + failure : exit is null ? "no dmesg on this host" : exit != 0 ? $"dmesg exited {exit}" : null));
    }

    private void AddFolder(ZipArchive archive, string source, string? dir, string glob, string entryDir, DateTimeOffset? since, int? newest,
            BugReportSharingMode mode, List<BugReportManifestEntry> manifest, long[] budget, Action<long> progress, CancellationToken ct) {
        if (string.IsNullOrEmpty(dir)) {
            return;
        }
        List<string> files;
        try {
            if (!Directory.Exists(dir)) {
                manifest.Add(new(source, dir, null, "missing", 0, 0, "the folder does not exist on this host"));
                return;
            }
            // Newest first, so a budget that runs out drops the oldest days. Without a range the original
            // order stands: ordinal-descending names, chronological for openastroara-<date>[_NNN].log.
            var all = Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly);
            IEnumerable<string> found = newest is null
                ? all.OrderByDescending(File.GetLastWriteTimeUtc)
                : all.OrderByDescending(System.IO.Path.GetFileName, StringComparer.Ordinal);
            if (since is DateTimeOffset from) {
                found = found.Where(f => File.GetLastWriteTimeUtc(f) >= from.UtcDateTime);
            }
            if (newest is int n) {
                found = found.Take(n);
            }
            files = [.. found];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            manifest.Add(new(source, dir, null, "unreadable", 0, 0, ex.Message));
            return;
        }
        if (files.Count == 0) {
            manifest.Add(new(source, dir, null, "empty", 0, 0, since is null ? "no files" : "no file written in the range"));
        }
        foreach (var file in files) {
            ct.ThrowIfCancellationRequested();
            AddFile(archive, source, file, entryDir + "/" + System.IO.Path.GetFileName(file), mode, manifest, budget, progress);
        }
    }

    // One text file, scrubbed line by line for the mode; its newest MaxFileBytes when larger, and nothing once
    // the bundle's budget is spent. Never throws for the file itself: a vanished or unreadable file is a
    // manifest line.
    private void AddFile(ZipArchive archive, string source, string path, string entryName, BugReportSharingMode mode,
            List<BugReportManifestEntry> manifest, long[] budget, Action<long> progress) {
        try {
            if (!File.Exists(path)) {
                manifest.Add(new(source, path, null, "missing", 0, 0, null));
                return;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) {
                LogSymlinkSkipped(path);
                manifest.Add(new(source, path, null, "skipped", 0, 0, "a symbolic link (not followed)"));
                return;
            }
            if (budget[0] <= 0) {
                manifest.Add(new(source, path, null, "over_budget", 0, 0, $"the bundle reached its {TotalCap / (1024 * 1024)} MB limit"));
                return;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var keep = Math.Min(Math.Min(stream.Length, FileCap), budget[0]);
            var omitted = stream.Length - keep;
            if (omitted > 0) {
                stream.Seek(omitted, SeekOrigin.Begin);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            if (omitted > 0) {
                reader.ReadLine(); // the cut lands mid-line: drop the partial one
            }
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (omitted > 0) {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[Ara bug report: the first {omitted:N0} bytes of this file are left out; this is its newest {keep:N0} bytes.]"));
            }
            while (reader.ReadLine() is { } line) {
                writer.WriteLine(BugReportRedactor.RedactText(line, mode));
            }
            budget[0] -= keep;
            progress(keep);
            manifest.Add(new(source, path, entryName, omitted > 0 ? "truncated" : "included", keep, omitted, null));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogEntrySkipped(path, ex);
            manifest.Add(new(source, path, null, "unreadable", 0, 0, ex.Message));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Journal boundary: whatever journalctl or its runner throws becomes a manifest line; the rest of the bundle stands.")]
    // The journal since the range start, one file per unit plus the kernel and the boot list (the CM4's journal
    // is volatile: it holds only the current boot, which is what the boot list tells a reader).
    private void AddJournal(ZipArchive archive, DateTimeOffset since, BugReportSharingMode mode, List<BugReportManifestEntry> manifest,
            Action<long> progress, CancellationToken ct) {
        if (_sources.Journal is not { } journal) {
            return;
        }
        var sinceArg = "@" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string[] common = ["--no-pager", "-o", "short-iso", "--since", sinceArg];
        var exports = new List<(string Name, string[] Args)> { ("boots.txt", ["--no-pager", "--list-boots"]), ("kernel.log", ["-k", .. common]) };
        foreach (var unit in _sources.JournalUnits) {
            exports.Add((unit + ".log", ["-u", unit, .. common]));
        }
        foreach (var (name, args) in exports) {
            ct.ThrowIfCancellationRequested();
            var entryName = "journal/" + name;
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            long bytes = 0;
            var denied = false;
            int? exit;
            string? failure = null;
            using (var entryStream = entry.Open())
            using (var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))) {
                try {
                    exit = journal(args, line => {
                        if (line.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("not seeing messages from other users", StringComparison.OrdinalIgnoreCase)) {
                            denied = true;
                        }
                        bytes += line.Length + 1;
                        writer.WriteLine(BugReportRedactor.RedactText(line, mode));
                    }, ct);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // A journal that cannot be read is a manifest line, never a failed bundle.
                    exit = -1;
                    failure = ex.Message;
                }
            }
            progress(bytes);
            var note = failure is not null ? "journalctl could not be read: " + failure
                : denied ? "journalctl reported limited permissions: the daemon needs the systemd-journal group (the package grants it)"
                : exit is null ? "no journal on this host" : exit != 0 ? $"journalctl exited {exit}" : null;
            manifest.Add(new("journal", "journalctl " + string.Join(' ', args), entryName,
                exit is null ? "missing" : denied || exit != 0 ? "partial" : "included", bytes, 0, note));
        }
    }

    private static string ManifestJson(DateTimeOffset? since, List<BugReportManifestEntry> manifest) {
        var entries = new JsonArray();
        foreach (var m in manifest) {
            entries.Add(new JsonObject {
                ["source"] = m.Source,
                ["path"] = m.Path,
                ["entry"] = m.Entry,
                ["status"] = m.Status,
                ["bytes"] = m.Bytes,
                ["omitted_bytes"] = m.OmittedBytes,
                ["note"] = m.Note,
            });
        }
        return new JsonObject {
            ["since_utc"] = since?.ToString("O", CultureInfo.InvariantCulture),
            ["max_file_bytes"] = MaxFileBytes,
            ["max_total_bytes"] = MaxTotalRawBytes,
            ["entries"] = entries,
        }.ToJsonString(IndentedJson);
    }

    // What the disclosure lists: per source, how many files and how many bytes went in.
    internal static IReadOnlyList<Contracts.BugReportSourceSummaryDto> Summarize(IEnumerable<BugReportManifestEntry> manifest) =>
        [.. manifest.GroupBy(m => m.Source)
            .Select(g => new Contracts.BugReportSourceSummaryDto(
                Source: g.Key,
                Files: g.Count(m => m.Entry is not null),
                Bytes: g.Sum(m => m.Bytes),
                Truncated: g.Any(m => m.Status == "truncated"),
                Note: g.Select(m => m.Note).FirstOrDefault(n => n is not null)))];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bug-report bundle {ReportId} failed to build")]
    partial void LogPrepareFailed(Exception ex, Guid reportId);
}
