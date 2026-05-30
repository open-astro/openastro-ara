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
/// Phase 13.5 — placeholder <see cref="IDiagnosticsService"/> so WILMA's
/// §51 Diagnostic Panel has wire shapes to render before the real
/// monitor worker lands.
///
/// State: one open Yellow issue (target-altitude trend) + Yellow overall
/// health. <see cref="DiagnosticsStateDto.Mode"/> reports the
/// <see cref="DiagnosticsMode"/> operating mode of the monitor itself
/// (Off/Observe/Suggest/AutoCorrect per §51); this is conceptually
/// distinct from the §51 *settings* mode (notify_only / pause_on_critical
/// / abort_on_critical) which lives in <c>profile.json</c> via
/// <see cref="IProfileStore"/>. Real implementation reconciles the two
/// in Phase 13.x.
///
/// History: three fixture events (Green/Yellow/Red severities) so the
/// §51.3 history scroll has data to render.
///
/// <see cref="SetModeAsync"/> updates an in-memory operating mode only —
/// resets on daemon restart. The persistent settings-level mode (§51.5
/// reaction-to-critical) round-trips separately via the §37 profile
/// endpoints (Phase 12h.6j).
/// </summary>
public sealed class PlaceholderDiagnosticsService : IDiagnosticsService {
    private readonly object _lock = new();
    private DiagnosticsMode _mode = DiagnosticsMode.Observe;

    private static readonly DateTimeOffset DetectedUtc =
        new(2026, 5, 30, 3, 30, 0, TimeSpan.Zero);

    private static readonly DiagnosticIssueDto SampleIssue = new(
        Id: Guid.Parse("44444444-4444-4444-4444-444444444441"),
        IssueType: "altitude.target.declining",
        Severity: DiagnosticHealth.Yellow,
        Description: "M31 will cross the altitude limit (20°) in about 45 minutes; sequence will skip to next target then.",
        DetectedUtc: DetectedUtc,
        RecommendedAction: "No action required — the §35.4 altitude-limit policy will skip-target automatically.",
        AutoCorrectible: true);

    private static readonly DiagnosticEventDto[] SampleHistory = new[] {
        new DiagnosticEventDto(
            Id: Guid.Parse("55555555-5555-5555-5555-555555555551"),
            EventType: "session.started",
            Severity: DiagnosticHealth.Green,
            Description: "Sequence M31 started.",
            DetectedUtc: DetectedUtc.AddMinutes(-15),
            ClearedUtc: DetectedUtc.AddMinutes(-15),
            AutoActionTaken: false,
            AutoActionDescription: null),
        new DiagnosticEventDto(
            Id: Guid.Parse("55555555-5555-5555-5555-555555555552"),
            EventType: "altitude.target.declining",
            Severity: DiagnosticHealth.Yellow,
            Description: "M31 altitude trending below 30° — limit check armed.",
            DetectedUtc: DetectedUtc,
            ClearedUtc: null,
            AutoActionTaken: false,
            AutoActionDescription: null),
        new DiagnosticEventDto(
            Id: Guid.Parse("55555555-5555-5555-5555-555555555553"),
            EventType: "guider.lost",
            Severity: DiagnosticHealth.Red,
            Description: "PHD2 lost the guide star; pause-and-retry policy engaged.",
            DetectedUtc: DetectedUtc.AddMinutes(20),
            ClearedUtc: DetectedUtc.AddMinutes(22),
            AutoActionTaken: true,
            AutoActionDescription: "Re-acquired guide star after 2 settle cycles per §35.6."),
    };

    public Task<DiagnosticsStateDto> GetStateAsync(CancellationToken ct) {
        DiagnosticsMode mode;
        lock (_lock) { mode = _mode; }
        return Task.FromResult(new DiagnosticsStateDto(
            Health: DiagnosticHealth.Yellow,   // one open issue → not Green
            Mode: mode,
            OpenIssueCount: 1,
            LastHourIssueCount: 3,
            LastEvaluationUtc: DetectedUtc.AddMinutes(45),
            OpenIssues: new[] { SampleIssue }));
    }

    public Task<DiagnosticsStateDto> SetModeAsync(DiagnosticsModeRequestDto request, CancellationToken ct) {
        lock (_lock) { _mode = request.Mode; }
        return GetStateAsync(ct);
    }

    public Task<CursorPage<DiagnosticEventDto>> GetHistoryAsync(int limit, string? cursor, CancellationToken ct) {
        var items = SampleHistory
            .OrderByDescending(e => e.DetectedUtc)
            .Take(Math.Max(1, limit))
            .ToList();
        return Task.FromResult(new CursorPage<DiagnosticEventDto>(items, NextCursor: null, HasMore: false));
    }
}
