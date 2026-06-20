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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §29.9 / §29.9.2 real <see cref="ILogService"/> over the daemon's rolling
/// Compact-JSON (CLEF) log files under <c>{profileDir}/logs/</c>, written by the
/// Serilog file sink wired in <c>Program.cs</c>. The files use Serilog's
/// <c>RenderedCompactJsonFormatter</c>, so each line is a JSON object with the
/// final message in <c>@m</c>; this service reads those lines back with
/// <see cref="System.Text.Json"/> (no reader dependency).
///
/// <para><see cref="TailAsync"/> streams the newest log file, parsing + filtering
/// line-by-line and keeping only the newest N matches (bounded memory).
/// <see cref="OpenDownloadAsync"/> streams a whole log file for "Download daemon
/// log" — the requested file is constrained to a bare <c>*.log</c> name inside
/// the logs dir (no path traversal). <see cref="RotateAsync"/> is best-effort:
/// the sink rolls automatically by day + size, so this records a marker and
/// returns 202 (Serilog's File sink exposes no force-roll).</para>
/// </summary>
public sealed partial class LogService : ILogService {
    // Matches the sink path in Program.cs: "openastroara-.log" → Serilog inserts
    // the date, e.g. "openastroara-20260620.log".
    private const string LogGlob = "openastroara-*.log";
    private const int DefaultMaxLines = 200;

    private readonly string _logsDir;
    private readonly ILogger<LogService> _logger;

    public LogService(string logsDir, ILogger<LogService> logger) {
        ArgumentException.ThrowIfNullOrEmpty(logsDir);
        _logsDir = logsDir;
        _logger = logger;
    }

    public Task<OperationAcceptedDto> RotateAsync(string? idempotencyKey, CancellationToken ct) {
        // The Serilog File sink rolls automatically (daily + on size limit) and
        // exposes no force-roll, so "rotate" records a marker into the live log
        // and acknowledges. The marker makes the request auditable in the very
        // file it concerns; a real same-instant roll isn't available.
        LogRotationRequested();
        return Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "log.rotate",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey));
    }

    public Task<(Stream Stream, string FileName)?> OpenDownloadAsync(string? logFileName, CancellationToken ct) {
        var path = ResolveLogFile(logFileName);
        if (path is null) {
            return Task.FromResult<(Stream Stream, string FileName)?>(null);
        }
        // FileShare.ReadWrite so the download coexists with the sink still
        // appending. The stream is owned by the response pipeline (disposed when
        // the response finishes).
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Task.FromResult<(Stream Stream, string FileName)?>((stream, Path.GetFileName(path)));
    }

    public async Task<IReadOnlyList<LogEntryDto>> TailAsync(LogTailRequestDto request, CancellationToken ct) {
        var max = request.MaxLines is int n && n > 0 ? n : DefaultMaxLines;
        var minRank = string.IsNullOrEmpty(request.MinLevel) ? int.MinValue : LevelRank(request.MinLevel);
        var contains = request.ContainsSubstring;

        // Tail across the roll boundary: walk the rolling files newest → oldest,
        // pulling each file's newest matching entries until the window is full. A
        // just-rolled (near-empty) newest file then falls back to the prior file
        // instead of returning almost nothing. Memory stays bounded to `max`: each
        // file is scanned through a ring sized to the entries still needed.
        var result = new List<LogEntryDto>(Math.Min(max, 1024));
        foreach (var path in LogFilesNewestFirst()) {
            if (result.Count >= max) {
                break;
            }
            var remaining = max - result.Count;
            var window = new Queue<LogEntryDto>(Math.Min(remaining, 1024));

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs)) {
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null) {
                    if (line.Length == 0) {
                        continue;
                    }
                    var entry = TryParse(line);
                    if (entry is null) {
                        continue; // a torn final line or a non-CLEF row — skip, never throw
                    }
                    if (LevelRank(entry.Level) < minRank) {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(contains) &&
                        !entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    window.Enqueue(entry);
                    if (window.Count > remaining) {
                        window.Dequeue();
                    }
                }
            }

            // `window` holds this file's newest `remaining` matches oldest-first;
            // reverse to newest-first and append after the already-collected newer
            // entries, keeping `result` globally newest-first.
            foreach (var entry in window.Reverse()) {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve a download/tail target to an absolute path inside the logs dir, or
    /// null. A caller-supplied name must be a bare <c>*.log</c> file name (no
    /// directory component, no traversal) that exists; null selects the newest
    /// <c>openastroara-*.log</c>.
    /// </summary>
    private string? ResolveLogFile(string? logFileName) {
        if (string.IsNullOrEmpty(logFileName)) {
            return LogFilesNewestFirst().FirstOrDefault();
        }
        if (!Directory.Exists(_logsDir)) {
            return null;
        }
        // Reject any path component (a/b, ../x, absolute) — only a bare file name
        // in the logs dir is addressable.
        if (Path.GetFileName(logFileName) != logFileName ||
            !logFileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        var full = Path.Combine(_logsDir, logFileName);
        // Defence in depth: the resolved path must still sit directly in the logs
        // dir (guards against any normalization surprise the basename check missed).
        if (Path.GetDirectoryName(Path.GetFullPath(full)) !=
            Path.GetFullPath(_logsDir).TrimEnd(Path.DirectorySeparatorChar)) {
            return null;
        }
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// The rolling log files, newest first. Serilog names them
    /// <c>openastroara-&lt;yyyyMMdd&gt;[_NNN].log</c> (the <c>_NNN</c> suffix on a
    /// same-day size roll), so an ordinal-descending filename sort is chronological
    /// (date, then roll sequence) — and deterministic, unlike last-write time.
    /// </summary>
    private IEnumerable<string> LogFilesNewestFirst() {
        if (!Directory.Exists(_logsDir)) {
            return Array.Empty<string>();
        }
        return Directory.EnumerateFiles(_logsDir, LogGlob, SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Parse one CLEF line written by Serilog's <c>RenderedCompactJsonFormatter</c>
    /// into a <see cref="LogEntryDto"/>, or null if the line isn't usable JSON.
    /// CLEF fields: <c>@t</c> timestamp, <c>@l</c> level (absent ⇒ Information),
    /// <c>@m</c> rendered message, <c>@x</c> exception, <c>SourceContext</c> source.
    /// </summary>
    private static LogEntryDto? TryParse(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return null;
            }

            var ts = root.TryGetProperty("@t", out var t) && t.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : default;

            var level = root.TryGetProperty("@l", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()!
                : "Information"; // CLEF omits @l for the Information level.

            var message = root.TryGetProperty("@m", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : string.Empty;
            if (root.TryGetProperty("@x", out var x) && x.ValueKind == JsonValueKind.String) {
                // Surface the exception inline so a stack trace isn't lost in tail.
                message = message.Length == 0 ? x.GetString()! : message + "\n" + x.GetString();
            }

            var source = root.TryGetProperty("SourceContext", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString()!
                : "OpenAstroAra.Server";

            return new LogEntryDto(ts, level, source, message, Properties: null);
        } catch (JsonException) {
            return null;
        }
    }

    // Serilog levels in increasing severity, each distinct so a MinLevel filter of
    // "Debug" still excludes "Verbose". Unknown ⇒ Information so a typo'd filter
    // doesn't silently drop everything.
    private static int LevelRank(string level) => level switch {
        "Verbose" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        "Fatal" => 5,
        _ => 2,
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Log rotation requested via /api/v1/server/logs/rotate.")]
    private partial void LogRotationRequested();
}
