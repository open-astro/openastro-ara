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
/// (NULL temperature — an uncooled camera with no sensor — matches NULL); a bias matches a light by
/// <c>(gain, offset, binning)</c>. The dark-library <em>build</em> is a
/// separate, guider/sequencer-gated concern (<see cref="IDarkLibraryService"/>);
/// this service only reports availability + generates the matching-flats plan.
/// </summary>
public sealed class SqliteCalibrationService : ICalibrationService {
    // Fallback defaults when no profile store is wired (test/bootstrap contexts) — production
    // reads the §48.7 flat_panel policy fields off SafetyPoliciesDto instead.
    private const int DefaultFlatFrames = 20;
    // Bias frames are cheap read-outs; 50 averages their read noise well below a light's.
    internal const int DefaultBiasFrames = 50;
    // The dark library form's default per combination.
    internal const int DefaultDarkFrames = 30;
    private const double DefaultTargetPercent = 50;
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
    // 7N+1 per page. This runs the same 7 aggregations ONCE each over the whole page's id set
    // (session_id IN (...)), so a page costs 8 queries total regardless of size.
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

        // 3) Flats coverage per filter (setup-aware: filter + rotator angle; see ApplyFlatCoverageAsync).
        await ApplyFlatCoverageAsync(conn, $"session_id IN ({placeholders})", BindIds, filtersBySession, ct);

        // 4) Darks coverage — the sessions with at least one UNCOVERED requirement. Coverage is GLOBAL
        // (darks are a shared library), expressed as NOT EXISTS with SQLite's null-safe IS. The inner
        // DISTINCT probes once per requirement combo, not once per raw frame row (a 500-frame
        // single-filter session probes once, not 500 times).
        // Darks match by (exposure, gain, temperature bucketed to the nearest whole degree; NULL buckets
        // with 0 for uncooled cameras across sentinel generations — see BuildSessionDtoAsync's remarks).
        var darksUncovered = await UncoveredSessionsAsync(conn, $"""
            SELECT DISTINCT l.session_id
            FROM (SELECT DISTINCT session_id, exposure_seconds, gain,
                         ROUND(COALESCE(temperature_c, 0), 0) AS temp_bucket
                  FROM frames
                  WHERE frame_type = 'light' AND session_id IN ({placeholders})) l
            WHERE NOT EXISTS (
                  SELECT 1 FROM frames d
                  WHERE d.frame_type = 'dark'
                    AND d.exposure_seconds IS l.exposure_seconds
                    AND d.gain IS l.gain
                    AND ROUND(COALESCE(d.temperature_c, 0), 0) IS l.temp_bucket)
            """, BindIds, ct);

        // 4b) Bias coverage — global like the darks, keyed by (gain, offset, binning). A row from
        // before binning was recorded counts as 1×1, the overwhelmingly common case.
        var biasUncovered = await UncoveredSessionsAsync(conn, $"""
            SELECT DISTINCT l.session_id
            FROM (SELECT DISTINCT session_id, gain, "offset", COALESCE(bin, 1) AS bin
                  FROM frames
                  WHERE frame_type = 'light' AND session_id IN ({placeholders})) l
            WHERE NOT EXISTS (
                  SELECT 1 FROM frames b
                  WHERE b.frame_type = 'bias'
                    AND b.gain IS l.gain
                    AND b."offset" IS l."offset"
                    AND COALESCE(b.bin, 1) IS l.bin)
            """, BindIds, ct);

        // 5) The lights' cooler set-point (modal per session).
        var setpoints = await ModalCoolerSetpointsAsync(conn, $"session_id IN ({placeholders})", BindIds, ct);

