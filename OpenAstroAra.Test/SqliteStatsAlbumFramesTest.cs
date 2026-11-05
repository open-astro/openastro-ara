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
    /// Sky Album slice 3 (#1518, docs/specs/sky-album-v1.md "Endpoint"): album-frames serves the lights
    /// Ara captured, only the ones changed after <c>since</c> when given, and always the full count.
    /// </summary>
    [TestFixture]
    [Category("IO")] // real SQLite on disk
    public class SqliteStatsAlbumFramesTest {
        private static readonly Guid Session = Guid.Parse("15181518-1518-1518-1518-151815181518");
        private static readonly DateTimeOffset T1 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset T2 = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset T3 = new(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteStatsService _svc = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-albumframes-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteStatsService(_db);
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, profile_id, started_at, recovery_needed, frame_count)
                VALUES ($s, 'profile-1', '2026-03-01T00:00:00.0000000+00:00', 0, 0);
                """;
            cmd.Parameters.AddWithValue("$s", Session.ToString());
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task An_empty_catalog_serves_nothing() {
            var dto = await _svc.GetAlbumFramesAsync(null, CancellationToken.None);
            Assert.That(dto.TotalLights, Is.Zero);
            Assert.That(dto.Frames, Is.Empty);
        }

        [Test]
        public async Task Only_Ara_lights_are_served_and_since_filters_by_updated_time() {
            var early = await InsertAsync("light", ara: 1, updated: T1, quality: """{"composite":0.83}""");
            var late = await InsertAsync("light", ara: 1, updated: T3);
            await InsertAsync("light", ara: 0, updated: T3);    // a NINA frame the drive scan catalogued
            await InsertAsync("light", ara: null, updated: T3); // not probed yet
            await InsertAsync("dark", ara: 1, updated: T3);

            var all = await _svc.GetAlbumFramesAsync(null, CancellationToken.None);
            Assert.That(all.TotalLights, Is.EqualTo(2));
            Assert.That(all.Frames.Select(f => f.FrameId), Is.EquivalentTo(new[] { early, late }));

            var row = all.Frames.Single(f => f.FrameId == early);
            Assert.That((row.RaDeg, row.DecDeg, row.Width, row.Height, row.Bin, row.PixelScaleArcsec),
                Is.EqualTo(((double?)10.68, (double?)41.27, 4144, 2822, (int?)2, (double?)1.56)));
            Assert.That((row.ExposureSeconds, row.FilterName, row.TargetName, row.CompositeScore, row.ProfileId),
                Is.EqualTo((180.0, "Ha", "M31", (double?)0.83, "profile-1")));
            Assert.That(row.CapturedUtc, Is.EqualTo(T1));
            Assert.That(row.UpdatedUtc, Is.EqualTo(T1));
            Assert.That(all.Frames.Single(f => f.FrameId == late).CompositeScore, Is.Null, "no quality score yet");

            var changed = await _svc.GetAlbumFramesAsync(T2, CancellationToken.None);
            Assert.That(changed.Frames.Select(f => f.FrameId), Is.EqualTo(new[] { late }));
            Assert.That(changed.TotalLights, Is.EqualTo(2), "totalLights is always the full count");

            var local = await _svc.GetAlbumFramesAsync(T2.ToOffset(TimeSpan.FromHours(13)), CancellationToken.None);
            Assert.That(local.Frames.Select(f => f.FrameId), Is.EqualTo(new[] { late }), "a non-UTC since compares as the same instant");
        }

        private async Task<Guid> InsertAsync(string type, int? ara, DateTimeOffset updated, string? quality = null) {
            var id = Guid.NewGuid();
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name, exposure_seconds, captured_utc,
                    file_path, file_size_bytes, width, height, bit_depth, quality_score_json, bin,
                    ra_deg, dec_deg, pixel_scale_arcsec, captured_by_ara, updated_utc)
                VALUES ($id, $s, 'M31', $type, 'Ha', 180, $captured, '/x.fits', 1, 4144, 2822, 16, $quality, 2,
                    10.68, 41.27, 1.56, $ara, $updated);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$s", Session.ToString());
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$captured", T1.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$quality", (object?)quality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ara", ara is int a ? a : DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", updated.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            return id;
        }
    }
}
