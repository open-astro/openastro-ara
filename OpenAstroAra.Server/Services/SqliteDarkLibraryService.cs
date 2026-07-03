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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §39.8 dark library over the §28 SQLite frame catalog (replaces the Phase 13.14 fixture
/// placeholder). The library IS the catalog: an entry is a distinct
/// (exposure, gain, whole-degree temperature) group of DARK frames — the same key
/// <see cref="SqliteCalibrationService"/> matches darks to lights by, so what this service
/// lists is exactly what MatchingDarksAvailable consults.
///
/// The <em>build</em> follows the §39.5 pattern proven on the matching-flats slice: it does
/// not run a parallel capture engine — it generates a runnable §38 dark-matrix sequence
/// (CoolCamera per temperature set-point, a looped TakeExposure(DARK) container per
/// combination) through <see cref="CalibrationSequenceBuilder"/> and persists it to the
/// sequence store. The user reviews and starts it like any other sequence; captured darks
/// land in the catalog through the normal §28 pipeline and thereby appear here.
/// </summary>
public sealed class SqliteDarkLibraryService : IDarkLibraryService {

    private readonly IAraDatabase _db;
    private readonly ISequenceService? _sequences;

    private readonly object _gate = new();
    private BuildRecord? _lastBuild;

    private sealed record BuildRecord(
        Guid? GeneratedSequenceId,
        IReadOnlyList<(double ExposureSeconds, int? Gain, double? TemperatureC)> Combinations,
        int FramesPerCombination,
        DateTimeOffset StartedUtc,
        DateTimeOffset? CompletedUtc);

    /// <param name="db">The §28 frame catalog.</param>
    /// <param name="sequences">The §38 store the generated dark-matrix sequence is persisted
    /// into. Null (test/bootstrap contexts only — Program.cs always wires the real store)
    /// degrades the build to plan-only: state is tracked but no sequence is written.</param>
    public SqliteDarkLibraryService(IAraDatabase db, ISequenceService? sequences = null) {
        _db = db;
        _sequences = sequences;
    }

    public async Task<OperationAcceptedDto> StartBuildAsync(DarkLibraryBuildRequestDto request, string? idempotencyKey, CancellationToken ct) {
        var exposures = (request.ExposureSecondsList ?? Array.Empty<double>())
            .Where(e => e > 0 && double.IsFinite(e)).Distinct().ToList();
        if (exposures.Count == 0) {
            throw new ArgumentException("ExposureSecondsList must contain at least one positive exposure.", nameof(request));
        }
        var frames = request.FramesPerCombination;
        if (frames <= 0) {
            throw new ArgumentException("FramesPerCombination must be positive.", nameof(request));
        }

        // Empty gain list = one combination at camera-default gain (no Gain on the generated
        // TakeExposure); empty temperature list = capture at ambient (no CoolCamera step).
        var gains = (request.GainList ?? Array.Empty<int>()).Distinct().Select(g => (int?)g).DefaultIfEmpty(null).ToList();
        var temps = (request.TargetTemperatureCList ?? Array.Empty<double>())
            .Where(double.IsFinite).Distinct().Select(t => (double?)t).DefaultIfEmpty(null).ToList();

        var combos = new List<(double ExposureSeconds, int? Gain, double? TemperatureC)>();
        foreach (var t in temps) {
            foreach (var e in exposures) {
                foreach (var g in gains) {
                    combos.Add((e, g, t));
                }
            }
        }

        var toCapture = combos;
        if (request.ReuseExistingFrames) {
            // Skip combinations the catalog already covers with enough darks. An ambient
            // (no set-point) combination can't predict its captured temperature, so coverage
            // for those matches on (exposure, gain) at any temperature.
            var covered = new List<(double, int?, double?)>();
            await using var conn = _db.OpenConnection();
            foreach (var combo in combos) {
                if (await CoveredCountAsync(conn, combo, ct) >= frames) {
                    covered.Add(combo);
                }
            }
            toCapture = combos.Except(covered).ToList();
        }

        Guid? generatedId = null;
        if (toCapture.Count > 0 && _sequences is not null) {
            var groups = toCapture
                .GroupBy(c => c.TemperatureC)
                .Select(g => new DarkTemperatureGroupSpec(
                    TemperatureC: g.Key,
                    Steps: g.Select(c => new DarkStepSpec(c.ExposureSeconds, c.Gain, frames)).ToList()))
                .ToList();
            var name = $"Dark library — {toCapture.Count} combination{(toCapture.Count == 1 ? "" : "s")} × {frames}";
            var body = CalibrationSequenceBuilder.BuildDarkLibraryBody(name, groups);
            var created = await _sequences.CreateAsync(new SequenceCreateRequestDto(
                Name: name,
                Description: $"§39.8 dark-matrix build requested {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. Run on a cloudy/moonless night with the scope covered.",
                Body: body,
                TemplateOrigin: "calibration:dark-library"), idempotencyKey, ct);
            generatedId = created.Id;
        }

        lock (_gate) {
            _lastBuild = new BuildRecord(
                GeneratedSequenceId: generatedId,
                Combinations: combos,
                FramesPerCombination: frames,
                StartedUtc: DateTimeOffset.UtcNow,
                CompletedUtc: toCapture.Count == 0 ? DateTimeOffset.UtcNow : null);
        }

        return new OperationAcceptedDto(
            OperationId: Guid.NewGuid(),
            OperationType: "calibration.dark-library.build",
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);
    }

    public async Task<DarkLibraryStateDto> GetStatusAsync(CancellationToken ct) {
        var entries = await ListEntriesAsync(ct);

        BuildRecord? build;
        lock (_gate) {
            build = _lastBuild;
        }

        if (build is null) {
            // No build requested this daemon lifetime: the library is simply what the catalog
            // holds; every listed group is by definition "complete".
            return new DarkLibraryStateDto(
                Status: "idle",
                TotalCombinations: entries.Count,
                CompletedCombinations: entries.Count,
                BuildStartedUtc: null,
                BuildCompletedUtc: null,
                FailureReason: null,
                Entries: entries,
                GeneratedSequenceId: null);
        }

        var completed = 0;
        await using (var conn = _db.OpenConnection()) {
            foreach (var combo in build.Combinations) {
                if (await CoveredCountAsync(conn, combo, ct) >= build.FramesPerCombination) {
                    completed++;
                }
            }
        }

        var done = completed >= build.Combinations.Count;
        if (done) {
            lock (_gate) {
                // Stamp completion the first time a status read observes full coverage (the
                // capture runs as a normal §38 sequence, so no other component can). Idempotent:
                // once stamped, later reads keep the first timestamp.
                if (_lastBuild is not null && _lastBuild.CompletedUtc is null) {
                    _lastBuild = _lastBuild with { CompletedUtc = DateTimeOffset.UtcNow };
                }
                build = _lastBuild ?? build;
            }
        }

        return new DarkLibraryStateDto(
            Status: done ? "complete" : "pending",
            TotalCombinations: build.Combinations.Count,
            CompletedCombinations: completed,
            BuildStartedUtc: build.StartedUtc,
            BuildCompletedUtc: build.CompletedUtc,
            FailureReason: null,
            Entries: entries,
            GeneratedSequenceId: build.GeneratedSequenceId);
    }

    public async Task<IReadOnlyList<DarkLibraryEntryDto>> ListEntriesAsync(CancellationToken ct) {
        var entries = new List<DarkLibraryEntryDto>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // ROUND(temperature_c, 0) is the same whole-degree bucket the §39 matching rules use.
        // The representative file path is the newest frame's (MAX(captured_utc) pairs with it
        // via the self-ordering trick below); size is the group total.
        cmd.CommandText = """
            SELECT exposure_seconds,
                   gain,
                   ROUND(temperature_c, 0) AS temp_bucket,
                   COUNT(*) AS frame_count,
                   MAX(captured_utc) AS newest_utc,
                   SUM(file_size_bytes) AS total_bytes,
                   (SELECT f2.file_path FROM frames f2
                     WHERE f2.frame_type = 'dark'
                       AND f2.exposure_seconds = f.exposure_seconds
                       AND (f2.gain = f.gain OR (f2.gain IS NULL AND f.gain IS NULL))
                       AND ROUND(f2.temperature_c, 0) = ROUND(f.temperature_c, 0)
                     ORDER BY f2.captured_utc DESC LIMIT 1) AS newest_path
            FROM frames f
            WHERE frame_type = 'dark'
            GROUP BY exposure_seconds, gain, temp_bucket
            ORDER BY exposure_seconds, gain, temp_bucket;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var exposure = reader.GetDouble(0);
            var gain = await reader.IsDBNullAsync(1, ct) ? (int?)null : reader.GetInt32(1);
            var temp = reader.GetDouble(2);
            entries.Add(new DarkLibraryEntryDto(
                Id: StableEntryId(exposure, gain, temp),
                ExposureSeconds: exposure,
                Gain: gain,
                TemperatureC: temp,
                FrameCount: reader.GetInt32(3),
                CapturedUtc: ParseUtc(reader.GetString(4)),
                FilePath: await reader.IsDBNullAsync(6, ct) ? string.Empty : reader.GetString(6),
                FileSizeBytes: reader.GetInt64(5)));
        }
        return entries;
    }

    private static async Task<int> CoveredCountAsync(
            SqliteConnection conn, (double ExposureSeconds, int? Gain, double? TemperatureC) combo, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        // One constant command text (CA2100-clean): the nullable gain/temperature predicates
        // collapse via IS-NULL branches on bound parameters instead of string composition.
        // $temp NULL = ambient combo → any captured temperature counts (can't predict ambient).
        cmd.CommandText = """
            SELECT COUNT(*) FROM frames
            WHERE frame_type = 'dark'
              AND exposure_seconds = $exp
              AND (gain = $gain OR ($gain IS NULL AND gain IS NULL))
              AND ($temp IS NULL OR ROUND(temperature_c, 0) = ROUND($temp, 0));
            """;
        cmd.Parameters.AddWithValue("$exp", combo.ExposureSeconds);
        cmd.Parameters.AddWithValue("$gain", combo.Gain is int gain ? gain : DBNull.Value);
        cmd.Parameters.AddWithValue("$temp", combo.TemperatureC is double temp ? temp : DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0, CultureInfo.InvariantCulture);
    }

    // A stable id per (exposure, gain, temp-bucket) group so clients can diff/select entries
    // across polls: same group ⇒ same Guid, no persistence needed.
    private static Guid StableEntryId(double exposureSeconds, int? gain, double tempBucket) {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"dark|{exposureSeconds:R}|{(gain is int g ? g.ToString(CultureInfo.InvariantCulture) : "null")}|{tempBucket:R}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : DateTimeOffset.UnixEpoch;
}
