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
    /// §40.8 — BulkDeleteAsync's DeleteFromDisk flag (from the #675 review: the flag used to be
    /// a silent no-op behind a UI checkbox that implied real deletion). With the flag the FITS
    /// file and its §65.4 sidecars (default/variant previews, thumbnail) are removed; without
    /// it only the catalog row goes. Unrelated neighbors always survive.
    /// </summary>
    [TestFixture]
    public class BulkDeleteDiskTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _repo = null!;
        private static readonly Guid Session = Guid.Parse("55555555-5555-5555-5555-555555555551");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-bulkdel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
            cmd.Parameters.AddWithValue("$id", Session.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private async Task<(Guid Id, string FitsPath)> InsertFrameWithFilesAsync() {
            var id = Guid.NewGuid();
            var fits = Path.Combine(_dir, $"frame_{id:N}.fits");
            await File.WriteAllTextAsync(fits, "FITS");
            await File.WriteAllTextAsync(Path.ChangeExtension(fits, null) + ".preview.jpg", "p");
            await File.WriteAllTextAsync(Path.ChangeExtension(fits, null) + ".preview.auto_stf.jpg", "v");
            await File.WriteAllTextAsync(Path.ChangeExtension(fits, null) + ".thumb.jpg", "t");
            await _repo.InsertAsync(new FrameDto(
                Id: id, SessionId: Session, TargetName: "M31", FrameType: FrameType.Light,
                FilterName: "Ha", ExposureSeconds: 300, Gain: 100, Offset: 10,
                TemperatureC: -10, CapturedUtc: DateTimeOffset.UtcNow, FilePath: fits,
                FileSizeBytes: 4, Width: 100, Height: 100, BitDepth: 16, Hfr: null,
                StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
                QualityScore: null, Rating: 0, Tags: []), CancellationToken.None);
            return (id, fits);
        }

        [Test]
        public async Task DeleteFromDisk_removes_fits_and_sidecars_but_not_neighbors() {
            var (id, fits) = await InsertFrameWithFilesAsync();
            var (survivorId, survivorFits) = await InsertFrameWithFilesAsync();
            var stem = Path.ChangeExtension(fits, null);

            await _repo.BulkDeleteAsync(
                new BulkDeleteRequestDto(FrameIds: [id], DeleteFromDisk: true),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null, "catalog row gone");
            Assert.That(File.Exists(fits), Is.False, "FITS deleted");
            Assert.That(File.Exists(stem + ".preview.jpg"), Is.False, "default preview deleted");
            Assert.That(File.Exists(stem + ".preview.auto_stf.jpg"), Is.False, "variant deleted");
            Assert.That(File.Exists(stem + ".thumb.jpg"), Is.False, "thumbnail deleted");
            // The other frame's row + files are untouched.
            Assert.That(await _repo.GetAsync(survivorId, CancellationToken.None), Is.Not.Null);
            Assert.That(File.Exists(survivorFits), Is.True);
        }

        [Test]
        public async Task Bulk_move_reassigns_frames_to_an_existing_session() {
            var (id, _) = await InsertFrameWithFilesAsync();
            var sessionB = Guid.Parse("55555555-5555-5555-5555-5555555555b2");
            await using (var conn = _db.OpenConnection()) {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
                cmd.Parameters.AddWithValue("$id", sessionB.ToString());
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync();
            }

            await _repo.BulkMoveAsync(
                new BulkMoveRequestDto(FrameIds: [id], TargetSessionId: sessionB),
                idempotencyKey: null, CancellationToken.None);

            var moved = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(moved!.SessionId, Is.EqualTo(sessionB));
        }

        [Test]
        public async Task Bulk_move_to_an_unknown_session_is_a_validation_error() {
            var (id, _) = await InsertFrameWithFilesAsync();
            var ex = Assert.ThrowsAsync<ArgumentException>(() => _repo.BulkMoveAsync(
                new BulkMoveRequestDto(FrameIds: [id], TargetSessionId: Guid.NewGuid()),
                idempotencyKey: null, CancellationToken.None));
            Assert.That(ex!.ParamName, Is.EqualTo("request"),
                "the endpoint's 422 catch filters on ParamName == request");
            var frame = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(frame!.SessionId, Is.EqualTo(Session), "nothing moved");
        }

        [Test]
        public async Task Catalog_only_delete_leaves_the_files_on_disk() {
            var (id, fits) = await InsertFrameWithFilesAsync();

            await _repo.BulkDeleteAsync(
                new BulkDeleteRequestDto(FrameIds: [id], DeleteFromDisk: false),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null, "catalog row gone");
            Assert.That(File.Exists(fits), Is.True, "the FITS file stays without the flag");
        }

        [Test]
        public async Task A_missing_file_does_not_block_the_catalog_delete() {
            var (id, fits) = await InsertFrameWithFilesAsync();
            File.Delete(fits); // volume rotated / user tidied manually

            await _repo.BulkDeleteAsync(
                new BulkDeleteRequestDto(FrameIds: [id], DeleteFromDisk: true),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null,
                "best-effort disk deletion never blocks the catalog removal");
        }
    }
}
