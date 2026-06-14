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
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §50 SQLite-backed stats — aggregations over the §28 catalog's
/// <c>frames</c> + <c>sessions</c> tables. Views that need data not yet
/// captured (focuser position, separated RA/Dec RMS, dither offsets)
/// return empty payloads in v0.0.1; they wire up when the §38 sequence
/// orchestrator starts persisting those columns.
/// </summary>
public sealed class SqliteStatsService : IStatsService {
    private readonly IAraDatabase _db;

    public SqliteStatsService(IAraDatabase db) {
        _db = db;
    }

    public async Task<StatsOverviewDto> GetOverviewAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();

        await using var overviewCmd = conn.CreateCommand();
        overviewCmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM sessions) AS total_sessions,
                COUNT(*) AS total_frames,
                SUM(CASE WHEN frame_type = 'light' THEN 1 ELSE 0 END) AS light_frames,
                CAST(IFNULL(SUM(CASE WHEN frame_type = 'light' THEN exposure_seconds ELSE 0 END), 0) AS REAL) / 3600.0 AS integration_hours,
                COUNT(DISTINCT target_name) AS unique_targets,
                MIN(captured_utc) AS first_utc,
                MAX(captured_utc) AS last_utc
            FROM frames;
            """;

        int sessions = 0, frames = 0, lights = 0, targets = 0;
        double hours = 0;
        DateTimeOffset? first = null, last = null;
        await using (var reader = await overviewCmd.ExecuteReaderAsync(ct)) {
            if (await reader.ReadAsync(ct)) {
                sessions = await reader.IsDBNullAsync(0, ct) ? 0 : Convert.ToInt32(reader.GetValue(0));
                frames = await reader.IsDBNullAsync(1, ct) ? 0 : Convert.ToInt32(reader.GetValue(1));
                lights = await reader.IsDBNullAsync(2, ct) ? 0 : Convert.ToInt32(reader.GetValue(2));
                hours = await reader.IsDBNullAsync(3, ct) ? 0 : reader.GetDouble(3);
                targets = await reader.IsDBNullAsync(4, ct) ? 0 : Convert.ToInt32(reader.GetValue(4));
                first = await reader.IsDBNullAsync(5, ct) ? null : DateTimeOffset.Parse(reader.GetString(5));
                last = await reader.IsDBNullAsync(6, ct) ? null : DateTimeOffset.Parse(reader.GetString(6));
            }
        }

        // Last-30-days sparkline: integration hours grouped by date(captured_utc).
        // Filter on captured_utc rather than session start so re-captures of an
        // old session don't skew the rolling window.
        var thirty = DateTimeOffset.UtcNow.AddDays(-30);
        await using var sparkCmd = conn.CreateCommand();
        sparkCmd.CommandText = """
            SELECT date(captured_utc) AS day,
                   CAST(IFNULL(SUM(CASE WHEN frame_type = 'light' THEN exposure_seconds ELSE 0 END), 0) AS REAL) / 3600.0
            FROM frames
            WHERE captured_utc >= $since
            GROUP BY day
            ORDER BY day ASC;
            """;
        sparkCmd.Parameters.AddWithValue("$since", thirty.ToString("O"));
        var sparkX = new List<DateTimeOffset>();
        var sparkY = new List<double>();
        await using (var reader = await sparkCmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct)) {
                sparkX.Add(DateTimeOffset.Parse(reader.GetString(0) + "T00:00:00Z"));
                sparkY.Add(reader.GetDouble(1));
            }
        }
        var sparkline = sparkX.Count > 0
            ? new StatsSparklineDto<DateTimeOffset, double>("integration_hours_per_day", sparkX, sparkY)
            : null;

        return new StatsOverviewDto(
            TotalSessions: sessions,
            TotalFrames: frames,
            TotalLightFrames: lights,
            TotalIntegrationHours: hours,
            UniqueTargetsImaged: targets,
            FirstImageUtc: first,
            LastImageUtc: last,
            LastSessionScore: null,  // composite score needs §50.10 weighted calc; out of scope for SQL aggregation
            Last30DaysIntegrationHours: sparkline);
    }

    public async Task<StatsTargetsDto> GetTargetsAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT target_name,
                   COUNT(*) AS frame_count,
                   CAST(IFNULL(SUM(CASE WHEN frame_type = 'light' THEN exposure_seconds ELSE 0 END), 0) AS REAL) / 3600.0 AS integration_hours,
                   MAX(captured_utc) AS last_utc
            FROM frames
            WHERE target_name IS NOT NULL AND target_name != ''
            GROUP BY target_name
            ORDER BY frame_count DESC;
            """;
        var rows = new List<StatsTargetSummaryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            rows.Add(new StatsTargetSummaryDto(
                TargetName: reader.GetString(0),
                FrameCount: reader.GetInt32(1),
                IntegrationHours: reader.GetDouble(2),
                CompositeQualityScore: null,  // would require averaging across frames' composite scores; future enhancement
                LastImagedUtc: DateTimeOffset.Parse(reader.GetString(3))));
        }
        return new StatsTargetsDto(rows);
    }

    public Task<StatsFocusTempDto> GetFocusTempAsync(DateTimeOffset? since, CancellationToken ct) =>
        // Focuser position isn't a column on the frames table yet — the
        // §38 sequence orchestrator will start persisting it (focuser
        // mediator events plus per-frame snapshot). Until then this view
        // is empty + correlation null. WILMA's chart renders "no data".
        Task.FromResult(new StatsFocusTempDto(Array.Empty<FocusTempPointDto>(), CorrelationR2: null));

    public async Task<StatsGuidingDto> GetGuidingAsync(DateTimeOffset? since, CancellationToken ct) {
        // Guiding RMS per frame is captured (guiding_rms_arcsec column),
        // but separated RA/Dec RMS columns don't exist yet — null those.
        // Time series + summary stats are derivable.
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT captured_utc, guiding_rms_arcsec
            FROM frames
            WHERE guiding_rms_arcsec IS NOT NULL
            """;
        if (since.HasValue) {
            sql += " AND captured_utc >= $since";
            cmd.Parameters.AddWithValue("$since", since.Value.ToString("O"));
        }
        sql += " ORDER BY captured_utc ASC;";
        cmd.CommandText = sql;

        var samples = new List<GuidingRmsPointDto>();
        var values = new List<double>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct)) {
                var ts = DateTimeOffset.Parse(reader.GetString(0));
                var rms = reader.GetDouble(1);
                samples.Add(new GuidingRmsPointDto(Timestamp: ts, RmsArcsec: rms, RaRms: null, DecRms: null));
                values.Add(rms);
            }
        }

        double? mean = values.Count > 0 ? values.Average() : null;
        double? p95 = null;
        if (values.Count > 0) {
            var sorted = values.OrderBy(v => v).ToArray();
            // Linear-interpolation percentile.
            var rank = 0.95 * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            p95 = lo == hi ? sorted[lo] : sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
        }
        return new StatsGuidingDto(samples, MeanRmsArcsec: mean, P95RmsArcsec: p95);
    }

    public async Task<StatsFrameQualityDto> GetFrameQualityAsync(string? filter, CancellationToken ct) {
        // Histogram of composite_quality_score over frames matching the filter.
        // Composite is stored in the quality_score_json blob; we read the
        // blob and parse rather than denormalizing into a real column for
        // v0.0.1 (cheap on the typical catalog size — single nightly).
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var sql = "SELECT quality_score_json FROM frames WHERE quality_score_json IS NOT NULL";
        if (!string.IsNullOrEmpty(filter)) {
            sql += " AND filter_name = $filter";
            cmd.Parameters.AddWithValue("$filter", filter);
        }
        sql += ";";
        cmd.CommandText = sql;

        var scores = new List<double>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) {
            while (await reader.ReadAsync(ct)) {
                try {
                    var qs = JsonSerializer.Deserialize(reader.GetString(0),
                        AraJsonSerializerContext.Default.QualityScoreBreakdownDto);
                    if (qs is not null) scores.Add(qs.Composite);
                } catch (JsonException) { /* skip corrupt blob */ }
            }
        }

        // 10 buckets, [0, 0.1), [0.1, 0.2), … [0.9, 1.0]
        var buckets = new int[10];
        foreach (var s in scores) {
            var idx = Math.Clamp((int)(s * 10), 0, 9);
            buckets[idx]++;
        }
        var distribution = new List<FrameQualityBucketDto>(10);
        for (var i = 0; i < 10; i++) {
            distribution.Add(new FrameQualityBucketDto(
                RangeLow: i / 10.0,
                RangeHigh: (i + 1) / 10.0,
                Count: buckets[i]));
        }
        var mean = scores.Count > 0 ? scores.Average() : 0;
        double? stddev = null;
        if (scores.Count > 1) {
            var variance = scores.Average(s => (s - mean) * (s - mean));
            stddev = Math.Sqrt(variance);
        }
        return new StatsFrameQualityDto(distribution, MeanScore: mean, StdDev: stddev);
    }

    public async Task<StatsBestFramesDto> GetBestFramesAsync(int limit, CancellationToken ct) {
        // Top frames by composite_quality_score (extracted from JSON blob).
        // SQLite's json_extract() lets us pull it inline + ORDER BY it.
        var cap = Math.Clamp(limit, 1, 100);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_name, captured_utc,
                   CAST(json_extract(quality_score_json, '$.composite') AS REAL) AS composite,
                   filter_name
            FROM frames
            WHERE quality_score_json IS NOT NULL
            ORDER BY composite DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", cap);

        var rows = new List<BestFrameDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            rows.Add(new BestFrameDto(
                FrameId: Guid.Parse(reader.GetString(0)),
                TargetName: reader.GetString(1),
                CapturedUtc: DateTimeOffset.Parse(reader.GetString(2)),
                CompositeScore: await reader.IsDBNullAsync(3, ct) ? 0 : reader.GetDouble(3),
                FilterName: await reader.IsDBNullAsync(4, ct) ? null : reader.GetString(4)));
        }
        return new StatsBestFramesDto(rows);
    }

    public async Task<StatsCalendarDto> GetCalendarAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) {
        // Per-day frame counts + integration + distinct target names.
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date(captured_utc) AS day,
                   COUNT(*),
                   CAST(IFNULL(SUM(CASE WHEN frame_type = 'light' THEN exposure_seconds ELSE 0 END), 0) AS REAL) / 3600.0,
                   GROUP_CONCAT(DISTINCT target_name)
            FROM frames
            WHERE date(captured_utc) BETWEEN $from AND $to
            GROUP BY day
            ORDER BY day ASC;
            """;
        cmd.Parameters.AddWithValue("$from", fromDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", toDate.ToString("yyyy-MM-dd"));

        var days = new List<StatsCalendarDayDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var date = DateOnly.Parse(reader.GetString(0));
            var count = reader.GetInt32(1);
            var hours = reader.GetDouble(2);
            var targetsCsv = await reader.IsDBNullAsync(3, ct) ? "" : reader.GetString(3);
            var targets = string.IsNullOrEmpty(targetsCsv)
                ? Array.Empty<string>()
                : targetsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            days.Add(new StatsCalendarDayDto(date, count, hours, targets));
        }
        return new StatsCalendarDto(days);
    }

    public async Task<StatsAchievementsDto> GetAchievementsAsync(CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        // Both queries run inside one transaction so the headline aggregates and the per-night
        // breakdown reflect a single consistent snapshot — a frame inserted between them can't make
        // TotalLightFrames and TotalNightsImaged disagree.
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Headline aggregates over light frames.
        await using var aggCmd = conn.CreateCommand();
        aggCmd.Transaction = tx;
        // COUNT(DISTINCT target_name) excludes NULL per SQL standard — intentional here: a frame with no
        // target name isn't a "target imaged", so it shouldn't inflate the unique-targets record.
        aggCmd.CommandText = """
            SELECT
                COUNT(*) AS light_frames,
                CAST(IFNULL(SUM(exposure_seconds), 0) AS REAL) / 3600.0 AS integration_hours,
                COUNT(DISTINCT target_name) AS unique_targets,
                MIN(captured_utc) AS first_light
            FROM frames WHERE frame_type = 'light';
            """;
        var totalLightFrames = 0;
        var totalHours = 0.0;
        var uniqueTargets = 0;
        DateTimeOffset? firstLight = null;
        await using (var r = await aggCmd.ExecuteReaderAsync(ct)) {
            if (await r.ReadAsync(ct)) {
                totalLightFrames = r.GetInt32(0);
                totalHours = r.GetDouble(1);
                uniqueTargets = r.GetInt32(2);
                if (!await r.IsDBNullAsync(3, ct) &&
                    DateTimeOffset.TryParse(r.GetString(3), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fl)) {
                    firstLight = fl;
                }
            }
        }

        // Per-night integration (distinct capture days, same date() basis as the calendar view).
        await using var nightsCmd = conn.CreateCommand();
        nightsCmd.Transaction = tx;
        nightsCmd.CommandText = """
            SELECT date(captured_utc) AS day,
                   CAST(IFNULL(SUM(exposure_seconds), 0) AS REAL) / 3600.0 AS hours
            FROM frames WHERE frame_type = 'light'
            GROUP BY day ORDER BY day ASC;
            """;
        var nights = new List<DateOnly>();
        var longestNightHours = 0.0;
        await using (var r = await nightsCmd.ExecuteReaderAsync(ct)) {
            while (await r.ReadAsync(ct)) {
                nights.Add(DateOnly.Parse(r.GetString(0), CultureInfo.InvariantCulture));
                longestNightHours = Math.Max(longestNightHours, r.GetDouble(1));
            }
        }

        // Both reads are done — commit to release the WAL read lock now rather than holding it until dispose.
        await tx.CommitAsync(ct);

        var (longestStreak, currentStreak) = ComputeStreaks(nights, DateOnly.FromDateTime(DateTime.UtcNow));

        return new StatsAchievementsDto(
            TotalNightsImaged: nights.Count,
            LongestStreakNights: longestStreak,
            CurrentStreakNights: currentStreak,
            LongestNightHours: longestNightHours,
            TotalIntegrationHours: totalHours,
            UniqueTargetsImaged: uniqueTargets,
            TotalLightFrames: totalLightFrames,
            FirstLightUtc: firstLight,
            Milestones: BuildMilestones(totalHours, uniqueTargets, nights.Count, totalLightFrames));
    }

    // Longest run of consecutive calendar days, and the *current* streak — the run ending at the most recent
    // night, but only if that night is still "live" (today or yesterday; a 1-day grace so an in-progress night
    // isn't dropped before midnight UTC). A run that ended days ago is stale → current streak is 0, even though
    // longest is retained. `nights` is ascending + de-duplicated by the GROUP BY.
    private static (int Longest, int Current) ComputeStreaks(List<DateOnly> nights, DateOnly today) {
        if (nights.Count == 0) {
            return (0, 0);
        }
        // Seed both at 1 to account for nights[0]; the loop extends from index 1. The empty-list guard
        // above guarantees at least one element, so a single-night catalog correctly yields (1, …).
        var longest = 1;
        var run = 1;
        for (var i = 1; i < nights.Count; i++) {
            run = nights[i] == nights[i - 1].AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }
        var current = nights[^1] >= today.AddDays(-1) ? run : 0;
        return (longest, current);
    }

    private static StatsMilestoneDto[] BuildMilestones(double hours, int targets, int nights, int frames) {
        static StatsMilestoneDto M(string id, string title, string desc, double threshold, double current) =>
            new(id, title, desc, current >= threshold, threshold, current);
        return new[] {
            M("hours_10", "Getting started", "10 hours of integration", 10, hours),
            M("hours_50", "Seasoned imager", "50 hours of integration", 50, hours),
            M("hours_100", "Centurion", "100 hours of integration", 100, hours),
            M("targets_10", "Explorer", "10 unique targets imaged", 10, targets),
            M("targets_25", "Cartographer", "25 unique targets imaged", 25, targets),
            M("nights_10", "Night owl", "10 nights under the stars", 10, nights),
            M("nights_50", "Dark-sky devotee", "50 nights under the stars", 50, nights),
            M("frames_1000", "Light collector", "1000 light frames captured", 1000, frames),
        };
    }

    public async Task<(Stream Stream, string FileName)?> OpenCsvExportAsync(string scope, CancellationToken ct) {
        // v0.0.1: scope is informational; we always dump the full frames
        // table. Filter-by-scope queries (per-session, per-target) land
        // when the WILMA Stats tab adds those export buttons.
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, target_name, frame_type, filter_name,
                   exposure_seconds, gain, captured_utc, hfr, star_count,
                   eccentricity, guiding_rms_arcsec, snr_estimate, rating
            FROM frames ORDER BY captured_utc ASC;
            """;
        var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(
            "id,session_id,target_name,frame_type,filter_name,exposure_seconds,gain,captured_utc,hfr,star_count,eccentricity,guiding_rms_arcsec,snr_estimate,rating");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            await writer.WriteLineAsync(string.Join(',',
                reader.GetString(0),
                reader.GetString(1),
                CsvEscape(reader.GetString(2)),
                reader.GetString(3),
                CsvEscape(await reader.IsDBNullAsync(4, ct) ? "" : reader.GetString(4)),
                reader.GetInt32(5).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(6).ToString(CultureInfo.InvariantCulture),
                reader.GetString(7),
                await reader.IsDBNullAsync(8, ct) ? "" : reader.GetDouble(8).ToString("G", CultureInfo.InvariantCulture),
                await reader.IsDBNullAsync(9, ct) ? "" : reader.GetInt32(9).ToString(CultureInfo.InvariantCulture),
                await reader.IsDBNullAsync(10, ct) ? "" : reader.GetDouble(10).ToString("G", CultureInfo.InvariantCulture),
                await reader.IsDBNullAsync(11, ct) ? "" : reader.GetDouble(11).ToString("G", CultureInfo.InvariantCulture),
                await reader.IsDBNullAsync(12, ct) ? "" : reader.GetDouble(12).ToString("G", CultureInfo.InvariantCulture),
                reader.GetInt32(13).ToString(CultureInfo.InvariantCulture)));
        }
        await writer.FlushAsync(ct);
        ms.Position = 0;
        return (ms, $"openastroara-frames-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static readonly System.Buffers.SearchValues<char> CsvSpecialChars =
        System.Buffers.SearchValues.Create(",\"\n");

    private static string CsvEscape(string field) {
        if (field.AsSpan().IndexOfAny(CsvSpecialChars) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}