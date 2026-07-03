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
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §50.19 AstroBin acquisition CSV export: light frames grouped per
    /// (night, filter, sub-length, gain, cooling), one row each with the sub count.
    /// </summary>
    [TestFixture]
    public class SqliteStatsAstrobinExportTest {

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteStatsService _svc = null!;
        private static readonly Guid Session = Guid.Parse("55555555-5555-5555-5555-555555555551");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-astrobin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteStatsService(_db);
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Unknown_target_returns_null() {
            var result = await _svc.OpenAstrobinExportAsync("M31", CancellationToken.None);
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task Groups_light_frames_by_night_filter_duration_gain_cooling() {
            var night = new DateTimeOffset(2026, 1, 10, 22, 0, 0, TimeSpan.Zero);
            // Three Ha 300s subs + two OIII 300s subs on the same night for M31.
            await InsertLightAsync("M31", "Ha", 300, 100, -10.0, night);
            await InsertLightAsync("M31", "Ha", 300, 100, -10.0, night.AddMinutes(5));
            await InsertLightAsync("M31", "Ha", 300, 100, -9.8, night.AddMinutes(10)); // rounds to -10 cooling
            await InsertLightAsync("M31", "OIII", 300, 100, -10.0, night.AddMinutes(15));
            await InsertLightAsync("M31", "OIII", 300, 100, -10.0, night.AddMinutes(20));
            // A dark and another target must not appear.
            await InsertFrameAsync("dark", "M31", "Ha", 300, 100, -10.0, night);
            await InsertLightAsync("M42", "Ha", 300, 100, -10.0, night);

            var result = await _svc.OpenAstrobinExportAsync("M31", CancellationToken.None);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.FileName, Does.StartWith("astrobin-m31-"));

            var lines = await ReadAllLinesAsync(result.Value.Stream);
            Assert.That(lines[0], Is.EqualTo(
                "date,filter,number,duration,binning,gain,sensorCooling,fNumber,darks,flats,flatDarks,bias,bortle,meanSqm,meanFwhm,temperature"));
            // Two acquisition rows (Ha x3, OIII x2), ordered filter asc.
            Assert.That(lines.Length, Is.EqualTo(3), "header + 2 acquisition rows");
            Assert.That(lines[1], Is.EqualTo("2026-01-10,Ha,3,300,,100,-10,,,,,,,,,"));
            Assert.That(lines[2], Is.EqualTo("2026-01-10,OIII,2,300,,100,-10,,,,,,,,,"));
        }

        [Test]
        public async Task Distinct_sub_lengths_split_into_separate_rows() {
            var night = new DateTimeOffset(2026, 2, 1, 21, 0, 0, TimeSpan.Zero);
            await InsertLightAsync("NGC7000", "L", 120, 0, -5.0, night);
            await InsertLightAsync("NGC7000", "L", 300, 0, -5.0, night.AddMinutes(5));

            var result = await _svc.OpenAstrobinExportAsync("NGC7000", CancellationToken.None);
            var lines = await ReadAllLinesAsync(result!.Value.Stream);
            Assert.That(lines.Length, Is.EqualTo(3), "header + 2 rows (one per sub length)");
            Assert.That(lines[1], Does.Contain(",L,1,120,"));
            Assert.That(lines[2], Does.Contain(",L,1,300,"));
        }

        [Test]
        public async Task A_null_filter_exports_as_an_empty_filter_field() {
            var night = new DateTimeOffset(2026, 3, 3, 20, 0, 0, TimeSpan.Zero);
            await InsertFrameAsync("light", "Moon", filter: null, 5, 0, 20.0, night);

            var result = await _svc.OpenAstrobinExportAsync("Moon", CancellationToken.None);
            var lines = await ReadAllLinesAsync(result!.Value.Stream);
            Assert.That(lines[1], Is.EqualTo("2026-03-03,,1,5,,0,20,,,,,,,,,"));
        }

        [Test]
        public async Task Sub_second_duration_and_null_gain_export_honestly() {
            // §28 widened schema (PR #670 r2): a 0.5s lucky-imaging sub used to
            // export duration=0 (GetInt32 truncation) and NULL gain exported a
            // false gain=0 instead of a blank field.
            var night = new DateTimeOffset(2026, 4, 4, 22, 0, 0, TimeSpan.Zero);
            await InsertFrameAsync("light", "Jupiter", "L", 0.5, gain: null, 5.0, night);

            var result = await _svc.OpenAstrobinExportAsync("Jupiter", CancellationToken.None);
            var lines = await ReadAllLinesAsync(result!.Value.Stream);
            Assert.That(lines[1], Is.EqualTo("2026-04-04,L,1,0.5,,,5,,,,,,,,,"),
                "duration keeps sub-second precision; unknown gain is blank, never 0");
        }

        [Test]
        public async Task Sessions_scope_streams_the_per_session_rollup() {
            // §50: scope=sessions rolls the catalog up one row per session
            // (frames scope keeps dumping the full frames table).
            var night = new DateTimeOffset(2026, 5, 5, 21, 0, 0, TimeSpan.Zero);
            await InsertLightAsync("M31", "Ha", 300, 100, -10.0, night);
            await InsertLightAsync("M31", "Ha", 0.5, 100, -10.0, night.AddMinutes(10));
            await InsertFrameAsync("dark", "M31", null, 300, 100, -10.0, night.AddHours(1));

            var result = await _svc.OpenCsvExportAsync("sessions", CancellationToken.None);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.FileName, Does.StartWith("openastroara-sessions-"));
            var lines = await ReadAllLinesAsync(result.Value.Stream);
            Assert.That(lines[0], Is.EqualTo(
                "session_id,started_utc,ended_utc,target_name,total_frames,light_frames,light_integration_seconds"));
            Assert.That(lines.Length, Is.EqualTo(2), "header + one session row");
            Assert.That(lines[1], Does.Contain(",M31,3,2,300.5"),
                "counts split lights vs total; integration sums real (sub-second) light seconds");
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static async Task<string[]> ReadAllLinesAsync(Stream s) {
            using var reader = new StreamReader(s);
            var text = await reader.ReadToEndAsync();
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r')).ToArray();
        }

        private Task InsertLightAsync(string target, string filter, double exposureSeconds, int? gain, double tempC, DateTimeOffset capturedUtc) =>
            InsertFrameAsync("light", target, filter, exposureSeconds, gain, tempC, capturedUtc);

        private async Task InsertSessionAsync(Guid id) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, profile_id, sequence_json, started_at, ended_at,
                    recovery_needed, last_completed_instruction_id, current_target_id, frame_count)
                VALUES ($id, NULL, NULL, $t, $t, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private async Task InsertFrameAsync(string frameType, string target, string? filter,
                double exposureSeconds, int? gain, double tempC, DateTimeOffset capturedUtc) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, $target, $type, $filter, $exp, $gain, $temp, $utc,
                    $path, 1000, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", Session.ToString());
            cmd.Parameters.AddWithValue("$target", target);
            cmd.Parameters.AddWithValue("$type", frameType);
            cmd.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", exposureSeconds);
            cmd.Parameters.AddWithValue("$gain", gain is null ? DBNull.Value : gain.Value);
            cmd.Parameters.AddWithValue("$temp", tempC);
            cmd.Parameters.AddWithValue("$utc", capturedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
