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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §50 stats-perf: the partial covering index <c>idx_frames_light_captured</c>
    /// (frames(captured_utc) WHERE frame_type = 'light') exists and is chosen by
    /// the planner for the light-frame, captured_utc-ordered queries the stats
    /// service runs.
    /// </summary>
    [TestFixture]
    public class SqliteFramesLightIndexTest {

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-lightidx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task The_partial_light_index_exists() {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_light_captured';";
            var count = (long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!;
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public async Task A_light_frame_captured_utc_query_uses_the_partial_index() {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            // The shape the stats service runs (e.g. focus-temp / best-frames):
            // restrict to light frames, ordered by capture time.
            cmd.CommandText =
                "EXPLAIN QUERY PLAN SELECT captured_utc FROM frames WHERE frame_type = 'light' ORDER BY captured_utc;";
            var plan = new System.Text.StringBuilder();
            await using (var reader = await cmd.ExecuteReaderAsync(CancellationToken.None)) {
                while (await reader.ReadAsync(CancellationToken.None)) {
                    // The "detail" column carries the human-readable plan step.
                    plan.Append(reader.GetString(reader.GetOrdinal("detail"))).Append('\n');
                }
            }
            Assert.That(plan.ToString(), Does.Contain("idx_frames_light_captured"),
                $"planner did not use the partial light index. Plan:\n{plan}");
        }
    }
}
