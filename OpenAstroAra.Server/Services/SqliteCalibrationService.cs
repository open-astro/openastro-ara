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
/// light by <c>(exposure, gain, temperature)</c>, where temperature is bucketed to the nearest whole degree
/// (NULL temperature — an uncooled camera with no sensor — matches NULL). The dark-library <em>build</em> is a
/// separate, guider/sequencer-gated concern (<see cref="IDarkLibraryService"/>);
/// this service only reports availability + generates the matching-flats plan.
/// </summary>
public sealed class SqliteCalibrationService : ICalibrationService {
    // Fallback defaults when no profile store is wired (test/bootstrap contexts) — production
    // reads the §48.7 flat_panel policy fields off SafetyPoliciesDto instead.
    private const int DefaultFlatFrames = 20;
    private const int DefaultTargetAdu = 32000;
    private const double DefaultTargetAduTolerancePct = 5;
    // §48.7 sky_flat fallbacks (store-less contexts only; production reads the policy fields).
    private const int DefaultSkyFlatFrames = 20;
    private const int DefaultSkyTargetAdu = 25000;
    private const double DefaultSkyStopAtMaxAdu = 50000;
    private const double DefaultSkyStopAtMinAdu = 5000;
    private const double DefaultSkySunAltitude = -9;
    private const double DefaultSkyAzimuth = 90;
    private const double DefaultSkyAltitude = 75;

    private readonly IAraDatabase _db;
    private readonly ISequenceService? _sequences;
    private readonly IProfileStore? _profile;

    /// <param name="db">The §28 frame catalog.</param>
    /// <param name="sequences">The §38 sequence store the generated matching-flats sequence is
    /// persisted into. Null (test/bootstrap contexts only — Program.cs always wires the real
    /// store) degrades generation to plan-only, exactly as if the caller set GenerateOnly.</param>
    /// <param name="profile">The §48.7 flat_panel policy source (target ADU, tolerance, frames
    /// per filter, post-flat park). Null falls back to the conventional defaults above.</param>
    public SqliteCalibrationService(IAraDatabase db, ISequenceService? sequences = null, IProfileStore? profile = null) {
        _db = db;
        _sequences = sequences;
        _profile = profile;
    }

