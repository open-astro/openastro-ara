#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.8 — placeholder <see cref="ILogService"/> for the §29.9
/// log-tail + rotate + download surface. Tail returns a small fixed
/// transcript so WILMA's §54 "View daemon log" panel can develop
/// against a real wire shape; rotate accepts the request and returns
/// an immediate <see cref="OperationAcceptedDto"/>; download returns
/// null (no real log file yet — Phase 14 wires Serilog file sinks
/// per §29.9.2 and downloads the actual rolling log).
/// </summary>
public sealed class PlaceholderLogService : ILogService {
    private static readonly DateTimeOffset BaseTime =
        new(2026, 5, 30, 3, 0, 0, TimeSpan.Zero);

    private static readonly LogEntryDto[] SampleEntries = new[] {
        new LogEntryDto(BaseTime,                  "Information", "OpenAstroAra.Server",  "OpenAstroAra.Server listening on :5555",                                     Properties: null),
        new LogEntryDto(BaseTime.AddSeconds(2),    "Information", "Hosting.Lifetime",     "Application started.",                                                       Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(10),   "Information", "Sequencer",            "Sequence M31 started — 2 targets, 12 frames planned.",                       Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(14),   "Information", "FrameRepository",      "Captured M31_L_001.fits — HFR 1.85″, 412 stars, score 0.87.",                Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(17),   "Information", "FrameRepository",      "Captured M31_R_002.fits — HFR 2.10″, 388 stars, score 0.81.",                Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(20),   "Warning",     "DiagnosticsMonitor",   "altitude.target.declining — M31 will cross 20° limit in 45m.",               Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(45),   "Warning",     "Storage",              "Disk space low — 8.2 GB free (~4h imaging remaining).",                       Properties: null),
        new LogEntryDto(BaseTime.AddMinutes(90),   "Error",       "SafetyMonitor",        "Unsafe weather — cloud sensor crossed threshold; pause-and-park engaged.",   Properties: null),
    };

    public Task<OperationAcceptedDto> RotateAsync(string? idempotencyKey, CancellationToken ct) =>
        // Real impl asks Serilog to roll over the .log file. Placeholder
        // accepts the request and reports synthetic operation id so WILMA's
        // "force-rotate" button has a 202 path to follow.
        Task.FromResult(new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "log.rotate",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey));

    public Task<(Stream Stream, string FileName)?> OpenDownloadAsync(string? logFileName, CancellationToken ct) =>
        // No log file on disk yet — Phase 14 wires Serilog sinks. Endpoint
        // returns 404 (preferable to 501-stub for "feature gated on infra").
        Task.FromResult<(Stream Stream, string FileName)?>(null);

    public Task<IReadOnlyList<LogEntryDto>> TailAsync(LogTailRequestDto request, CancellationToken ct) {
        IEnumerable<LogEntryDto> q = SampleEntries;
        if (!string.IsNullOrEmpty(request.MinLevel)) {
            var minRank = LevelRank(request.MinLevel);
            q = q.Where(e => LevelRank(e.Level) >= minRank);
        }
        if (!string.IsNullOrEmpty(request.ContainsSubstring)) {
            q = q.Where(e => e.Message.Contains(request.ContainsSubstring, StringComparison.OrdinalIgnoreCase));
        }
        // §29.9 wants newest first; the fixture is already in chronological
        // order so reverse before taking.
        var max = request.MaxLines is int n && n > 0 ? n : 200;
        return Task.FromResult<IReadOnlyList<LogEntryDto>>(
            q.Reverse().Take(max).ToList());
    }

    // Serilog log levels in increasing severity order. Unknown levels
    // fall through to Information (rank 1) so a typo in the filter
    // doesn't silently drop all entries.
    private static int LevelRank(string level) => level switch {
        "Verbose" => 0,
        "Debug" => 0,
        "Information" => 1,
        "Warning" => 2,
        "Error" => 3,
        "Fatal" => 4,
        _ => 1,
    };
}
