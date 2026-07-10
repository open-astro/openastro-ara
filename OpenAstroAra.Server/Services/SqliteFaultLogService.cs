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
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.5 SQLite-backed <see cref="IFaultLogService"/> over the <c>faults</c> table.
/// Detection (the hub) and reaction (the §42.3 episode) write independently and
/// asynchronously, so both write paths upsert against the fault event's natural key
/// (equipment_type + fault_type + detected_at + equipment_id) and a single in-proc
/// write gate serializes them — whichever lands first creates the row, the other
/// completes it. <c>session_id</c> is stamped from
/// <see cref="ActiveRunSessionRegistry"/> at detection time (null outside a run);
/// <c>affected_frames</c> stays empty until the §42.6 frame-correlation slice.
/// </summary>
public sealed partial class SqliteFaultLogService : IFaultLogService, IDisposable {

    private readonly IAraDatabase _db;
    private readonly ILogger<SqliteFaultLogService> _logger;
    private readonly ActiveRunSessionRegistry? _sessions;
    // Serializes the two upsert paths (detection insert vs action update) so the
    // exists-check + write pair can't interleave. Reads don't take it.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteFaultLogService(
            IAraDatabase db,
            ILogger<SqliteFaultLogService>? logger = null,
            ActiveRunSessionRegistry? sessions = null) {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? NullLogger<SqliteFaultLogService>.Instance;
        _sessions = sessions;
    }

    public async Task RecordFaultAsync(EquipmentFaultEvent fault, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(fault);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            await using var conn = _db.OpenConnection();
            if (await FindByNaturalKeyAsync(conn, fault, ct).ConfigureAwait(false) is not null) {
                return; // the reaction's action landed first and created the row
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO faults
                    (id, session_id, detected_at, equipment_type, equipment_id,
                     equipment_name, fault_type, details, action_taken, resolved_at,
                     affected_frames)
                VALUES
                    ($id, $session, $detected, $etype, $eid, $ename, $ftype, $details,
                     NULL, NULL, NULL);
                """;
            BindNaturalKey(cmd, fault);
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$session", (object?)_sessions?.Current?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ename", (object?)fault.DeviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$details", (object?)fault.Details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            LogFaultRecorded(fault.DeviceType, fault.Kind);
        } finally {
            _writeGate.Release();
        }
    }

    public async Task RecordActionAsync(EquipmentFaultEvent fault, string action, DateTimeOffset? resolvedUtc, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            await using var conn = _db.OpenConnection();
            await using (var update = conn.CreateCommand()) {
                update.CommandText = """
                    UPDATE faults
                    SET action_taken = $action,
                        resolved_at = COALESCE($resolved, resolved_at)
                    WHERE equipment_type = $etype AND fault_type = $ftype
                      AND detected_at = $detected AND equipment_id IS $eid;
                    """;
                BindNaturalKey(update, fault);
                update.Parameters.AddWithValue("$action", action);
                update.Parameters.AddWithValue("$resolved", (object?)resolvedUtc?.ToString("O") ?? DBNull.Value);
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0) {
                    return;
                }
            }
            // Detection hasn't landed yet (it persists fire-and-forget off the hub) —
            // create the row with the action already stamped; the late detection
            // insert then no-ops against the natural key.
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO faults
                    (id, session_id, detected_at, equipment_type, equipment_id,
                     equipment_name, fault_type, details, action_taken, resolved_at,
                     affected_frames)
                VALUES
                    ($id, $session, $detected, $etype, $eid, $ename, $ftype, $details,
                     $action, $resolved, NULL);
                """;
            BindNaturalKey(insert, fault);
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("$session", (object?)_sessions?.Current?.ToString() ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ename", (object?)fault.DeviceName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$details", (object?)fault.Details ?? DBNull.Value);
            insert.Parameters.AddWithValue("$action", action);
            insert.Parameters.AddWithValue("$resolved", (object?)resolvedUtc?.ToString("O") ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        } finally {
            _writeGate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is assembled from compile-time-constant fragments only; every caller-supplied value is bound through $-parameters, never concatenated into the SQL.")]
    public async Task<CursorPage<FaultDto>> ListAsync(
            int limit, string? cursor, string? equipmentType, Guid? sessionId,
            bool? unresolvedOnly, string? faultType, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(equipmentType)) {
            where.Add("equipment_type = $etype");
            cmd.Parameters.AddWithValue("$etype", equipmentType.ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(faultType)) {
            where.Add("fault_type = $ftype");
            cmd.Parameters.AddWithValue("$ftype", faultType.ToLowerInvariant());
        }
        if (sessionId is Guid session) {
            where.Add("session_id = $session");
            cmd.Parameters.AddWithValue("$session", session.ToString());
        }
        if (unresolvedOnly == true) {
            where.Add("resolved_at IS NULL");
        }
        cmd.CommandText = SelectColumns
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY detected_at DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);

        // Read every LIMIT pageSize+1 row, then trim the sentinel (the
        // SqliteCalibrationService pattern) — a read-then-check loop condition
        // consumes the sentinel row before the count check and reports
        // has_more=false on every page.
        var items = new List<FaultDto>(pageSize + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            items.Add(await ReadRowAsync(reader, ct).ConfigureAwait(false));
        }
        var hasMore = items.Count > pageSize;
        if (hasMore) {
            items.RemoveAt(items.Count - 1);
        }
        var nextCursor = hasMore ? (offset + pageSize).ToString(CultureInfo.InvariantCulture) : null;
        return new CursorPage<FaultDto>(items, NextCursor: nextCursor, HasMore: hasMore);
    }

    public async Task<FaultDto?> GetAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? await ReadRowAsync(reader, ct).ConfigureAwait(false)
            : null;
    }

    private const string SelectColumns = """
        SELECT id, session_id, detected_at, equipment_type, equipment_id,
               equipment_name, fault_type, details, action_taken, resolved_at,
               affected_frames
        FROM faults
        """;

    // The fault event's natural key — both upsert paths bind the same four
    // parameters so detection and action target the identical row. equipment_id
    // matches with IS (null-safe) in the UPDATE.
    private static void BindNaturalKey(SqliteCommand cmd, EquipmentFaultEvent fault) {
        cmd.Parameters.AddWithValue("$etype", fault.DeviceType.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$ftype", EquipmentFaultHub.WireToken(fault.Kind));
        cmd.Parameters.AddWithValue("$detected", fault.DetectedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$eid", (object?)fault.DeviceId ?? DBNull.Value);
    }

    private static async Task<string?> FindByNaturalKeyAsync(SqliteConnection conn, EquipmentFaultEvent fault, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM faults
            WHERE equipment_type = $etype AND fault_type = $ftype
              AND detected_at = $detected AND equipment_id IS $eid;
            """;
        BindNaturalKey(cmd, fault);
        return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task<FaultDto> ReadRowAsync(SqliteDataReader reader, CancellationToken ct) =>
        new(
            Id: Guid.Parse(reader.GetString(0)),
            SessionId: await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(1)),
            DetectedUtc: DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            EquipmentType: reader.GetString(3),
            EquipmentId: await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4),
            EquipmentName: await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetString(5),
            FaultType: reader.GetString(6),
            Details: await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetString(7),
            ActionTaken: await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetString(8),
            ResolvedUtc: await reader.IsDBNullAsync(9, ct).ConfigureAwait(false) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            AffectedFrames: await reader.IsDBNullAsync(10, ct).ConfigureAwait(false) ? [] : ParseFrameIds(reader.GetString(10)));

    private static List<Guid> ParseFrameIds(string json) {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) {
            return [];
        }
        var ids = new List<Guid>(doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray()) {
            if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var id)) {
                ids.Add(id);
            }
        }
        return ids;
    }

    public void Dispose() => _writeGate.Dispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fault log: recorded {DeviceType} {Kind} (§42.5)")]
    private partial void LogFaultRecorded(DeviceType deviceType, EquipmentFaultKind kind);
}