        // 6) Owning profile ids.
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
                MatchingFlatsAvailable: FlatsCoverAll(filtersBySession.GetValueOrDefault(sid)),
                MatchingDarksAvailable: !darksUncovered.Contains(sid),
                ProfileId: profiles.GetValueOrDefault(sid),
                CoolerSetpointC: setpoints.TryGetValue(sid, out var sp) ? sp : null,
                MatchingBiasAvailable: !biasUncovered.Contains(sid)));
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
        var policyTarget = sky ? policies?.SkyFlatTargetAdu : null;
        var frameCount = request.OverrideFrameCount is int n && n > 0
            ? n
            : policyFrames is int p && p > 0 ? p : (sky ? DefaultSkyFlatFrames : DefaultFlatFrames);
        // Panel flats aim at a share of the camera's own range and solve their exposure and
        // brightness at run time (FlatPanelFlats, TargetAdu 0); an explicit override is absolute.
        // Sky flats keep their absolute §48.7 target and stop window.
        var targetAdu = request.OverrideTargetAdu is int o && o > 0
            ? o
            : sky ? (policyTarget is int t && t > 0 ? t : DefaultSkyTargetAdu) : 0;
        var targetPercent = policies?.FlatTargetPercent is double pct && pct > 0 && pct < 100 ? pct : DefaultTargetPercent;
        var tolerancePct = policies?.FlatTargetAduTolerancePct is double tol && tol > 0
            ? tol : DefaultTargetAduTolerancePct;
        // NB: the park rides only the persisted sequence body — GeneratedFlatSequenceDto.Steps
        // is the per-filter capture plan and deliberately doesn't model non-capture steps.
        // Mount motion is never added unasked: a park the user didn't choose for THIS run swung a
        // mount on a bench at the desk next to it. The request's answer wins; only the unattended
        // end-of-night path (null) reads the profile, and a store-less context never parks.
        var parkAfter = request.ParkMountAfter ?? policies?.PostFlatParkMount ?? false;
        var cooling = ResolveFlatCooling(request, session.CoolerSetpointC);
        var captureSettings = await ModalCaptureSettingsByFilterAsync(conn, sessionId, ct);
        var steps = new List<GeneratedFlatStepDto>(session.FiltersUsed.Count);
        var specs = new List<FlatStepSpec>(session.FiltersUsed.Count);
        var skySpecs = new List<SkyFlatStepSpec>(session.FiltersUsed.Count);
        var total = 0;
        foreach (var filter in session.FiltersUsed) {
            // Flats are reused while the setup is unchanged: only shoot what isn't covered yet.
            if (!request.IncludeFlats || (filter.MatchingFlatFrames > 0 && !request.IncludeCoveredFilters)) {
                continue;
            }
            steps.Add(new GeneratedFlatStepDto(
                FilterName: filter.FilterName,
                FrameCount: frameCount,
                TargetAdu: targetAdu > 0 ? targetAdu : null,
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
                    FocuserPosition: settings?.FocuserPosition,
                    RotatorMechanicalDeg: settings?.RotatorMechanicalDeg));
            } else {
                specs.Add(new FlatStepSpec(
                    FilterName: specFilter,
                    FrameCount: frameCount,
                    TargetAdu: targetAdu,
                    TargetAduTolerancePct: tolerancePct,
                    Gain: settings?.Gain,
                    Offset: settings?.Offset,
                    FocuserPosition: settings?.FocuserPosition,
                    RotatorMechanicalDeg: settings?.RotatorMechanicalDeg,
                    TargetPercent: targetPercent));
            }
            total += frameCount;
        }

        var biasFrames = request.BiasFrameCount is int bc && bc > 0 ? bc : DefaultBiasFrames;
        var bias = request.IncludeBias ? await UncoveredBiasAsync(conn, sessionId, biasFrames, ct) : [];
        var totalBias = bias.Sum(b => b.FrameCount);
        var darkFrames = request.DarkFrameCount is int dc && dc > 0 ? dc : DefaultDarkFrames;
        var darks = request.IncludeDarks ? await UncoveredDarksAsync(conn, sessionId, darkFrames, ct) : [];
        var totalDarks = darks.Sum(d => d.FrameCount);
        var afterFlats = new AfterFlatsSpec(bias, darks, request.PauseForLensCap);

        // The frames file next to the lights they calibrate: {target}/{night}/Flat, …/Dark, …/Bias.
        // The night is the lights' observing night on the daemon's clock, as the lights were named.
        var filing = string.IsNullOrWhiteSpace(session.TargetName) ? null
            : new CalibrationFilingSpec(session.TargetName,
                DateOnly.FromDateTime(session.SessionStartUtc.ToLocalTime().AddHours(-12).DateTime));
        var name = $"{RunTitle(sky, steps.Count > 0, bias.Count > 0, darks.Count > 0)} — {session.TargetName}";

        // §39.5: materialize the plan into a runnable §38 sequence unless the caller asked for the
        // plan alone. The persisted body is the same $type tree every stored sequence uses,
        // so the WILMA editor renders it and POST /sequences/{id}/start runs it as-is.
        Guid? generatedId = null;
        if (!request.GenerateOnly && _sequences is not null && (steps.Count > 0 || bias.Count > 0 || darks.Count > 0)) {
            // A run with no flats is a bias/darks run: never the sky body's twilight wait and slew.
            var body = sky && skySpecs.Count > 0
                ? CalibrationSequenceBuilder.BuildSkyFlatsBody(name, skySpecs,
                    new SkyFlatEnvelope(
                        SunAltitudeDeg: policies?.SkyFlatSunAltitude ?? DefaultSkySunAltitude,
                        AzimuthDeg: policies?.SkyFlatTargetAzimuth ?? DefaultSkyAzimuth,
                        AltitudeDeg: policies?.SkyFlatTargetAltitude ?? DefaultSkyAltitude),
                    parkMountAfter: parkAfter, cooling: cooling, afterFlats: afterFlats, filing: filing)
                : CalibrationSequenceBuilder.BuildMatchingFlatsBody(name, specs, parkMountAfter: parkAfter, cooling: cooling, afterFlats: afterFlats, filing: filing);
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
            Steps: steps,
            TotalBiasFrames: totalBias,
            TotalDarkFrames: totalDarks);
    }

    // "Flats", "Sky flats, bias & darks", "Darks"...: what the run shoots, for its sequence name.
    private static string RunTitle(bool sky, bool flats, bool bias, bool darks) {
        var parts = new List<string>(3);
        if (flats) {
            parts.Add(sky ? "Sky flats" : "Flats");
        }
        if (bias) {
            parts.Add(parts.Count == 0 ? "Bias" : "bias");
        }
        if (darks) {
            parts.Add(parts.Count == 0 ? "Darks" : "darks");
        }
        return parts.Count switch {
            0 => sky ? "Sky flats" : "Flats",
            1 => parts[0],
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} & {parts[^1]}",
        };
    }

    // The cooler around the flats (see MatchingFlatsRequestDto's remarks for the request rules).
    // Flats are short, so cooling is not about their own dark current: it puts the sensor back
    // where the lights were (an imaging run that warmed up at its end leaves it warm), so the
    // flats calibrate with the lights' darks/dark-flats. The warm-up after follows the profile's
    // "warm up at session end", which the imaging run itself honours.
    private FlatCooling? ResolveFlatCooling(MatchingFlatsRequestDto request, double? lightsSetpointC) {
        if (request.CoolCamera == false) {
            return null;
        }
        var defaults = _profile?.GetImagingDefaults();
        var target = request.CoolToTemperatureC is double t && double.IsFinite(t)
            ? t
            : lightsSetpointC ?? (request.CoolCamera == true ? defaults?.CoolerTargetC : null);
        return target is double c ? new FlatCooling(c, WarmAfter: defaults?.WarmupAtSessionEnd ?? false) : null;
    }

    private sealed record ModalCaptureSettings(int? Gain, int? Offset, int? FocuserPosition, double? RotatorMechanicalDeg);

    // The per-filter capture settings the session's lights actually used, so the generated flats
    // replay them (§39.5: gain/offset must match for the calibration to apply; focus per filter so
    // dust shadows align; rotator angle so the camera turns back with its dust and vignetting).
    // The angle is rounded to 0.1° so driver read-back jitter doesn't split one position into
    // several combinations. "Modal" = the most frequent (gain, offset, focuser, rotator) combination per
    // filter — a mid-session change loses the minority combination, which is the standard
    // one-flat-set-per-filter trade-off. The trailing ORDER BY terms are a deterministic
    // tiebreaker: an even split must not pick a different combo on different runs.
    private static async Task<Dictionary<string, ModalCaptureSettings>> ModalCaptureSettingsByFilterAsync(
            SqliteConnection conn, Guid sessionId, CancellationToken ct) {
        var result = new Dictionary<string, ModalCaptureSettings>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT filter_name, gain, "offset", focuser_position,
                   ROUND(rotator_mechanical_deg, 1) AS rotator, COUNT(*) AS c
            FROM frames
            WHERE frame_type = 'light' AND session_id = $sid
            GROUP BY filter_name, gain, "offset", focuser_position, rotator
            ORDER BY c DESC, gain, "offset", focuser_position, rotator;
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
                FocuserPosition: await reader.IsDBNullAsync(3, ct) ? null : reader.GetInt32(3),
                RotatorMechanicalDeg: await reader.IsDBNullAsync(4, ct) ? null : reader.GetDouble(4));
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

        // Calibration coverage is GLOBAL by design: flats/darks are a shared library reused across nights.
        var byFilter = new Dictionary<string, List<CalibrationFilterSummaryDto>>(StringComparer.Ordinal) { [sid] = filters };
        await ApplyFlatCoverageAsync(conn, "session_id = $sid", cmd => cmd.Parameters.AddWithValue("$sid", sid), byFilter, ct);
        filters = byFilter[sid];
        var flatsAvailable = FlatsCoverAll(filters);

        // Darks match a light by (exposure, gain, temperature). Temperature is bucketed to the nearest whole
        // degree via ROUND(COALESCE(temperature_c, 0), 0): a cooled camera regulates to its set-point within a
        // fraction of a degree, so same-set-point lights and darks land in the same bucket, while a dark shot at
        // a different temperature correctly fails to match. An uncooled camera (no temperature sensor) records
        // NULL since the sentinel pass; legacy rows may hold the old 0.0 sentinel — COALESCE buckets NULL with 0
        // so uncooled lights/darks keep matching across both generations, exactly the documented semantics.
        // No trailing ';' — this is embedded as a subquery in AllCoveredAsync's SELECT COUNT(*) FROM (...).
        var darksAvailable = await AllCoveredAsync(conn, sid, """
            SELECT DISTINCT exposure_seconds, gain, ROUND(COALESCE(temperature_c, 0), 0) FROM frames WHERE frame_type = 'light' AND session_id = $sid
            EXCEPT
            SELECT DISTINCT exposure_seconds, gain, ROUND(COALESCE(temperature_c, 0), 0) FROM frames WHERE frame_type = 'dark'
            """, ct);

        var biasAvailable = await AllCoveredAsync(conn, sid, BiasRequirementsSql, ct);

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
            ProfileId: profileId,
            CoolerSetpointC: (await ModalCoolerSetpointsAsync(conn, "session_id = $sid",
                cmd => cmd.Parameters.AddWithValue("$sid", sid), ct)).TryGetValue(sid, out var sp) ? sp : null,
            MatchingBiasAvailable: biasAvailable);
    }

    // The session's (gain, offset, binning) combinations no bias frame covers yet. EXCEPT treats
    // NULLs as equal, so camera-default lights match camera-default biases. No trailing ';' — it is
    // embedded as a subquery.
    private const string BiasRequirementsSql = """
        SELECT DISTINCT gain, "offset", COALESCE(bin, 1) FROM frames WHERE frame_type = 'light' AND session_id = $sid
        EXCEPT
        SELECT DISTINCT gain, "offset", COALESCE(bin, 1) FROM frames WHERE frame_type = 'bias'
        """;

    // The session's (exposure, gain, temperature) combinations no dark covers yet, with the gain,
    // offset and binning the lights used for each (modal, so one set per combination), and the
    // temperature to shoot their darks at: the set-point the lights ran at, else (lights from before
    // the catalog recorded it, or taken with the cooler off) their recorded sensor temperature, which
    // is the bucket the darks have to land in.
    private static async Task<List<DarkStepSpec>> UncoveredDarksAsync(SqliteConnection conn, Guid sessionId, int frameCount, CancellationToken ct) {
        var result = new List<DarkStepSpec>();
        var seen = new HashSet<(double, int?, double)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.exposure_seconds, l.gain, ROUND(COALESCE(l.temperature_c, 0), 0) AS temp_bucket,
                   l."offset", COALESCE(l.bin, 1) AS bin, COUNT(*) AS c, MAX(l.cooler_setpoint_c) AS setpoint,
                   MAX(ROUND(l.temperature_c, 0)) AS recorded_temp
            FROM frames l
            WHERE l.frame_type = 'light' AND l.session_id = $sid
              AND NOT EXISTS (
                  SELECT 1 FROM frames d
                  WHERE d.frame_type = 'dark'
                    AND d.exposure_seconds IS l.exposure_seconds
                    AND d.gain IS l.gain
                    AND ROUND(COALESCE(d.temperature_c, 0), 0) IS ROUND(COALESCE(l.temperature_c, 0), 0))
            GROUP BY l.exposure_seconds, l.gain, temp_bucket, l."offset", bin
            ORDER BY l.exposure_seconds, l.gain, temp_bucket, c DESC, l."offset", bin;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var exposure = await reader.IsDBNullAsync(0, ct) ? 0 : reader.GetDouble(0);
            var gain = await reader.IsDBNullAsync(1, ct) ? (int?)null : reader.GetInt32(1);
            if (exposure <= 0 || !seen.Add((exposure, gain, reader.GetDouble(2)))) {
                continue; // rows are frequency-ordered within a combination; the first is the modal offset/bin
            }
            result.Add(new DarkStepSpec(
                ExposureSeconds: exposure,
                Gain: gain,
                FrameCount: frameCount,
                Offset: await reader.IsDBNullAsync(3, ct) ? null : reader.GetInt32(3),
                Bin: Math.Max(1, reader.GetInt32(4)),
                CoolToC: !await reader.IsDBNullAsync(6, ct) ? reader.GetDouble(6)
                    : !await reader.IsDBNullAsync(7, ct) ? reader.GetDouble(7)
                    : null));
        }
        return result;
    }

    private static async Task<List<BiasStepSpec>> UncoveredBiasAsync(SqliteConnection conn, Guid sessionId, int frameCount, CancellationToken ct) {
        var result = new List<BiasStepSpec>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BiasRequirementsSql + " ORDER BY 1, 2, 3;";
        cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            result.Add(new BiasStepSpec(
                Gain: await reader.IsDBNullAsync(0, ct) ? null : reader.GetInt32(0),
                Offset: await reader.IsDBNullAsync(1, ct) ? null : reader.GetInt32(1),
                Bin: Math.Max(1, reader.GetInt32(2)),
                FrameCount: frameCount));
        }
        return result;
    }

    /// <summary>Rotator angles closer than this are the same camera orientation (a CAA reads back
    /// ±0.01°; a manual re-set lands within a few tenths).</summary>
    internal const double FlatRotatorToleranceDeg = 1.0;

    private static bool FlatsCoverAll(IReadOnlyList<CalibrationFilterSummaryDto>? filters) =>
        filters is { Count: > 0 } && filters.All(f => f.MatchingFlatFrames > 0);

    private sealed record FlatGroup(double? RotatorDeg, int Count, DateTimeOffset Newest);

    // Flats calibrate lights taken through the same optical train, so a set is reused across nights
    // until something changes. Ara can see two parts of that train: the filter and the rotator angle
    // (dust and vignetting turn with the camera). For each session's filter it finds the lights'
    // modal angle and counts the flats with the same filter at the same angle. An angle missing on
    // either side (frames from before it was recorded) can't disprove a match: those flats count,
    // with a note saying the match is assumed. Flats that exist only at other angles don't count,
    // and the note says where they were taken. Focus is not compared — per-filter focus offsets are
    // normal and the generator replays the lights' focus anyway.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The 'sessionFilter' argument is a locally-composed literal (a bound $sid or a generated placeholder list); every value is bound as a parameter.")]
    private static async Task ApplyFlatCoverageAsync(SqliteConnection conn, string sessionFilter, Action<SqliteCommand> bind,
            Dictionary<string, List<CalibrationFilterSummaryDto>> filtersBySession, CancellationToken ct) {
        var lightAngles = new Dictionary<(string Sid, string Filter), double?>();
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = $"""
                SELECT session_id, filter_name, ROUND(rotator_mechanical_deg, 1) AS rotator, COUNT(*) AS c
                FROM frames
                WHERE frame_type = 'light' AND {sessionFilter}
                GROUP BY session_id, filter_name, rotator
                ORDER BY session_id, filter_name, c DESC, rotator;
                """;
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var key = (reader.GetString(0), await reader.IsDBNullAsync(1, ct) ? NoFilter : reader.GetString(1));
                lightAngles.TryAdd(key, await reader.IsDBNullAsync(2, ct) ? null : reader.GetDouble(2)); // first = modal
            }
        }
        var flats = new Dictionary<string, List<FlatGroup>>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                SELECT filter_name, ROUND(rotator_mechanical_deg, 1), COUNT(*), MAX(captured_utc)
                FROM frames WHERE frame_type = 'flat'
                GROUP BY filter_name, ROUND(rotator_mechanical_deg, 1);
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var filter = await reader.IsDBNullAsync(0, ct) ? NoFilter : reader.GetString(0);
                if (!flats.TryGetValue(filter, out var groups)) {
                    flats[filter] = groups = [];
                }
                groups.Add(new FlatGroup(
                    await reader.IsDBNullAsync(1, ct) ? null : reader.GetDouble(1),
                    reader.GetInt32(2),
                    ParseUtc(reader.GetString(3))));
            }
        }
        foreach (var (sid, list) in filtersBySession) {
            for (var i = 0; i < list.Count; i++) {
                var f = list[i];
                var angle = lightAngles.GetValueOrDefault((sid, f.FilterName));
                var (count, newest, note) = FlatCoverage(angle, flats.GetValueOrDefault(f.FilterName));
                list[i] = f with { MatchingFlatFrames = count, MatchingFlatsNewestUtc = newest, FlatsNote = note };
            }
        }
    }

    internal static double AngleDifferenceDeg(double a, double b) {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static (int Count, DateTimeOffset? Newest, string? Note) FlatCoverage(double? lightAngle, List<FlatGroup>? groups) {
        if (groups is not { Count: > 0 }) {
            return (0, null, null);
        }
        // Verified matches (both angles known and equal) win; unverifiable ones only when there are none.
        var verified = groups.Where(g => lightAngle is double l && g.RotatorDeg is double r
            && AngleDifferenceDeg(l, r) <= FlatRotatorToleranceDeg).ToList();
        if (verified.Count > 0) {
            return (verified.Sum(g => g.Count), verified.Max(g => g.Newest), null);
        }
        var assumed = groups.Where(g => lightAngle is null || g.RotatorDeg is null).ToList();
        if (assumed.Count > 0) {
            return (assumed.Sum(g => g.Count), assumed.Max(g => g.Newest),
                "rotator angle not recorded for these frames — assumes the camera wasn't rotated");
        }
        var nearest = groups.OrderBy(g => AngleDifferenceDeg(lightAngle!.Value, g.RotatorDeg!.Value)).First();
        return (0, null, string.Create(CultureInfo.InvariantCulture,
            $"flats exist at rotator {nearest.RotatorDeg:0.#}°, the lights were at {lightAngle:0.#}°"));
    }

    // The set-point most of each session's cooled lights ran at, to 0.1 °C (a driver can read back
    // −9.9999). Lights taken warm record NULL and don't vote, so a session that cooled for only part
    // of the night still cools its flats. Ties break toward the colder set-point, deterministically.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The 'sessionFilter' argument is a locally-composed literal (a bound $sid or a generated placeholder list); every value is bound as a parameter.")]
    private static async Task<Dictionary<string, double>> ModalCoolerSetpointsAsync(
            SqliteConnection conn, string sessionFilter, Action<SqliteCommand> bind, CancellationToken ct) {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT session_id, ROUND(cooler_setpoint_c, 1) AS setpoint, COUNT(*) AS c
            FROM frames
            WHERE frame_type = 'light' AND cooler_setpoint_c IS NOT NULL AND {sessionFilter}
            GROUP BY session_id, setpoint
            ORDER BY session_id, c DESC, setpoint;
            """;
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            result.TryAdd(reader.GetString(0), reader.GetDouble(1)); // first row per session is the modal set-point
        }
        return result;
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
