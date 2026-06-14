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
    /// §38/§50.4 focus-vs-temperature: focuser position paired with sensor
    /// temperature per frame, with a Pearson r² correlation. Frames with no
    /// recorded focuser position are excluded.
    /// </summary>
    [TestFixture]
    public class SqliteStatsFocusTempTest {

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteStatsService _svc = null!;
        private static readonly Guid Session = Guid.Parse("38383838-3838-3838-3838-383838383839");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-focustemp-{Guid.NewGuid():N}");
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
        public async Task Empty_catalog_returns_no_samples_and_null_correlation() {
            var dto = await _svc.GetFocusTempAsync(null, CancellationToken.None);
            Assert.That(dto.Samples, Is.Empty);
            Assert.That(dto.CorrelationR2, Is.Null);
        }

        [Test]
        public async Task Only_frames_with_a_focuser_position_become_samples() {
            await InsertFrameAsync(temp: 5.0, focuserPos: 1000, minutesAgo: 30);
            await InsertFrameAsync(temp: 4.0, focuserPos: null, minutesAgo: 20); // excluded
            await InsertFrameAsync(temp: 3.0, focuserPos: 1100, minutesAgo: 10);

            var dto = await _svc.GetFocusTempAsync(null, CancellationToken.None);
            Assert.That(dto.Samples.Count, Is.EqualTo(2));
            Assert.That(dto.Samples.Select(s => s.FocuserPosition), Is.EquivalentTo(ExpectedPositions));
        }

        private static readonly int[] ExpectedPositions = { 1000, 1100 };

        [Test]
        public async Task A_perfect_linear_relationship_has_r2_of_one() {
            // position = 1000 - 20*temp → perfectly (negatively) correlated.
            await InsertFrameAsync(temp: 0.0, focuserPos: 1000, minutesAgo: 40);
            await InsertFrameAsync(temp: 5.0, focuserPos: 900, minutesAgo: 30);
            await InsertFrameAsync(temp: 10.0, focuserPos: 800, minutesAgo: 20);
            await InsertFrameAsync(temp: 15.0, focuserPos: 700, minutesAgo: 10);

            var dto = await _svc.GetFocusTempAsync(null, CancellationToken.None);
            Assert.That(dto.CorrelationR2, Is.Not.Null);
            Assert.That(dto.CorrelationR2!.Value, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public async Task Constant_temperature_has_null_correlation() {
            // Zero variance in temperature → r² undefined.
            await InsertFrameAsync(temp: 7.0, focuserPos: 1000, minutesAgo: 30);
            await InsertFrameAsync(temp: 7.0, focuserPos: 1100, minutesAgo: 20);

            var dto = await _svc.GetFocusTempAsync(null, CancellationToken.None);
            Assert.That(dto.Samples.Count, Is.EqualTo(2));
            Assert.That(dto.CorrelationR2, Is.Null);
        }

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

        private async Task InsertFrameAsync(double temp, int? focuserPos, int minutesAgo) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth, focuser_position)
                VALUES ($id, $session, 'M31', 'light', 'Ha',
                    300, 100, $temp, $captured, $path,
                    1000, 100, 100, 16, $focuser);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$session", Session.ToString());
            cmd.Parameters.AddWithValue("$temp", temp);
            cmd.Parameters.AddWithValue("$captured",
                DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            cmd.Parameters.AddWithValue("$focuser", focuserPos is int fp ? fp : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
