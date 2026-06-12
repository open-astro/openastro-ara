#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §51 SQLite-backed <see cref="IDiagnosticsService"/>. Events + open
/// issues live in one <c>diagnostic_events</c> table — issues are
/// just events with <c>cleared_utc IS NULL</c>. Operating mode lives
/// in <c>app_config</c> under <c>diagnostics_mode</c>; survives daemon
/// restart whereas the placeholder reset to Observe on every launch.
///
/// The §51 monitor *worker* that actually produces events (target
/// altitude, guider health, equipment failures, etc.) lands when the
/// §38 sequence orchestrator is online. This service is the storage
/// + query API; the writer-side wires in alongside the orchestrator.
/// </summary>
public sealed partial class SqliteDiagnosticsService : IDiagnosticsService {
    private const string ModeKey = "diagnostics_mode";

    private static readonly Guid SampleIssueId =
        Guid.Parse("44444444-4444-4444-4444-444444444441");
    private static readonly Guid[] SampleHistoryIds = new[] {
        Guid.Parse("55555555-5555-5555-5555-555555555551"),
        Guid.Parse("55555555-5555-5555-5555-555555555552"),
        Guid.Parse("55555555-5555-5555-5555-555555555553"),
    };

    private readonly IAraDatabase _db;
    private readonly ILogger<SqliteDiagnosticsService> _logger;
    // §60.9 WS sink for diagnostics.* events. Optional so the service composes in tests without a hub.
    private readonly IWsBroadcaster? _ws;

    public SqliteDiagnosticsService(IAraDatabase db, ILogger<SqliteDiagnosticsService>? logger, IWsBroadcaster? ws = null) {
        _db = db;
        _logger = logger ?? NullLogger<SqliteDiagnosticsService>.Instance;
        _ws = ws;
    }

    /// <summary>
    /// Seed fixture diagnostic events on first init. Idempotent — skipped
    /// if the table is already populated.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM diagnostic_events;";
        var count = (long)(await check.ExecuteScalarAsync(ct) ?? 0L);
        if (count > 0) return;

        var detected = new DateTimeOffset(2026, 5, 30, 3, 30, 0, TimeSpan.Zero);

        // sessions.started — green, cleared (historical)
        await InsertEventAsync(conn,
            id: SampleHistoryIds[0],
            eventType: "session.started",
            severity: DiagnosticHealth.Green,
            description: "Sequence M31 started.",
            detectedUtc: detected.AddMinutes(-15),
            clearedUtc: detected.AddMinutes(-15),
            autoActionTaken: false,
            autoActionDescription: null,
            recommendedAction: null,
            autoCorrectible: null,
            ct: ct);

        // altitude.target.declining — yellow, OPEN (the one §51 issue showing)
        await InsertEventAsync(conn,
            id: SampleIssueId,
            eventType: "altitude.target.declining",
            severity: DiagnosticHealth.Yellow,
            description: "M31 will cross the altitude limit (20°) in about 45 minutes; sequence will skip to next target then.",
            detectedUtc: detected,
            clearedUtc: null,
            autoActionTaken: false,
            autoActionDescription: null,
            recommendedAction: "No action required — the §35.4 altitude-limit policy will skip-target automatically.",
            autoCorrectible: true,
            ct: ct);

        // guider.lost — red, cleared (historical)
        await InsertEventAsync(conn,
            id: SampleHistoryIds[2],
            eventType: "guider.lost",
            severity: DiagnosticHealth.Red,
            description: "PHD2 lost the guide star; pause-and-retry policy engaged.",
            detectedUtc: detected.AddMinutes(20),
            clearedUtc: detected.AddMinutes(22),
            autoActionTaken: true,
            autoActionDescription: "Re-acquired guide star after 2 settle cycles per §35.6.",
            recommendedAction: null,
            autoCorrectible: null,
            ct: ct);

