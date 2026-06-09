#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// PORT_PLAYBOOK.md §10.8 + §51 (diagnostics monitor)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Aggregate health colour per §51.2.</summary>
public enum DiagnosticHealth {
    Green,
    Yellow,
    Red,
    Unknown
}

/// <summary>Diagnostics monitor operating mode (per §51.5).</summary>
public enum DiagnosticsMode {
    Off,
    Observe,
    Suggest,
    AutoCorrect
}

/// <summary>GET /api/v1/diagnostics/state — current snapshot.</summary>
public sealed record DiagnosticsStateDto(
    DiagnosticHealth Health,
    DiagnosticsMode Mode,
    int OpenIssueCount,
    int LastHourIssueCount,
    DateTimeOffset LastEvaluationUtc,
    IReadOnlyList<DiagnosticIssueDto> OpenIssues);

/// <summary>One open diagnostic issue.</summary>
public sealed record DiagnosticIssueDto(
    Guid Id,
    string IssueType,
    DiagnosticHealth Severity,
    string Description,
    DateTimeOffset DetectedUtc,
    string? RecommendedAction,
    bool AutoCorrectible);

/// <summary>POST /api/v1/diagnostics/mode body.</summary>
public sealed record DiagnosticsModeRequestDto(
    DiagnosticsMode Mode,
    string? Reason);

/// <summary>Historical diagnostic event (closed or active). Per §51.</summary>
public sealed record DiagnosticEventDto(
    Guid Id,
    string EventType,
    DiagnosticHealth Severity,
    string Description,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? ClearedUtc,
    bool AutoActionTaken,
    string? AutoActionDescription);