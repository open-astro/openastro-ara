#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#1311 — the night's polar-alignment residuals measured from guiding, one row per
/// completed measurement.</summary>
public interface IPaResidualLog {
    Task InsertAsync(PaResidualDto result, CancellationToken ct);

    /// <summary>Newest first; only <paramref name="sessionId"/>'s rows when given.</summary>
    Task<IReadOnlyList<PaResidualDto>> ListAsync(Guid? sessionId, int limit, CancellationToken ct);
}

/// <summary>SQLite-backed <see cref="IPaResidualLog"/> over the <c>pa_residuals</c> table.</summary>
public sealed class SqlitePaResidualLog : IPaResidualLog {
    private readonly IAraDatabase _db;

    public SqlitePaResidualLog(IAraDatabase db) => _db = db;

    public async Task InsertAsync(PaResidualDto result, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != "done" || result.CompletedUtc is not DateTimeOffset completed
                || result.DriftArcsecPerMin is not double drift || result.PaErrorMinArcmin is not double error
                || result.UncertaintyArcmin is not double uncertainty) {
            throw new ArgumentException("only a finished measurement is logged", nameof(result));
        }
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pa_residuals
                (id, session_id, started_at, completed_at, sample_seconds, frames, drift_arcsec_per_min,
                 pa_error_min_arcmin, uncertainty_arcmin, reliable, hour_angle_hours, dec_deg,
                 align_error_arcmin, align_ended_at)
            VALUES ($id, $session, $started, $completed, $sample, $frames, $drift,
                    $error, $uncertainty, $reliable, $ha, $dec, $align, $alignEnded);
            """;
        cmd.Parameters.AddWithValue("$id", result.Id.ToString());
        cmd.Parameters.AddWithValue("$session", result.SessionId is Guid session ? session.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$started", Iso(result.StartedUtc));
        cmd.Parameters.AddWithValue("$completed", Iso(completed));
        cmd.Parameters.AddWithValue("$sample", result.SampleSeconds);
        cmd.Parameters.AddWithValue("$frames", result.Frames);
        cmd.Parameters.AddWithValue("$drift", drift);
        cmd.Parameters.AddWithValue("$error", error);
        cmd.Parameters.AddWithValue("$uncertainty", uncertainty);
        cmd.Parameters.AddWithValue("$reliable", result.Reliable == true ? 1 : 0);
        cmd.Parameters.AddWithValue("$ha", ToDb(result.HourAngleHours));
        cmd.Parameters.AddWithValue("$dec", ToDb(result.DecDeg));
        cmd.Parameters.AddWithValue("$align", ToDb(result.AlignErrorArcmin));
        cmd.Parameters.AddWithValue("$alignEnded", result.AlignEndedUtc is DateTimeOffset a ? Iso(a) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaResidualDto>> ListAsync(Guid? sessionId, int limit, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, started_at, completed_at, sample_seconds, frames, drift_arcsec_per_min,
                   pa_error_min_arcmin, uncertainty_arcmin, reliable, hour_angle_hours, dec_deg,
                   align_error_arcmin, align_ended_at
            FROM pa_residuals
            WHERE ($session IS NULL OR session_id = $session)
            ORDER BY completed_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$session", sessionId is Guid s ? s.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var rows = new List<PaResidualDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            async Task<bool> IsNull(int ordinal) => await reader.IsDBNullAsync(ordinal, ct).ConfigureAwait(false);
            rows.Add(new PaResidualDto(
                Id: Guid.Parse(reader.GetString(0)),
                Status: "done",
                StartedUtc: ParseIso(reader.GetString(2)),
                CompletedUtc: ParseIso(reader.GetString(3)),
                SampleSeconds: reader.GetDouble(4),
                // A logged row is finished: its sample is the whole of it.
                TargetSeconds: reader.GetDouble(4),
                Frames: reader.GetInt32(5),
                DriftArcsecPerMin: reader.GetDouble(6),
                PaErrorMinArcmin: reader.GetDouble(7),
                UncertaintyArcmin: reader.GetDouble(8),
                Reliable: reader.GetInt32(9) != 0,
                HourAngleHours: await IsNull(10) ? null : reader.GetDouble(10),
                DecDeg: await IsNull(11) ? null : reader.GetDouble(11),
                AlignErrorArcmin: await IsNull(12) ? null : reader.GetDouble(12),
                AlignEndedUtc: await IsNull(13) ? null : ParseIso(reader.GetString(13)),
                SessionId: await IsNull(1) ? null : Guid.Parse(reader.GetString(1))));
        }
        return rows;
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static object ToDb(double? value) => value.HasValue ? value.Value : DBNull.Value;
}
