#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>One finished §45.13 polar-alignment routine. <c>Outcome</c> is
/// <c>complete</c> | <c>aborted</c> | <c>failed</c>; error fields are null when the routine never
/// reached the adjusting state (e.g. a failed seed).</summary>
public sealed record PolarAlignmentRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double? FinalErrorArcmin,
    double? FinalAltErrorArcmin,
    double? FinalAzErrorArcmin,
    int Iterations,
    string Outcome);

/// <summary>§45.13 — append-only log of polar-alignment routines, one row per run.</summary>
public interface IPolarAlignmentLog {
    Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct);
}

/// <summary>SQLite-backed <see cref="IPolarAlignmentLog"/> over the <c>polar_alignments</c> table.</summary>
public sealed class SqlitePolarAlignmentLog : IPolarAlignmentLog {
    private readonly SqliteAraDatabase _db;

    public SqlitePolarAlignmentLog(SqliteAraDatabase db) => _db = db;

    public async Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO polar_alignments
                (id, session_id, started_at, ended_at, final_error_arcmin,
                 final_alt_error_arcmin, final_az_error_arcmin, iterations, outcome, notes)
            VALUES ($id, NULL, $started, $ended, $err, $alt, $az, $iterations, $outcome, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$started", record.StartedAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$ended", record.EndedAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$err", ToDb(record.FinalErrorArcmin));
        cmd.Parameters.AddWithValue("$alt", ToDb(record.FinalAltErrorArcmin));
        cmd.Parameters.AddWithValue("$az", ToDb(record.FinalAzErrorArcmin));
        cmd.Parameters.AddWithValue("$iterations", record.Iterations);
        cmd.Parameters.AddWithValue("$outcome", record.Outcome);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object ToDb(double? value) => value.HasValue ? value.Value : DBNull.Value;
}