        LogSeededEvents();
    }

    public async Task<DiagnosticsStateDto> GetStateAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        var mode = await ReadModeAsync(conn, ct);

        // Open issues + counts in a single round-trip.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, severity, description, detected_utc,
                   recommended_action, auto_correctible
            FROM diagnostic_events
            WHERE cleared_utc IS NULL
            ORDER BY detected_utc DESC;
            """;
        var openIssues = new List<DiagnosticIssueDto>();
        var maxSeverity = DiagnosticHealth.Green;
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct)) {
                var severity = Enum.Parse<DiagnosticHealth>(reader.GetString(2), ignoreCase: true);
                if (severity > maxSeverity) maxSeverity = severity;
                openIssues.Add(new DiagnosticIssueDto(
                    Id: Guid.Parse(reader.GetString(0)),
                    IssueType: reader.GetString(1),
                    Severity: severity,
                    Description: reader.GetString(3),
                    DetectedUtc: DateTimeOffset.Parse(reader.GetString(4)),
                    RecommendedAction: await reader.IsDBNullAsync(5, ct) ? null : reader.GetString(5),
                    AutoCorrectible: !await reader.IsDBNullAsync(6, ct) && reader.GetInt32(6) != 0));
            }
        }

        // Counts: events detected in the last hour (any severity).
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        await using var hourCmd = conn.CreateCommand();
        hourCmd.CommandText = "SELECT COUNT(*) FROM diagnostic_events WHERE detected_utc >= $since;";
        hourCmd.Parameters.AddWithValue("$since", hourAgo);
        var lastHour = Convert.ToInt32((await hourCmd.ExecuteScalarAsync(ct)) ?? 0);

        return new DiagnosticsStateDto(
            Health: maxSeverity,
            Mode: mode,
            OpenIssueCount: openIssues.Count,
            LastHourIssueCount: lastHour,
            LastEvaluationUtc: DateTimeOffset.UtcNow,
            OpenIssues: openIssues);
    }

    public async Task<DiagnosticsStateDto> SetModeAsync(DiagnosticsModeRequestDto request, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_config (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", ModeKey);
        cmd.Parameters.AddWithValue("$value", request.Mode.ToString().ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(ct);
        return await GetStateAsync(ct);
    }

    public async Task<CursorPage<DiagnosticEventDto>> GetHistoryAsync(int limit, string? cursor, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, severity, description, detected_utc,
                   cleared_utc, auto_action_taken, auto_action_description
            FROM diagnostic_events
            ORDER BY detected_utc DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<DiagnosticEventDto>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct) && items.Count < pageSize) {
            items.Add(new DiagnosticEventDto(
                Id: Guid.Parse(reader.GetString(0)),
                EventType: reader.GetString(1),
                Severity: Enum.Parse<DiagnosticHealth>(reader.GetString(2), ignoreCase: true),
                Description: reader.GetString(3),
                DetectedUtc: DateTimeOffset.Parse(reader.GetString(4)),
                ClearedUtc: await reader.IsDBNullAsync(5, ct) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                AutoActionTaken: reader.GetInt32(6) != 0,
                AutoActionDescription: await reader.IsDBNullAsync(7, ct) ? null : reader.GetString(7)));
        }
        var hasMore = await reader.ReadAsync(ct);
        var nextCursor = hasMore ? (offset + pageSize).ToString() : null;
        return new CursorPage<DiagnosticEventDto>(items, NextCursor: nextCursor, HasMore: hasMore);
    }

    private static async Task<DiagnosticsMode> ReadModeAsync(SqliteConnection conn, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_config WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", ModeKey);
        var raw = (string?)(await cmd.ExecuteScalarAsync(ct));
        if (string.IsNullOrEmpty(raw)) return DiagnosticsMode.Observe;
        return Enum.TryParse<DiagnosticsMode>(raw, ignoreCase: true, out var m)
            ? m
            : DiagnosticsMode.Observe;
    }

    public async Task CreateEventAsync(
            DiagnosticEventDto diagnosticEvent,
            string? recommendedAction,
            bool? autoCorrectible,
            CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await InsertEventAsync(
            conn,
            id: diagnosticEvent.Id,
            eventType: diagnosticEvent.EventType,
            severity: diagnosticEvent.Severity,
            description: diagnosticEvent.Description,
            detectedUtc: diagnosticEvent.DetectedUtc,
            clearedUtc: diagnosticEvent.ClearedUtc,
            autoActionTaken: diagnosticEvent.AutoActionTaken,
            autoActionDescription: diagnosticEvent.AutoActionDescription,
            recommendedAction: recommendedAction,
            autoCorrectible: autoCorrectible,
            ct: ct);
        // §60.9: announce the raised diagnostic so clients update live. An event that records an auto-action
        // taken is the auto_action_taken type; otherwise it's a newly-detected issue.
        await EmitDiagnosticsEventAsync(
            diagnosticEvent.AutoActionTaken ? WsEventCatalog.DiagnosticsAutoActionTaken : WsEventCatalog.DiagnosticsIssueDetected,
            new JsonObject {
                ["id"] = diagnosticEvent.Id.ToString(),
                ["event_type"] = diagnosticEvent.EventType,
                ["severity"] = SeverityToken(diagnosticEvent.Severity),
                ["description"] = diagnosticEvent.Description,
                ["detected_utc"] = diagnosticEvent.DetectedUtc.ToString("O"),
                ["auto_action_taken"] = diagnosticEvent.AutoActionTaken,
                ["auto_action_description"] = diagnosticEvent.AutoActionDescription,
                ["recommended_action"] = recommendedAction,
                ["auto_correctible"] = autoCorrectible,
            });
    }

    public async Task<int> ClearOpenEventsByTypeAsync(string eventType, DateTimeOffset clearedUtc, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE diagnostic_events SET cleared_utc = $cleared
            WHERE event_type = $type AND cleared_utc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$cleared", clearedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$type", eventType);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        // Only announce a clear that actually closed open events — a no-op clear shouldn't churn the WS stream.
        if (affected > 0) {
            await EmitDiagnosticsEventAsync(WsEventCatalog.DiagnosticsCleared, new JsonObject {
                ["event_type"] = eventType,
                ["cleared_count"] = affected,
                ["cleared_utc"] = clearedUtc.ToString("O"),
            });
        }
        return affected;
    }

    // DiagnosticHealth → the lowercase wire token (no ToLowerInvariant, per the analyzer gate). An unhandled
    // member throws rather than defaulting to "green" — silently reporting the *healthiest* state for an unknown
    // severity is the wrong direction for a diagnostic, so force the mapping to be updated if the enum grows.
    private static string SeverityToken(DiagnosticHealth severity) => severity switch {
        DiagnosticHealth.Green => "green",
        DiagnosticHealth.Yellow => "yellow",
        DiagnosticHealth.Red => "red",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "unhandled DiagnosticHealth"),
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: a failed publish from a custom IWsBroadcaster (e.g. SocketException) must not abort the diagnostic write or surface as an unobserved exception. CA1031's log-and-recover boundary applies.")]
    private async Task EmitDiagnosticsEventAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            // ToJsonString()+Parse is the AOT-safe way to build a JsonElement from a JsonObject (SerializeToElement
            // takes the reflection path the warnings=errors AOT gate rejects) — mirrors SequencerService.EmitAsync.
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogDiagnosticsWsPublishFailed(eventType, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Diagnostics WS event '{EventType}' failed to publish")]
    partial void LogDiagnosticsWsPublishFailed(string eventType, Exception ex);

    private static async Task InsertEventAsync(
            SqliteConnection conn, Guid id, string eventType,
            DiagnosticHealth severity, string description,
            DateTimeOffset detectedUtc, DateTimeOffset? clearedUtc,
            bool autoActionTaken, string? autoActionDescription,
            string? recommendedAction, bool? autoCorrectible,
            CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO diagnostic_events
                (id, event_type, severity, description, detected_utc,
                 cleared_utc, auto_action_taken, auto_action_description,
                 recommended_action, auto_correctible)
            VALUES
                ($id, $type, $severity, $desc, $detected,
                 $cleared, $aat, $aad, $rec, $autocorr);
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$type", eventType);
        cmd.Parameters.AddWithValue("$severity", severity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$desc", description);
        cmd.Parameters.AddWithValue("$detected", detectedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$cleared", (object?)clearedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aat", autoActionTaken ? 1 : 0);
        cmd.Parameters.AddWithValue("$aad", (object?)autoActionDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rec", (object?)recommendedAction ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$autocorr",
            autoCorrectible.HasValue ? (object)(autoCorrectible.Value ? 1 : 0) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded sample diagnostic events")]
    private partial void LogSeededEvents();
}