#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Fits;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28.8 orphan recovery vs live sessions (#932 review, round 2). The
    /// per-(target, night) session probe derives membership from existing
    /// frames, so an orphan surfacing for the same target a LIVE session is
    /// capturing tonight would — unguarded — adopt that live session, and
    /// FinalizeRecoverySessionsAsync would then stamp an end time onto a
    /// session the orchestrator still owns (ended_at IS NULL is the "in
    /// progress" signal EndSessionAsync keys on). These pin the guard:
    /// live sessions are never adopted, ended ones still are (that reuse is
    /// what keeps restarts from minting duplicate "target — night" rows).
    /// </summary>
    [TestFixture]
    public class CaptureScanRecoverySessionTest {

        // One fixed instant for the DB frame and the orphan's DATE-OBS: the
        // night window is derived from LOCAL noon, so any two distinct times
        // could straddle a noon boundary under some host offset — the same
        // instant can't, keeping the test deterministic in every timezone.
        private static readonly DateTimeOffset Captured =
            new(2026, 5, 30, 3, 14, 0, TimeSpan.Zero);
        private const string Target = "NGC 6188";

        private string _root = string.Empty;
        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private CaptureScanService _scan = null!;

        [SetUp]
        public async Task SetUp() {
            _root = Path.Combine(Path.GetTempPath(), $"oara-recsess-{Guid.NewGuid():N}");
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-recsessp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_profileDir);

            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);

            var profile = new InMemoryProfileStore();
            profile.PutStorageSettings(
                profile.GetStorageSettings() with { SaveDirectory = _root });
            _scan = new CaptureScanService(profile, _db, logger: null);
        }

        [TearDown]
        public void TearDown() {
            _scan.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Orphan_never_adopts_a_live_session_for_the_same_target_and_night() {
            var liveId = Guid.NewGuid();
            await InsertSessionAsync(liveId, endedAt: null);
            await InsertFrameAsync(liveId, Path.Combine(_root, "live-0001.fits"));
            WriteOrphanFits(Path.Combine(_root, "stray-0001.fits"));

            var result = await _scan.RunAsync(CancellationToken.None);
            Assert.That(result.FramesRecovered, Is.EqualTo(1));

            var orphanSession = await ScalarAsync(
                "SELECT session_id FROM frames WHERE file_path LIKE $p;", "%stray-0001.fits");
            Assert.That(orphanSession, Is.Not.Null.And.Not.EqualTo(liveId.ToString()),
                "the orphan must get its own recovery session, not the in-progress one");

            var liveEndedAt = await ScalarAsync(
                "SELECT ended_at FROM sessions WHERE id = $p;", liveId.ToString());
            Assert.That(liveEndedAt, Is.Null,
                "recovery must never stamp an end time onto a session the orchestrator still owns");
            var liveCount = await ScalarAsync(
                "SELECT frame_count FROM sessions WHERE id = $p;", liveId.ToString());
            Assert.That(liveCount, Is.EqualTo("0"),
                "the live session's bookkeeping is the orchestrator's, not the scan's");
        }

        [Test]
        public async Task Orphan_folds_into_an_already_ended_session_for_the_same_target_and_night() {
            var endedId = Guid.NewGuid();
            await InsertSessionAsync(endedId, endedAt: Captured);
            await InsertFrameAsync(endedId, Path.Combine(_root, "kept-0001.fits"));
            WriteOrphanFits(Path.Combine(_root, "stray-0002.fits"));

            var result = await _scan.RunAsync(CancellationToken.None);
            Assert.That(result.FramesRecovered, Is.EqualTo(1));

            var orphanSession = await ScalarAsync(
                "SELECT session_id FROM frames WHERE file_path LIKE $p;", "%stray-0002.fits");
            Assert.That(orphanSession, Is.EqualTo(endedId.ToString()),
                "same-night strays belong in the night's ended session — that reuse is what "
                + "keeps a rescan from minting duplicate library rows");
            var count = await ScalarAsync(
                "SELECT frame_count FROM sessions WHERE id = $p;", endedId.ToString());
            Assert.That(count, Is.EqualTo("2"), "finalize re-counts the adopted session's frames");
        }

        private async Task InsertSessionAsync(Guid id, DateTimeOffset? endedAt) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions
                    (id, profile_id, sequence_json, started_at, ended_at,
                     recovery_needed, last_completed_instruction_id,
                     current_target_id, frame_count)
                VALUES ($id, NULL, NULL, $started, $ended, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$started", Captured.ToString("O"));
            cmd.Parameters.AddWithValue("$ended",
                endedAt is null ? DBNull.Value : endedAt.Value.ToString("O"));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // file_path deliberately names a file that does not exist on disk:
        // the scan derives orphans from the directory walk, so a DB-only row
        // is exactly a frame the orchestrator recorded normally.
        private async Task InsertFrameAsync(Guid sessionId, string filePath) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames
                    (id, session_id, target_name, frame_type, filter_name,
                     exposure_seconds, captured_utc, file_path, file_size_bytes,
                     width, height, bit_depth)
                VALUES ($id, $sid, $target, 'light', NULL, 60, $captured, $path, 1, 4, 4, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
            cmd.Parameters.AddWithValue("$target", Target);
            cmd.Parameters.AddWithValue("$captured", Captured.ToString("O"));
            cmd.Parameters.AddWithValue("$path", filePath);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private static void WriteOrphanFits(string path) {
            using var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort);
            fits.SetHeader("OBJECT", Target);
            // Zoneless DATE-OBS is UTC per the FITS spec (CaptureScanDateObsTest).
            fits.SetHeader("DATE-OBS", Captured.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            fits.SetHeader("IMAGETYP", "LIGHT");
            fits.WriteImageData(new ushort[16]);
            fits.Complete();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Every caller passes a compile-time-constant query; values travel via the $p parameter.")]
        private async Task<string?> ScalarAsync(string sql, string param) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", param);
            var value = await cmd.ExecuteScalarAsync(CancellationToken.None);
            return value is null or DBNull ? null : Convert.ToString(
                value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
