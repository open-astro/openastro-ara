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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The exposure and panel brightness a flat set last solved for one filter/gain/offset/bin,
/// and the level it measured there against the target it aimed for.</summary>
public sealed record FlatTraining(double ExposureSec, int? Brightness, double Level, double Target);

/// <summary>Remembered flat solutions (NINA's "trained flat exposures"), in the catalog database.</summary>
public interface IFlatTrainingStore {
    Task<FlatTraining?> GetAsync(string filterName, int gain, int offset, int bin, CancellationToken ct);
    Task PutAsync(string filterName, int gain, int offset, int bin, FlatTraining training, CancellationToken ct);
}

public sealed class FlatTrainingStore : IFlatTrainingStore {
    private readonly IAraDatabase _db;

    public FlatTrainingStore(IAraDatabase db) {
        _db = db;
    }

    public async Task<FlatTraining?> GetAsync(string filterName, int gain, int offset, int bin, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT exposure_sec, brightness, level, target FROM flat_training
            WHERE filter_name = $f AND gain = $g AND "offset" = $o AND bin = $b;
            """;
        Bind(cmd, filterName, gain, offset, bin);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) {
            return null;
        }
        return new FlatTraining(
            reader.GetDouble(0),
            await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetInt32(1),
            reader.GetDouble(2),
            reader.GetDouble(3));
    }

    public async Task PutAsync(string filterName, int gain, int offset, int bin, FlatTraining training, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(training);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flat_training (filter_name, gain, "offset", bin, exposure_sec, brightness, level, target, updated_utc)
            VALUES ($f, $g, $o, $b, $exp, $bright, $level, $target, $utc)
            ON CONFLICT (filter_name, gain, "offset", bin) DO UPDATE SET
                exposure_sec = excluded.exposure_sec, brightness = excluded.brightness,
                level = excluded.level, target = excluded.target, updated_utc = excluded.updated_utc;
            """;
        Bind(cmd, filterName, gain, offset, bin);
        cmd.Parameters.AddWithValue("$exp", training.ExposureSec);
        cmd.Parameters.AddWithValue("$bright", training.Brightness is int b ? b : DBNull.Value);
        cmd.Parameters.AddWithValue("$level", training.Level);
        cmd.Parameters.AddWithValue("$target", training.Target);
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, string filterName, int gain, int offset, int bin) {
        cmd.Parameters.AddWithValue("$f", filterName);
        cmd.Parameters.AddWithValue("$g", gain);
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.Parameters.AddWithValue("$b", bin);
    }
}
