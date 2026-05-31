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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28-backed <see cref="ISessionService"/>. The sessions table per §28.1
/// stores the core lifecycle fields (id, started_at, ended_at,
/// recovery_needed, profile_id, frame_count, etc.); the user-facing
/// <see cref="SessionDto"/> adds derived fields (target name, light/cal
/// counts, filters used) that are computed by aggregating the frames
/// table at read time.
///
/// GetFramesAsync + GetHfrAnalysisAsync delegate to the frame repo —
/// already SQLite-backed, so this service composes on top.
///
/// Mutating endpoints (ResumeTargetAsync / RestretchAsync) keep the
/// placeholder Accepted shape; they need the §38 sequence orchestrator
/// + §65 stretch pipeline to actually execute.
/// </summary>
public sealed class SqliteSessionService : ISessionService {
    private readonly IAraDatabase _db;
    private readonly IFrameRepository _frames;

    public SqliteSessionService(IAraDatabase db, IFrameRepository frames) {
        _db = db;
        _frames = frames;
    }

    public async Task<CursorPage<SessionDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_at, ended_at, profile_id, frame_count
            FROM sessions
            ORDER BY started_at DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        var rows = new List<(Guid Id, DateTimeOffset Start, DateTimeOffset? End, string? ProfileId, int FrameCount)>(pageSize);
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct) && rows.Count < pageSize) {
                rows.Add((
                    Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4)));
            }
            var hasMore = await reader.ReadAsync(ct);
            var items = new List<SessionDto>(rows.Count);
            foreach (var r in rows) {
                items.Add(await BuildSessionDtoAsync(conn, r.Id, r.Start, r.End, r.ProfileId, ct));
            }
            var nextCursor = hasMore ? (offset + pageSize).ToString() : null;
            return new CursorPage<SessionDto>(items, NextCursor: nextCursor, HasMore: hasMore);
        }
    }

    public async Task<SessionDto?> GetAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, started_at, ended_at, profile_id FROM sessions WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var sid = Guid.Parse(reader.GetString(0));
        var start = DateTimeOffset.Parse(reader.GetString(1));
        var end = reader.IsDBNull(2) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(2));
        var profileId = reader.IsDBNull(3) ? null : reader.GetString(3);
        await reader.CloseAsync();
        return await BuildSessionDtoAsync(conn, sid, start, end, profileId, ct);
    }

    public Task<CursorPage<FrameListItemDto>> GetFramesAsync(Guid sessionId, int limit, string? cursor, CancellationToken ct) =>
        // Delegate to the frame repo so /api/v1/sessions/{id}/frames stays
        // consistent with /api/v1/frames?sessionId=…
        _frames.ListAsync(limit, cursor, sessionId, targetName: null, ct);

    public Task<OperationAcceptedDto> ResumeTargetAsync(Guid sessionId, ResumeTargetRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        // §38 sequence orchestrator required for real execution; placeholder
        // 202 keeps the wire contract intact.
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sessions.resume-target", idempotencyKey));

    public Task<OperationAcceptedDto> RestretchAsync(Guid sessionId, SessionRestretchRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        // §65 stretch pipeline required for real re-stretch; placeholder
        // 202 keeps the wire contract intact.
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sessions.restretch", idempotencyKey));

    public async Task<HfrAnalysisDto?> GetHfrAnalysisAsync(Guid sessionId, CancellationToken ct) {
        // 404 unless the session exists in the catalog.
        var session = await GetAsync(sessionId, ct);
        if (session is null) return null;

        var page = await _frames.ListAsync(limit: 200, cursor: null, sessionId, targetName: null, ct);
        var points = page.Items
            .Where(f => f.Hfr.HasValue)
            .OrderBy(f => f.CapturedUtc)
            .Select(f => new HfrTimeSeriesPointDto(
                Timestamp: f.CapturedUtc,
                Hfr: f.Hfr!.Value,
                StarCount: f.StarCount,
                FrameId: f.Id))
            .ToList();

        if (points.Count == 0) {
            return new HfrAnalysisDto(
                SessionId: sessionId,
                FrameCount: 0,
                MeanHfr: 0,
                StandardDeviation: 0,
                TrendSlopePerHour: 0,
                Trend: "insufficient-data",
                TimeSeries: Array.Empty<HfrTimeSeriesPointDto>());
        }

        var mean = points.Average(p => p.Hfr);
        var variance = points.Average(p => (p.Hfr - mean) * (p.Hfr - mean));
        var stdDev = Math.Sqrt(variance);

        var t0 = points[0].Timestamp;
        var xs = points.Select(p => (p.Timestamp - t0).TotalHours).ToList();
        var ys = points.Select(p => p.Hfr).ToList();
        var xMean = xs.Average();
        var yMean = ys.Average();
        var numerator = 0.0;
        var denominator = 0.0;
        for (int i = 0; i < points.Count; i++) {
            var dx = xs[i] - xMean;
            numerator += dx * (ys[i] - yMean);
            denominator += dx * dx;
        }
        var slopePerHour = denominator > 0 ? numerator / denominator : 0;

        var trend = slopePerHour > 0.05 ? "degrading"
                  : slopePerHour < -0.05 ? "improving"
                  : "stable";

        return new HfrAnalysisDto(
            SessionId: sessionId,
            FrameCount: points.Count,
            MeanHfr: mean,
            StandardDeviation: stdDev,
            TrendSlopePerHour: slopePerHour,
            Trend: trend,
            TimeSeries: points);
    }

    /// <summary>
    /// Build the user-facing SessionDto by aggregating frame stats. Per §28.1
    /// the sessions table doesn't store the display-name, target-name, or
    /// per-type frame counts — those come from the frames table at read
    /// time. The aggregation runs in a single query bounded by session id;
    /// catalog size doesn't affect this hot path.
    /// </summary>
    private static async Task<SessionDto> BuildSessionDtoAsync(
            SqliteConnection conn, Guid sessionId,
            DateTimeOffset start, DateTimeOffset? end, string? profileId,
            CancellationToken ct) {
        // Single aggregation query covering the four derived fields.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total_frames,
                SUM(CASE WHEN frame_type = 'light' THEN 1 ELSE 0 END) AS light_frames,
                SUM(CASE WHEN frame_type IN ('dark', 'flat', 'bias', 'darkflat') THEN 1 ELSE 0 END) AS cal_frames
            FROM frames WHERE session_id = $sid;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId.ToString());

        var totalFrames = 0;
        var lightFrames = 0;
        var calFrames = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            if (await reader.ReadAsync(ct)) {
                totalFrames = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                lightFrames = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                calFrames = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
            }
        }

        // Most-common target_name across frames serves as the session's
        // display target. Ignore null/blank target_names. Result null →
        // "Untitled" placeholder.
        await using var targetCmd = conn.CreateCommand();
        targetCmd.CommandText = """
            SELECT target_name
            FROM frames
            WHERE session_id = $sid AND target_name IS NOT NULL AND target_name != ''
            GROUP BY target_name
            ORDER BY COUNT(*) DESC
            LIMIT 1;
            """;
        targetCmd.Parameters.AddWithValue("$sid", sessionId.ToString());
        var target = (string?)(await targetCmd.ExecuteScalarAsync(ct)) ?? "Untitled";

        // Distinct filter_names used in this session. NULL filter_name is
        // skipped (darks/bias don't carry filter info).
        var filters = new List<string>();
        await using (var filterCmd = conn.CreateCommand()) {
            filterCmd.CommandText = """
                SELECT DISTINCT filter_name FROM frames
                WHERE session_id = $sid AND filter_name IS NOT NULL
                ORDER BY filter_name;
                """;
            filterCmd.Parameters.AddWithValue("$sid", sessionId.ToString());
            await using var reader = await filterCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                filters.Add(reader.GetString(0));
            }
        }

        // Display name: "YYYY-MM-DD <target>" matches what the placeholder
        // returned + what NINA users expect from the §40 Library tab.
        var name = $"{start:yyyy-MM-dd} {target}";

        return new SessionDto(
            Id: sessionId,
            Name: name,
            TargetName: target,
            SessionStartUtc: start,
            SessionEndUtc: end,
            TotalFrames: totalFrames,
            LightFrames: lightFrames,
            CalibrationFrames: calFrames,
            FiltersUsed: filters,
            ProfileId: profileId,
            // §65 stretch palette tracking — null until the user sets one
            // on first preview/restretch. Stored per-session when §65 lands.
            StretchPaletteUsed: null);
    }
}
