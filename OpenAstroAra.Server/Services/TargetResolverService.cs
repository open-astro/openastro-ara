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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

public interface ITargetResolver {
    /// <summary>Resolve one name: the offline index first, then the daemon's cache of earlier Simbad answers,
    /// then Sesame itself. Never throws for a name that merely cannot be found or reached.</summary>
    Task<TargetResolveResponseDto> ResolveAsync(string name, CancellationToken ct);
}

/// <summary>
/// #1267 — the target name resolver behind <c>GET /targets/resolve</c>. Offline-first: the bundled index
/// answers instantly with no internet; a miss asks Sesame (5 s) and keeps the answer in
/// <c>target_resolutions</c>, so a target resolved at home is still there at the dark site. "Not found" is
/// never cached (Simbad grows, and a typo should not be remembered), and an unreachable Sesame reports
/// <c>offline</c> so the client can say "needs internet" instead of spinning.
/// </summary>
public sealed partial class TargetResolverService : ITargetResolver {

    public const string StatusResolved = "resolved";
    public const string StatusNotFound = "not_found";
    public const string StatusOffline = "offline";
    public const string SourceIndex = "index";
    public const string SourceCache = "cache";
    public const string SourceSimbad = "simbad";

    private readonly TargetNameIndex _index;
    private readonly ISesameClient? _sesame;
    private readonly IAraDatabase? _db;
    private readonly ILogger<TargetResolverService> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public TargetResolverService(TargetNameIndex index, ISesameClient? sesame, IAraDatabase? db,
            ILogger<TargetResolverService>? logger = null, Func<DateTimeOffset>? clock = null) {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _sesame = sesame;
        _db = db;
        _logger = logger ?? NullLogger<TargetResolverService>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<TargetResolveResponseDto> ResolveAsync(string name, CancellationToken ct) {
        var query = (name ?? string.Empty).Trim();
        if (query.Length == 0) {
            return Miss(query, StatusNotFound, "Type a name.");
        }
        if (_index.Find(query, ct) is { } hit) {
            return new TargetResolveResponseDto(query, StatusResolved, SourceIndex, hit.MainId,
                hit.Aliases.Where(a => a != hit.MainId).ToArray(), hit.RaDeg, hit.DecDeg, hit.ObjectType, hit.Magnitude, null);
        }
        var key = TargetNameIndex.TightKey(query);
        if (await ReadCacheAsync(key, ct).ConfigureAwait(false) is { } cached) {
            return new TargetResolveResponseDto(query, StatusResolved, SourceCache, cached.MainId, cached.Aliases,
                cached.RaDeg, cached.DecDeg, cached.ObjectType, cached.Magnitude, null);
        }
        if (_sesame is null) {
            return Miss(query, StatusOffline, "Not in the offline index, and this rig has no Simbad access.");
        }
        SesameResult? result;
        try {
            result = await _sesame.ResolveAsync(query, ct).ConfigureAwait(false);
        } catch (SesameUnavailableException) {
            return Miss(query, StatusOffline, "Not in the offline index. Simbad could not be reached: this name needs internet.");
        }
        if (result is null) {
            return Miss(query, StatusNotFound, "Not in the offline index, and Simbad knows no object by that name.");
        }
        await WriteCacheAsync(key, query, result, ct).ConfigureAwait(false);
        var queryText = GuiderCameraReconcile.ForLog(query);
        var mainText = GuiderCameraReconcile.ForLog(result.MainId);
        LogResolved(queryText, mainText);
        return new TargetResolveResponseDto(query, StatusResolved, SourceSimbad, result.MainId, result.Aliases,
            result.RaDeg, result.DecDeg, result.ObjectType, result.Magnitude, null);
    }

    private static TargetResolveResponseDto Miss(string query, string status, string message) =>
        new(query, status, string.Empty, null, [], null, null, null, null, message);

    // ---- cache ----

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The cache is an accelerator: a locked or missing database must fall through to Sesame, never fail the lookup. Log-and-recover.")]
    private async Task<SesameResult?> ReadCacheAsync(string key, CancellationToken ct) {
        if (_db is null || key.Length == 0) {
            return null;
        }
        try {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT main_id, aliases_json, ra_deg, dec_deg, object_type, magnitude FROM target_resolutions WHERE query_key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
                return null;
            }
            var aliases = JsonSerializer.Deserialize(reader.GetString(1), AraJsonSerializerContext.Default.IReadOnlyListString) ?? [];
            var type = await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4);
            double? mag = await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetDouble(5);
            return new SesameResult(reader.GetString(0), aliases, reader.GetDouble(2), reader.GetDouble(3), type, mag);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogCacheFailed(ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The cache is an accelerator: a failed write must not fail a lookup that already succeeded. Log-and-recover.")]
    private async Task WriteCacheAsync(string key, string query, SesameResult result, CancellationToken ct) {
        if (_db is null || key.Length == 0) {
            return;
        }
        try {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO target_resolutions (query_key, query, main_id, aliases_json, ra_deg, dec_deg, object_type, magnitude, resolved_at)
                VALUES ($k, $q, $m, $a, $ra, $dec, $t, $mag, $at)
                ON CONFLICT(query_key) DO UPDATE SET query = excluded.query, main_id = excluded.main_id, aliases_json = excluded.aliases_json,
                    ra_deg = excluded.ra_deg, dec_deg = excluded.dec_deg, object_type = excluded.object_type, magnitude = excluded.magnitude,
                    resolved_at = excluded.resolved_at;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$m", result.MainId);
            cmd.Parameters.AddWithValue("$a", JsonSerializer.Serialize(result.Aliases, AraJsonSerializerContext.Default.IReadOnlyListString));
            cmd.Parameters.AddWithValue("$ra", result.RaDeg);
            cmd.Parameters.AddWithValue("$dec", result.DecDeg);
            cmd.Parameters.AddWithValue("$t", (object?)result.ObjectType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mag", result.Magnitude is { } mag ? mag : DBNull.Value);
            cmd.Parameters.AddWithValue("$at", _clock().ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogCacheFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Target '{Query}' resolved by Simbad to '{MainId}' and cached (#1267)")]
    private partial void LogResolved(string query, string mainId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Target resolution cache unavailable (#1267)")]
    private partial void LogCacheFailed(Exception ex);
}