    // Keyset cursor prefix. captured_utc is stored as ISO-8601 "O" (fixed-width, UTC), so string
    // comparison IS chronological comparison — the keyset predicate runs directly on the stored text.
    private const string KeysetCursorPrefix = "k2:";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "CommandText is a ternary over two compile-time constant literals (keyset vs legacy-offset page query); the cursor's parsed pieces only ever reach bound $ parameters, never the text.")]
    public async Task<CursorPage<CalibrationSessionDto>> ListSessionsAsync(int limit, string? cursor, CancellationToken ct) {
        var pageSize = Math.Clamp(limit, 1, 200);

        // The cursor is opaque wire-contract-wise, so its format can evolve server-side: new cursors are
        // keyset ("k2:{startedUtc}|{sessionId}" of the page's last row — O(log n) per page and stable when
        // sessions land mid-pagination), while a bare integer from a response minted before this change
        // still pages via the legacy OFFSET path so an in-flight pagination doesn't break.
        string? afterStarted = null, afterSid = null;
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor)) {
            if (cursor.StartsWith(KeysetCursorPrefix, StringComparison.Ordinal)
                    && cursor.IndexOf('|', StringComparison.Ordinal) is var sep && sep > KeysetCursorPrefix.Length) {
                afterStarted = cursor[KeysetCursorPrefix.Length..sep];
                afterSid = cursor[(sep + 1)..];
            } else if (int.TryParse(cursor, out var parsed) && parsed >= 0) {
                offset = parsed;
            }
        }

        await using var conn = _db.OpenConnection();

        // Page over the sessions that have LIGHT frames, newest first (session_id is the deterministic
        // tiebreaker — equal timestamps must not reorder between pages). Fetch one extra row to know if
        // there's a next page without a second COUNT query. Started is kept as the RAW stored string:
        // the next cursor must compare byte-identically against captured_utc, so it never round-trips
        // through DateTimeOffset (which would rewrite the "+00:00" suffix and break tie comparisons).
        var rows = new List<(Guid Id, string Sid, string Started)>();
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = afterStarted is not null
                ? """
                  SELECT session_id, MIN(captured_utc) AS started
                  FROM frames
                  WHERE frame_type = 'light'
                  GROUP BY session_id
                  HAVING started < $cStarted OR (started = $cStarted AND session_id < $cSid)
                  ORDER BY started DESC, session_id DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT session_id, MIN(captured_utc) AS started
                  FROM frames
                  WHERE frame_type = 'light'
                  GROUP BY session_id
                  ORDER BY started DESC, session_id DESC
                  LIMIT $limit OFFSET $offset;
                  """;
            cmd.Parameters.AddWithValue("$limit", pageSize + 1);
            if (afterStarted is not null) {
                cmd.Parameters.AddWithValue("$cStarted", afterStarted);
                cmd.Parameters.AddWithValue("$cSid", afterSid!);
            } else {
                cmd.Parameters.AddWithValue("$offset", offset);
            }
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var sid = reader.GetString(0);
                if (Guid.TryParse(sid, out var id)) {
                    rows.Add((id, sid, reader.GetString(1)));
                }
            }
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore) {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = await BuildSessionDtosAsync(conn, rows, ct);

        // The keyset anchor is the last row that stays ON the page — its raw (started, session_id) pair.
        var nextCursor = hasMore && rows.Count > 0
            ? KeysetCursorPrefix + rows[^1].Started + "|" + rows[^1].Sid
            : null;
        return new CursorPage<CalibrationSessionDto>(items, nextCursor, hasMore && nextCursor is not null);
    }

    // Batched page assembly (§39 perf): the per-session BuildSessionDtoAsync runs 5 queries per id —
    // 5N+1 per page. This runs the same 5 aggregations ONCE each over the whole page's id set
    // (session_id IN (...)), so a page costs 6 queries total regardless of size.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The composed fragment is a parameter-placeholder list ($s0,$s1,…) generated locally; every value is bound as a parameter. No user input reaches the command text.")]
    private static async Task<List<CalibrationSessionDto>> BuildSessionDtosAsync(
            SqliteConnection conn, List<(Guid Id, string Sid, string Started)> ids, CancellationToken ct) {
        var items = new List<CalibrationSessionDto>(ids.Count);
        if (ids.Count == 0) {
            return items;
        }
        var placeholders = string.Join(",", Enumerable.Range(0, ids.Count).Select(i => "$s" + i.ToString(CultureInfo.InvariantCulture)));
        void BindIds(SqliteCommand cmd) {
            for (var i = 0; i < ids.Count; i++) {
                cmd.Parameters.AddWithValue("$s" + i.ToString(CultureInfo.InvariantCulture), ids[i].Sid);
            }
        }

        // 1) Session headers from their LIGHT frames.
        var headers = new Dictionary<string, (int Count, DateTimeOffset Start, DateTimeOffset End, string Target)>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = $"""
                SELECT session_id, COUNT(*), MIN(captured_utc), MAX(captured_utc), MIN(target_name)
                FROM frames
                WHERE frame_type = 'light' AND session_id IN ({placeholders})
                GROUP BY session_id;
                """;
            BindIds(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                headers[reader.GetString(0)] = (
                    reader.GetInt32(1),
                    ParseUtc(reader.GetString(2)),
                    ParseUtc(reader.GetString(3)),
                    await reader.IsDBNullAsync(4, ct) ? "(unknown)" : reader.GetString(4));
            }
        }

        // 2) Per-filter summaries.
        var filtersBySession = new Dictionary<string, List<CalibrationFilterSummaryDto>>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = $"""
                SELECT session_id, filter_name, COUNT(*), AVG(exposure_seconds)
                FROM frames
                WHERE frame_type = 'light' AND session_id IN ({placeholders})
                GROUP BY session_id, filter_name
                ORDER BY session_id, filter_name;
                """;
            BindIds(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var sid = reader.GetString(0);
                if (!filtersBySession.TryGetValue(sid, out var list)) {
                    filtersBySession[sid] = list = [];
                }
                list.Add(new CalibrationFilterSummaryDto(
                    FilterName: await reader.IsDBNullAsync(1, ct) ? NoFilter : reader.GetString(1),
                    LightFrameCount: reader.GetInt32(2),
                    MeanExposureSeconds: await reader.IsDBNullAsync(3, ct) ? 0 : reader.GetDouble(3),
                    RecommendedFlatFrames: DefaultFlatFrames));
            }
        }

        // 3+4) Coverage — the sessions with at least one UNCOVERED requirement. Same semantics as the
        // per-session EXCEPT queries (coverage is GLOBAL: flats/darks are a shared library), expressed as
        // NOT EXISTS with SQLite's null-safe IS so a no-filter light still matches a no-filter flat.
        var flatsUncovered = await UncoveredSessionsAsync(conn, $"""
            SELECT DISTINCT l.session_id
            FROM frames l
            WHERE l.frame_type = 'light' AND l.session_id IN ({placeholders})
              AND NOT EXISTS (
                  SELECT 1 FROM frames f
                  WHERE f.frame_type = 'flat' AND f.filter_name IS l.filter_name)
            """, BindIds, ct);
        // Darks match by (exposure, gain, temperature bucketed to the nearest whole degree; NULL buckets
        // with 0 for uncooled cameras across sentinel generations — see BuildSessionDtoAsync's remarks).
        var darksUncovered = await UncoveredSessionsAsync(conn, $"""
            SELECT DISTINCT l.session_id
            FROM frames l
            WHERE l.frame_type = 'light' AND l.session_id IN ({placeholders})
              AND NOT EXISTS (
                  SELECT 1 FROM frames d
                  WHERE d.frame_type = 'dark'
                    AND d.exposure_seconds IS l.exposure_seconds
                    AND d.gain IS l.gain
                    AND ROUND(COALESCE(d.temperature_c, 0), 0) IS ROUND(COALESCE(l.temperature_c, 0), 0))
            """, BindIds, ct);

        // 5) Owning profile ids.
        var profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = $"SELECT id, profile_id FROM sessions WHERE id IN ({placeholders});";
            BindIds(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                if (!await reader.IsDBNullAsync(1, ct) && reader.GetString(1) is { Length: > 0 } pid) {
                    profiles[reader.GetString(0)] = pid;
                }
            }
        }

        // Assemble in page order. A missing header means the session's lights vanished between the page
        // query and here (raced deletion) — skip it, matching the old per-session null contract.
        foreach (var (id, sid, _) in ids) {
            if (!headers.TryGetValue(sid, out var h)) {
                continue;
            }
            items.Add(new CalibrationSessionDto(
                Id: id,
                TargetName: h.Target,
                SessionStartUtc: h.Start,
                SessionEndUtc: h.End,
                LightFrameCount: h.Count,
                FiltersUsed: filtersBySession.GetValueOrDefault(sid) ?? [],
                MatchingFlatsAvailable: !flatsUncovered.Contains(sid),
                MatchingDarksAvailable: !darksUncovered.Contains(sid),
                ProfileId: profiles.GetValueOrDefault(sid)));
        }
        return items;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The 'sql' argument is a locally-composed literal whose only interpolation is the generated parameter-placeholder list; all values are bound parameters.")]
    private static async Task<HashSet<string>> UncoveredSessionsAsync(
            SqliteConnection conn, string sql, Action<SqliteCommand> bind, CancellationToken ct) {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql + ";";
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task<CalibrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        return await BuildSessionDtoAsync(conn, id, ct);
    }

    public async Task<GeneratedFlatSequenceDto> GenerateMatchingFlatsAsync(Guid sessionId, MatchingFlatsRequestDto request, string? idempotencyKey, CancellationToken ct) {
        await using var conn = _db.OpenConnection();

        var session = await BuildSessionDtoAsync(conn, sessionId, ct)
            ?? throw new CalibrationSessionNotFoundException(sessionId);

        // §48.7 flat_panel / sky_flat policy (request overrides win, then the profile, then the
        // fallback constants for a store-less context). The sky flavor has its own target/count
        // defaults — twilight is dimmer and drifts, so §48.7 specs 25000/20 vs the panel's 30000/30.
        var sky = string.Equals(request.Flavor, "sky", StringComparison.OrdinalIgnoreCase);
        var policies = _profile?.GetSafetyPolicies();
        var policyFrames = sky ? policies?.SkyFlatFramesPerFilter : policies?.FlatFramesPerFilter;
        var policyTarget = sky ? policies?.SkyFlatTargetAdu : policies?.FlatTargetAdu;
        var frameCount = request.OverrideFrameCount is int n && n > 0
            ? n
            : policyFrames is int p && p > 0 ? p : (sky ? DefaultSkyFlatFrames : DefaultFlatFrames);
        var targetAdu = request.OverrideTargetAdu is int o && o > 0
            ? o
            : policyTarget is int t && t > 0 ? t : (sky ? DefaultSkyTargetAdu : DefaultTargetAdu);
        var tolerancePct = policies?.FlatTargetAduTolerancePct is double tol && tol > 0
            ? tol : DefaultTargetAduTolerancePct;
        // NB: the park rides only the persisted sequence body — GeneratedFlatSequenceDto.Steps
        // is the per-filter capture plan and deliberately doesn't model non-capture steps.
        // Store-less fallback is FALSE (not the DTO's true default) on purpose: with no profile
        // consulted, the generator must not add unrequested mount motion.
        var parkAfter = policies?.PostFlatParkMount ?? false;
        var captureSettings = await ModalCaptureSettingsByFilterAsync(conn, sessionId, ct);
        var steps = new List<GeneratedFlatStepDto>(session.FiltersUsed.Count);
        var specs = new List<FlatStepSpec>(session.FiltersUsed.Count);
        var skySpecs = new List<SkyFlatStepSpec>(session.FiltersUsed.Count);
        var total = 0;
        foreach (var filter in session.FiltersUsed) {
            steps.Add(new GeneratedFlatStepDto(
                FilterName: filter.FilterName,
                FrameCount: frameCount,
                TargetAdu: targetAdu,
                PanelBrightness: null));
            var settings = captureSettings.GetValueOrDefault(filter.FilterName);
            var specFilter = filter.FilterName == NoFilter ? null : filter.FilterName;
            if (sky) {
                skySpecs.Add(new SkyFlatStepSpec(
                    FilterName: specFilter,
                    FrameCount: frameCount,
                    TargetAdu: targetAdu,
                    TargetAduTolerancePct: tolerancePct,
                    StopAtMaxAdu: policies?.SkyFlatStopAtMaxAdu is double mx && mx > targetAdu ? mx : DefaultSkyStopAtMaxAdu,
                    StopAtMinAdu: policies?.SkyFlatStopAtMinAdu is double mn && mn >= 0 && mn < targetAdu ? mn : DefaultSkyStopAtMinAdu,
                    Gain: settings?.Gain,
                    Offset: settings?.Offset,
                    FocuserPosition: settings?.FocuserPosition));
            } else {
                specs.Add(new FlatStepSpec(
                    FilterName: specFilter,
                    FrameCount: frameCount,
                    TargetAdu: targetAdu,
                    TargetAduTolerancePct: tolerancePct,
                    Gain: settings?.Gain,
                    Offset: settings?.Offset,
                    FocuserPosition: settings?.FocuserPosition));
            }
            total += frameCount;
        }

        var name = sky ? $"Sky flats — {session.TargetName}" : $"Flats — {session.TargetName}";

        // §39.5: materialize the plan into a runnable §38 sequence unless the caller asked for the
        // plan alone. The persisted body is the same NINA-verbatim tree every stored sequence uses,
        // so the WILMA editor renders it and POST /sequences/{id}/start runs it as-is.
        Guid? generatedId = null;
        if (!request.GenerateOnly && _sequences is not null) {
            var body = sky
                ? CalibrationSequenceBuilder.BuildSkyFlatsBody(name, skySpecs,
                    new SkyFlatEnvelope(
                        SunAltitudeDeg: policies?.SkyFlatSunAltitude ?? DefaultSkySunAltitude,
                        AzimuthDeg: policies?.SkyFlatTargetAzimuth ?? DefaultSkyAzimuth,
                        AltitudeDeg: policies?.SkyFlatTargetAltitude ?? DefaultSkyAltitude),
                    parkMountAfter: parkAfter)
                : CalibrationSequenceBuilder.BuildMatchingFlatsBody(name, specs, parkMountAfter: parkAfter);
            var created = await _sequences.CreateAsync(new SequenceCreateRequestDto(
                Name: name,
                Description: $"Matching flats generated from the {session.SessionStartUtc:yyyy-MM-dd} {session.TargetName} session ({sessionId:D}).",
                Body: body,
                TemplateOrigin: "calibration:matching-flats"), idempotencyKey, ct);
            generatedId = created.Id;
        }

        return new GeneratedFlatSequenceDto(
            SourceSessionId: sessionId,
            GeneratedSequenceId: generatedId,
            GeneratedSequenceName: name,
            TotalFlatFrames: total,
            Steps: steps);
    }

    private sealed record ModalCaptureSettings(int? Gain, int? Offset, int? FocuserPosition);

    // The per-filter capture settings the session's lights actually used, so the generated flats
    // replay them (§39.5: gain/offset must match for the calibration to apply; focus per filter so
    // dust shadows align). "Modal" = the most frequent (gain, offset, focuser) combination per
    // filter — a mid-session change loses the minority combination, which is the standard
    // one-flat-set-per-filter trade-off. The trailing ORDER BY terms are a deterministic
    // tiebreaker: an even split must not pick a different combo on different runs.
    private static async Task<Dictionary<string, ModalCaptureSettings>> ModalCaptureSettingsByFilterAsync(
            SqliteConnection conn, Guid sessionId, CancellationToken ct) {
        var result = new Dictionary<string, ModalCaptureSettings>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT filter_name, gain, "offset", focuser_position, COUNT(*) AS c
            FROM frames
            WHERE frame_type = 'light' AND session_id = $sid
            GROUP BY filter_name, gain, "offset", focuser_position
            ORDER BY c DESC, gain, "offset", focuser_position;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var filter = await reader.IsDBNullAsync(0, ct) ? NoFilter : reader.GetString(0);
            if (result.ContainsKey(filter)) {
                continue; // rows are frequency-ordered; the first per filter is the modal combo
            }
            result[filter] = new ModalCaptureSettings(
                Gain: await reader.IsDBNullAsync(1, ct) ? null : reader.GetInt32(1),
                Offset: await reader.IsDBNullAsync(2, ct) ? null : reader.GetInt32(2),
                FocuserPosition: await reader.IsDBNullAsync(3, ct) ? null : reader.GetInt32(3));
        }
        return result;
    }

    private static async Task<CalibrationSessionDto?> BuildSessionDtoAsync(SqliteConnection conn, Guid sessionId, CancellationToken ct) {
        var sid = sessionId.ToString();

        // Session header from its LIGHT frames. Null row (no lights) ⇒ not a calibration session.
        string targetName;
        int lightCount;
        DateTimeOffset start, end;
        await using (var cmd = conn.CreateCommand()) {
            // MIN(target_name) picks the lexicographically-first target for a multi-target session (mosaic /
            // back-to-back nights). Acceptable for v0.0.1 — one representative name; a fuller multi-target
            // summary is a future refinement.
            cmd.CommandText = """
                SELECT COUNT(*), MIN(captured_utc), MAX(captured_utc), MIN(target_name)
                FROM frames
                WHERE frame_type = 'light' AND session_id = $sid;
                """;
            cmd.Parameters.AddWithValue("$sid", sid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            // COUNT(*) is never NULL (0 for an empty set), so the zero check is the real "no lights" guard.
            if (!await reader.ReadAsync(ct) || reader.GetInt32(0) == 0) {
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

        // Calibration coverage is GLOBAL by design: flats/darks are a shared library reused across nights, so
        // the right-hand side of each EXCEPT scans all flat/dark frames (not just this session's). Only the
        // light requirements (left side) are session-scoped via $sid.
        // No trailing ';' — these are embedded as subqueries in AllCoveredAsync's SELECT COUNT(*) FROM (...).
        var flatsAvailable = await AllCoveredAsync(conn, sid, """
            SELECT DISTINCT filter_name FROM frames WHERE frame_type = 'light' AND session_id = $sid
            EXCEPT
            SELECT DISTINCT filter_name FROM frames WHERE frame_type = 'flat'
            """, ct);

        // Darks match a light by (exposure, gain, temperature). Temperature is bucketed to the nearest whole
        // degree via ROUND(COALESCE(temperature_c, 0), 0): a cooled camera regulates to its set-point within a
        // fraction of a degree, so same-set-point lights and darks land in the same bucket, while a dark shot at
        // a different temperature correctly fails to match. An uncooled camera (no temperature sensor) records
        // NULL since the sentinel pass; legacy rows may hold the old 0.0 sentinel — COALESCE buckets NULL with 0
        // so uncooled lights/darks keep matching across both generations, exactly the documented semantics.
        var darksAvailable = await AllCoveredAsync(conn, sid, """
            SELECT DISTINCT exposure_seconds, gain, ROUND(COALESCE(temperature_c, 0), 0) FROM frames WHERE frame_type = 'light' AND session_id = $sid
            EXCEPT
            SELECT DISTINCT exposure_seconds, gain, ROUND(COALESCE(temperature_c, 0), 0) FROM frames WHERE frame_type = 'dark'
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

    // The app writes captured_utc as ISO-8601 "O", but a single corrupt/hand-edited row shouldn't 500 the
    // whole session list — fall back to the epoch sentinel so the session still renders.
    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : DateTimeOffset.UnixEpoch;
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
