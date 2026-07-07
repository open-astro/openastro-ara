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
// PORT_PLAYBOOK.md §10.9 + §60.4 (server state snapshot) + §34.7 (lifecycle)
//
// /api/v1/server/state returns enough to hydrate a fresh WILMA client from
// cold without making N additional REST calls. The shape is intentionally
// chunky: it returns the union of all current top-level state in one round
// trip, then the WS resume protocol takes over for live updates.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Full server state snapshot per §60.4. Returned by /api/v1/server/state.</summary>
public sealed record ServerStateDto(
    string ServerUuid,
    string Nickname,
    string Version,
    string ApiVersion,
    string Tier,
    DateTimeOffset CurrentUtc,
    string? CurrentProfileId,
    PendingRestartDto? PendingRestart,
    string WsResumeToken,
    long WsEventCursor,
    System.Text.Json.JsonElement EquipmentStates,
    System.Text.Json.JsonElement ActiveSequenceRun,
    System.Text.Json.JsonElement DiagnosticsHealth,
    System.Text.Json.JsonElement NotificationsSummary);

/// <summary>Pending restart envelope (§34.7 / §30.8).</summary>
public sealed record PendingRestartDto(
    string Reason,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? PlannedAtUtc,
    bool RestartOnIdle);

/// <summary>GET /api/v1/server/versions per §33.2.1.</summary>
public sealed record ApiVersionsDto(
    string DaemonVersion,
    string DaemonGitSha,
    string DotnetVersion,
    string OsRelease,
    string OsArch,
    string AlpacaSdkVersion,
    string Phd2ProtocolVersion,
    IReadOnlyList<ApiSurfaceVersionDto> ApiSurfaces);

public sealed record ApiSurfaceVersionDto(
    string Name,
    string Version,
    bool IsDeprecated,
    DateTimeOffset? SunsetUtc);

/// <summary>GET /api/v1/server/info — lightweight identity (already exists at Phase 4).</summary>
public sealed record ServerInfoDto(
    string ServerUuid,
    string Nickname,
    string Version,
    string Api,
    string MdnsService,
    string Tier);

/// <summary>GET /api/v1/server/release-notes (§54).</summary>
public sealed record ReleaseNotesDto(
    string Version,
    DateTimeOffset ReleasedUtc,
    string MarkdownBody,
    IReadOnlyList<string> BreakingChanges,
    Uri? UpgradeGuideUrl);

// ─── Log endpoints (§29.9, §10.9 row 3) ─────────────────────────────────────

/// <summary>POST /api/v1/server/logs/tail body.</summary>
public sealed record LogTailRequestDto(
    int? MaxLines,
    string? MinLevel,
    string? ContainsSubstring);

/// <summary>One structured log entry returned by tail.</summary>
public sealed record LogEntryDto(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    System.Text.Json.JsonElement? Properties);
/// <summary>
/// §35.3 — what the emergency stop actually did, rung by rung. Booleans are
/// honest: false means the rung failed or the device wasn't available, not
/// "skipped". <see cref="AlreadyInProgress"/> reports a second trigger that
/// arrived while a stop was mid-flight (ignored — the first pass covers it).
/// </summary>
public sealed record EmergencyStopResultDto(
    bool AlreadyInProgress,
    int RunsAborted,
    bool ExposureAborted,
    bool GuidingStopped,
    bool ParkRequested,
    bool FlatPanelLightOff);
