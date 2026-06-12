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
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §39 calibration — real <see cref="ICalibrationService"/> over the §28 SQLite frame catalog (replaces the
/// Phase 13.14 fixture placeholder). A "calibration session" is any catalog session that captured LIGHT frames;
/// its per-filter summary, and whether matching flats/darks already exist, are derived live from the catalog.
///
/// Matching rules (standard calibration practice): a flat matches a light by <c>filter</c>; a dark matches a
/// light by <c>(exposure, gain)</c>. Temperature matching for cooled-camera darks is a refinement tracked in
/// PORT_TODO. The dark-library <em>build</em> is a separate, guider/sequencer-gated concern (<see cref="IDarkLibraryService"/>);
/// this service only reports availability + generates the matching-flats plan.
/// </summary>
public sealed class SqliteCalibrationService : ICalibrationService {
    // A conventional default flat count when the caller doesn't override it (NINA ships ~20-30).
    private const int DefaultFlatFrames = 20;

    private readonly IAraDatabase _db;

    public SqliteCalibrationService(IAraDatabase db) {
        _db = db;
    }

    public async Task<CursorPage<CalibrationSessionDto>> ListSessionsAsync(int limit, string? cursor, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();

        // Page over the sessions that have LIGHT frames, newest first. Fetch one extra row to know if there's
        // a next page without a second COUNT query.
        var ids = new List<Guid>();
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                SELECT session_id
                FROM frames
                WHERE frame_type = 'light'
                GROUP BY session_id
                ORDER BY MIN(captured_utc) DESC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$limit", pageSize + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                if (Guid.TryParse(reader.GetString(0), out var id)) {
                    ids.Add(id);
                }
            }
        }

        var hasMore = ids.Count > pageSize;
        if (hasMore) {
            ids.RemoveAt(ids.Count - 1);
        }

        var items = new List<CalibrationSessionDto>(ids.Count);
        foreach (var id in ids) {
            var dto = await BuildSessionDtoAsync(conn, id, ct);
            if (dto != null) {
                items.Add(dto);
            }
        }

        var nextCursor = hasMore ? (offset + pageSize).ToString(CultureInfo.InvariantCulture) : null;
        return new CursorPage<CalibrationSessionDto>(items, nextCursor, hasMore);
    }

    public async Task<CalibrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        return await BuildSessionDtoAsync(conn, id, ct);
    }

    public async Task<GeneratedFlatSequenceDto> GenerateMatchingFlatsAsync(Guid sessionId, MatchingFlatsRequestDto request, CancellationToken ct) {
        await using var conn = _db.OpenConnection();

        var session = await BuildSessionDtoAsync(conn, sessionId, ct)
            ?? throw new CalibrationSessionNotFoundException(sessionId);

        var frameCount = request.OverrideFrameCount is int n && n > 0 ? n : DefaultFlatFrames;
        var steps = new List<GeneratedFlatStepDto>(session.FiltersUsed.Count);
        var total = 0;
        foreach (var filter in session.FiltersUsed) {
            steps.Add(new GeneratedFlatStepDto(
                FilterName: filter.FilterName,
                FrameCount: frameCount,
                TargetAdu: request.OverrideTargetAdu,
                PanelBrightness: null));
            total += frameCount;
        }

        // v0.0.1 returns the PLAN (one step per light filter). Enqueuing it as a runnable flat sequence is a
        // follow-up that needs the §38 sequence service; GenerateOnly is honoured by simply never persisting.
        return new GeneratedFlatSequenceDto(
            SourceSessionId: sessionId,
            GeneratedSequenceId: Guid.NewGuid(),
            GeneratedSequenceName: $"Flats — {session.TargetName}",
            TotalFlatFrames: total,
            Steps: steps);
    }

    private static async Task<CalibrationSessionDto?> BuildSessionDtoAsync(SqliteConnection conn, Guid sessionId, CancellationToken ct) {
        var sid = sessionId.ToString();

        // Session header from its LIGHT frames. Null row (no lights) ⇒ not a calibration session.
        string targetName;
        int lightCount;
        DateTimeOffset start, end;
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                SELECT COUNT(*), MIN(captured_utc), MAX(captured_utc), MIN(target_name)
                FROM frames
                WHERE frame_type = 'light' AND session_id = $sid;
                """;
            cmd.Parameters.AddWithValue("$sid", sid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || await reader.IsDBNullAsync(0, ct) || reader.GetInt32(0) == 0) {
                return null;
            }
            lightCount = reader.GetInt32(0);
            start = ParseUtc(reader.GetString(1));
            end = ParseUtc(reader.GetString(2));
            targetName = await reader.IsDBNullAsync(3, ct) ? "(unknown)" : reader.GetString(3);
        }

        // Per-filter summary.
        var filters = new List<CalibrationFilterSummaryDto>();
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                SELECT filter_name, COUNT(*), AVG(exposure_seconds)
                FROM frames
                WHERE frame_type = 'light' AND session_id = $sid
                GROUP BY filter_name
                ORDER BY filter_name;
                """;
            cmd.Parameters.AddWithValue("$sid", sid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                filters.Add(new CalibrationFilterSummaryDto(
                    FilterName: await reader.IsDBNullAsync(0, ct) ? NoFilter : reader.GetString(0),
                    LightFrameCount: reader.GetInt32(1),
                    MeanExposureSeconds: await reader.IsDBNullAsync(2, ct) ? 0 : reader.GetDouble(2),
                    RecommendedFlatFrames: DefaultFlatFrames));
            }
        }

        // No trailing ';' — these are embedded as subqueries in AllCoveredAsync's SELECT COUNT(*) FROM (...).
        var flatsAvailable = await AllCoveredAsync(conn, sid, """
            SELECT DISTINCT filter_name FROM frames WHERE frame_type = 'light' AND session_id = $sid
            EXCEPT
            SELECT DISTINCT filter_name FROM frames WHERE frame_type = 'flat'
            """, ct);

        var darksAvailable = await AllCoveredAsync(conn, sid, """
            SELECT DISTINCT exposure_seconds, gain FROM frames WHERE frame_type = 'light' AND session_id = $sid
            EXCEPT
            SELECT DISTINCT exposure_seconds, gain FROM frames WHERE frame_type = 'dark'
            """, ct);

        string? profileId = null;
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = "SELECT profile_id FROM sessions WHERE id = $sid LIMIT 1;";
            cmd.Parameters.AddWithValue("$sid", sid);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string s && !string.IsNullOrEmpty(s)) {
                profileId = s;
            }
        }

        return new CalibrationSessionDto(
            Id: sessionId,
            TargetName: targetName,
            SessionStartUtc: start,
            SessionEndUtc: end,
            LightFrameCount: lightCount,
            FiltersUsed: filters,
            MatchingFlatsAvailable: flatsAvailable,
            MatchingDarksAvailable: darksAvailable,
            ProfileId: profileId);
    }

    // The query returns the set of light requirements NOT covered by the calibration frame set; zero rows means
    // every requirement is covered. SQLite's EXCEPT treats NULLs as equal, so a no-filter light matches a
    // no-filter flat.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The 'sql' argument is a compile-time constant literal supplied only by this class's two internal callers; the sole external value (the session id) is a bound $sid parameter. No user input reaches the command text.")]
    private static async Task<bool> AllCoveredAsync(SqliteConnection conn, string sid, string sql, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM ({sql});";
        cmd.Parameters.AddWithValue("$sid", sid);
        var uncovered = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture);
        return uncovered == 0;
    }

    private const string NoFilter = "(no filter)";

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>Thrown by <see cref="SqliteCalibrationService.GenerateMatchingFlatsAsync"/> when the session id has
/// no LIGHT frames in the catalog (so there's nothing to match flats to). The endpoint maps it to a 404.</summary>
public sealed class CalibrationSessionNotFoundException : Exception {
    public CalibrationSessionNotFoundException() { }
    public CalibrationSessionNotFoundException(string message) : base(message) { }
    public CalibrationSessionNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    public CalibrationSessionNotFoundException(Guid sessionId)
        : base($"No calibration session with light frames was found for id {sessionId}.") { }
}
