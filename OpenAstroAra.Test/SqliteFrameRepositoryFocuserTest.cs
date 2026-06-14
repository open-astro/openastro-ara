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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38 focuser_position column: the per-frame focuser step position persists
    /// through the catalog (for the §50.4 focus-vs-temperature view) and the
    /// additive column add is idempotent across re-init.
    /// </summary>
    [TestFixture]
    public class SqliteFrameRepositoryFocuserTest {

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _repo = null!;
        private static readonly Guid Session = Guid.Parse("38383838-3838-3838-3838-383838383838");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-frame-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task FocuserPosition_round_trips_through_insert_and_get() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id, focuserPosition: 4321), CancellationToken.None);

            var got = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(got, Is.Not.Null);
            Assert.That(got!.FocuserPosition, Is.EqualTo(4321));
        }

        [Test]
        public async Task FocuserPosition_is_null_when_unset() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id, focuserPosition: null), CancellationToken.None);

            var got = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(got!.FocuserPosition, Is.Null);
        }

        [Test]
        public async Task InitializeAsync_is_idempotent_and_preserves_focuser_position() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id, focuserPosition: 999), CancellationToken.None);

            // Re-running init must not error on the already-present column nor drop data.
            await _db.InitializeAsync(CancellationToken.None);

            var got = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(got!.FocuserPosition, Is.EqualTo(999));
        }

        private static FrameDto Frame(Guid id, int? focuserPosition) => new(
            Id: id,
            SessionId: Session,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "Ha",
            ExposureSeconds: 300,
            Gain: 100,
            Offset: 10,
            TemperatureC: -10.0,
            CapturedUtc: DateTimeOffset.UtcNow,
            FilePath: $"/tmp/{id:N}.fits",
            FileSizeBytes: 1000,
            Width: 100,
            Height: 100,
            BitDepth: 16,
            Hfr: null,
            StarCount: null,
            Eccentricity: null,
            GuidingRmsArcsec: null,
            SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: Array.Empty<string>(),
            FocuserPosition: focuserPosition);

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
    }
}
