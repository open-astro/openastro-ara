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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The §28 widening pass: sub-second exposures round-trip as REAL (they used to
    /// round up to 1 s), gain is nullable (no more -1 sentinel), and — the critical
    /// half — a database created with the OLD v0.0.1 schema is rebuilt in place on
    /// initialize, with legacy -1 gains normalized to NULL and the frames indexes
    /// recreated.
    /// </summary>
    [TestFixture]
    public class FramesSchemaWideningTest {

        private string _dir = null!;
        private static readonly Guid Session = Guid.Parse("28282828-2828-2828-2828-282828282828");

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-widen-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static FrameDto Frame(Guid id, double exposureSeconds, int? gain) => new(
            Id: id, SessionId: Session, TargetName: "M31", FrameType: FrameType.Bias,
            FilterName: null, ExposureSeconds: exposureSeconds, Gain: gain, Offset: 10,
            TemperatureC: -10, CapturedUtc: DateTimeOffset.UtcNow, FilePath: "/tmp/x.fits",
            FileSizeBytes: 1, Width: 100, Height: 100, BitDepth: 16, Hfr: null,
            StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null, Rating: 0, Tags: []);

        private static async Task InsertSessionAsync(SqliteAraDatabase db, Guid id) {
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        [Test]
        public async Task Sub_second_exposure_and_null_gain_round_trip() {
            var db = new SqliteAraDatabase(_dir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            await InsertSessionAsync(db, Session);
            var repo = new SqliteFrameRepository(db, new InMemoryProfileStore());

            var id = Guid.NewGuid();
            await repo.InsertAsync(Frame(id, exposureSeconds: 0.001, gain: null), CancellationToken.None);

            var got = await repo.GetAsync(id, CancellationToken.None);
            Assert.That(got!.ExposureSeconds, Is.EqualTo(0.001),
                "a 1 ms bias must not round up to 1 s");
            Assert.That(got.Gain, Is.Null, "unreported gain is null, never a -1 sentinel");
        }

        [Test]
        public async Task Null_temperature_round_trips() {
            var db = new SqliteAraDatabase(_dir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            await InsertSessionAsync(db, Session);
            var repo = new SqliteFrameRepository(db, new InMemoryProfileStore());

            var id = Guid.NewGuid();
            await repo.InsertAsync(Frame(id, exposureSeconds: 60, gain: 100) with { TemperatureC = null }, CancellationToken.None);

            var got = await repo.GetAsync(id, CancellationToken.None);
            Assert.That(got!.TemperatureC, Is.Null,
                "a camera reporting no CCD temperature records NULL, never a 0.0 sentinel");
        }

        [Test]
        public async Task A_not_null_temperature_schema_is_rebuilt_nullable_with_rows_verbatim() {
            // Hand-build the post-#670 / pre-sentinel-pass shape (REAL NOT NULL temp)
            // with one legacy uncooled row (the ambiguous 0.0) and one cooled row.
            var dbPath = Path.Combine(_dir, "openastroara.db");
            await using (var conn = new SqliteConnection($"Data Source={dbPath}")) {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE sessions (
                        id                            TEXT PRIMARY KEY NOT NULL,
                        profile_id                    TEXT,
                        sequence_json                 TEXT,
                        started_at                    TEXT NOT NULL,
                        ended_at                      TEXT,
                        recovery_needed               INTEGER NOT NULL DEFAULT 0,
                        last_completed_instruction_id TEXT,
                        current_target_id             TEXT,
                        frame_count                   INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE frames (
                        id                 TEXT PRIMARY KEY NOT NULL,
                        session_id         TEXT NOT NULL,
                        target_name        TEXT NOT NULL,
                        frame_type         TEXT NOT NULL,
                        filter_name        TEXT,
                        exposure_seconds   REAL NOT NULL,
                        gain               INTEGER,
                        "offset"           INTEGER,
                        temperature_c      REAL NOT NULL,
                        captured_utc       TEXT NOT NULL,
                        file_path          TEXT NOT NULL,
                        file_size_bytes    INTEGER NOT NULL,
                        width              INTEGER NOT NULL,
                        height             INTEGER NOT NULL,
                        bit_depth          INTEGER NOT NULL,
                        hfr                REAL,
                        star_count         INTEGER,
                        eccentricity       REAL,
                        guiding_rms_arcsec REAL,
                        snr_estimate       REAL,
                        quality_score_json TEXT,
                        rating             INTEGER NOT NULL DEFAULT 0,
                        tags_json          TEXT NOT NULL DEFAULT '[]',
                        focuser_position   INTEGER,
                        FOREIGN KEY (session_id) REFERENCES sessions(id)
                    );
                    INSERT INTO sessions (id, started_at) VALUES ('28282828-2828-2828-2828-282828282828', '2026-01-01T00:00:00Z');
                    INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                        exposure_seconds, gain, "offset", temperature_c, captured_utc, file_path,
                        file_size_bytes, width, height, bit_depth, rating, tags_json)
                    VALUES
                    ('22222222-2222-2222-2222-222222222221', '28282828-2828-2828-2828-282828282828',
                        'M31', 'light', 'Ha', 300, 100, 10, 0.0, '2026-01-01T01:00:00Z', '/tmp/a.fits', 1, 100, 100, 16, 0, '[]'),
                    ('22222222-2222-2222-2222-222222222222', '28282828-2828-2828-2828-282828282828',
                        'M31', 'light', 'Ha', 300, 100, 10, -10.0, '2026-01-01T01:05:00Z', '/tmp/b.fits', 1, 100, 100, 16, 0, '[]');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var db = new SqliteAraDatabase(_dir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            var repo = new SqliteFrameRepository(db, new InMemoryProfileStore());

            // Rows copied VERBATIM: the ambiguous legacy 0.0 stays 0.0 (it cannot be
            // told apart from a real freezing-point reading), the cooled row stays.
            var legacy = await repo.GetAsync(Guid.Parse("22222222-2222-2222-2222-222222222221"), CancellationToken.None);
            Assert.That(legacy!.TemperatureC, Is.EqualTo(0.0));
            var cooled = await repo.GetAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"), CancellationToken.None);
            Assert.That(cooled!.TemperatureC, Is.EqualTo(-10.0));

            // The rebuilt shape accepts NULL, and schema_version records the pass.
            var id = Guid.NewGuid();
            await repo.InsertAsync(Frame(id, exposureSeconds: 60, gain: null) with { TemperatureC = null }, CancellationToken.None);
            Assert.That((await repo.GetAsync(id, CancellationToken.None))!.TemperatureC, Is.Null);
            await using var check = db.OpenConnection();
            await using var ver = check.CreateCommand();
            ver.CommandText = "SELECT version FROM schema_version;";
            Assert.That(Convert.ToInt64(await ver.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(3), "the sentinel pass introduces schema_version=3 (the #670 commitment)");

            Assert.DoesNotThrowAsync(() => new SqliteAraDatabase(_dir, logger: null).InitializeAsync(CancellationToken.None));
        }

        [Test]
        public async Task An_old_schema_database_is_widened_in_place_on_initialize() {
            // Hand-build the v0.0.1 shape (INTEGER exposure, NOT NULL gain) with a
            // legacy row carrying the -1 gain sentinel…
            var dbPath = Path.Combine(_dir, "openastroara.db");
            await using (var conn = new SqliteConnection($"Data Source={dbPath}")) {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE sessions (
                        id                            TEXT PRIMARY KEY NOT NULL,
                        profile_id                    TEXT,
                        sequence_json                 TEXT,
                        started_at                    TEXT NOT NULL,
                        ended_at                      TEXT,
                        recovery_needed               INTEGER NOT NULL DEFAULT 0,
                        last_completed_instruction_id TEXT,
                        current_target_id             TEXT,
                        frame_count                   INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE TABLE frames (
                        id                 TEXT PRIMARY KEY NOT NULL,
                        session_id         TEXT NOT NULL,
                        target_name        TEXT NOT NULL,
                        frame_type         TEXT NOT NULL,
                        filter_name        TEXT,
                        exposure_seconds   INTEGER NOT NULL,
                        gain               INTEGER NOT NULL,
                        "offset"           INTEGER,
                        temperature_c      REAL NOT NULL,
                        captured_utc       TEXT NOT NULL,
                        file_path          TEXT NOT NULL,
                        file_size_bytes    INTEGER NOT NULL,
                        width              INTEGER NOT NULL,
                        height             INTEGER NOT NULL,
                        bit_depth          INTEGER NOT NULL,
                        hfr                REAL,
                        star_count         INTEGER,
                        eccentricity       REAL,
                        guiding_rms_arcsec REAL,
                        snr_estimate       REAL,
                        quality_score_json TEXT,
                        rating             INTEGER NOT NULL DEFAULT 0,
                        tags_json          TEXT NOT NULL DEFAULT '[]',
                        FOREIGN KEY (session_id) REFERENCES sessions(id)
                    );
                    INSERT INTO sessions (id, started_at) VALUES ('28282828-2828-2828-2828-282828282828', '2026-01-01T00:00:00Z');
                    INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                        exposure_seconds, gain, "offset", temperature_c, captured_utc, file_path,
                        file_size_bytes, width, height, bit_depth, rating, tags_json)
                    VALUES ('11111111-1111-1111-1111-111111111111', '28282828-2828-2828-2828-282828282828',
                        'M31', 'light', 'Ha', 180, -1, 10, -10.0, '2026-01-01T01:00:00Z', '/tmp/a.fits',
                        1, 100, 100, 16, 0, '[]');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // …then initialize the current build over it: the widening rebuild runs.
            var db = new SqliteAraDatabase(_dir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            var repo = new SqliteFrameRepository(db, new InMemoryProfileStore());

            var legacy = await repo.GetAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), CancellationToken.None);
            Assert.That(legacy, Is.Not.Null, "legacy rows survive the rebuild");
            Assert.That(legacy!.ExposureSeconds, Is.EqualTo(180));
            Assert.That(legacy.Gain, Is.Null, "the legacy -1 sentinel is normalized to NULL in the copy");

            // The widened shape accepts what the old one couldn't.
            var id = Guid.NewGuid();
            await repo.InsertAsync(Frame(id, exposureSeconds: 0.5, gain: null), CancellationToken.None);
            Assert.That((await repo.GetAsync(id, CancellationToken.None))!.ExposureSeconds, Is.EqualTo(0.5));

            // The frames indexes were recreated by the rebuild.
            await using var check = db.OpenConnection();
            await using var idx = check.CreateCommand();
            idx.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='frames' AND name LIKE 'idx_frames_%';";
            Assert.That(Convert.ToInt64(await idx.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(3), "session_id + captured_utc + light partial indexes all recreated");

            // Idempotence: a second initialize is a no-op (the DDL no longer matches).
            var again = new SqliteAraDatabase(_dir, logger: null);
            Assert.DoesNotThrowAsync(() => again.InitializeAsync(CancellationToken.None));
        }
    }
}
