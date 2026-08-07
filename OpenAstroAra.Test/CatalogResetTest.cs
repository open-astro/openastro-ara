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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>§50 stats maintenance: ResetAndRescanAsync wipes frames +
    /// sessions and re-ingests from the mounted store in one locked op.</summary>
    [TestFixture]
    public class CatalogResetTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private CaptureScanService _scan = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-catreset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            var profile = new InMemoryProfileStore();
            profile.PutStorageSettings(profile.GetStorageSettings() with { SaveDirectory = _dir });
            _scan = new CaptureScanService(profile, _db, logger: null);
        }

        [TearDown]
        public void TearDown() {
            _scan.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Reset_clears_frames_and_sessions_then_rescans_the_store() {
            var session = Guid.NewGuid();
            await using (var conn = _db.OpenConnection()) {
                await using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
                    cmd.Parameters.AddWithValue("$id", session.ToString());
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                    await cmd.ExecuteNonQueryAsync();
                }
                await using (var cmd = conn.CreateCommand()) {
                    cmd.CommandText = """
                        INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                                            exposure_seconds, captured_utc, file_path, file_size_bytes,
                                            width, height, bit_depth)
                        VALUES ($id, $sid, 'M31', 'light', 'L', 300, $t, '/gone/old-drive/a.fits', 1, 10, 10, 16);
                        """;
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                    cmd.Parameters.AddWithValue("$sid", session.ToString());
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var (framesCleared, sessionsCleared, scan) =
                await _scan.ResetAndRescanAsync(CancellationToken.None);

            Assert.That(framesCleared, Is.EqualTo(1), "the stale drive's frame row is gone");
            Assert.That(sessionsCleared, Is.EqualTo(1));
            Assert.That(scan.Ran, Is.True, "the rebuilding scan ran against the mounted store");
            Assert.That(scan.FramesRecovered, Is.Zero, "empty store → empty catalog, honestly");
            await using var verify = _db.OpenConnection();
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT (SELECT COUNT(*) FROM frames) + (SELECT COUNT(*) FROM sessions);";
            Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.Zero);
        }

        [Test]
        public void LooksLikeFits_accepts_both_extensions_and_skips_appledouble() {
            Assert.That(CaptureScanService.LooksLikeFits("/d/a.fits"), Is.True);
            Assert.That(CaptureScanService.LooksLikeFits("/d/Light_NGC6188_0004.fit"), Is.True, "NINA single-t extension");
            Assert.That(CaptureScanService.LooksLikeFits("/d/B.FIT"), Is.True, "case-insensitive");
            Assert.That(CaptureScanService.LooksLikeFits("/d/._Light_NGC6188_0004.fit"), Is.False, "macOS AppleDouble fork");
            Assert.That(CaptureScanService.LooksLikeFits("/d/readme.txt"), Is.False);
        }
    }
}
