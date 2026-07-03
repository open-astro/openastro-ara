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
        public async Task Bulk_export_tars_the_existing_files_and_skips_missing() {
            var (idA, fitsA) = await InsertFrameWithFilesAsync();
            var (idB, fitsB) = await InsertFrameWithFilesAsync();
            File.Delete(fitsB); // rotated volume — must not fail the rest

            var prep = await _repo.PrepareExportAsync(
                new BulkExportRequestDto(FrameIds: [idA, idB]), CancellationToken.None);

            Assert.That(prep, Is.Not.Null);
            Assert.That(prep!.FileName, Does.StartWith("openastroara-frames-").And.EndWith(".tar"));
            Assert.That(prep.Entries.Count, Is.EqualTo(1), "the missing file is skipped at open time");
            var names = await StreamAndListEntriesAsync(prep);
            Assert.That(names, Is.EqualTo(new[] { Path.GetFileName(fitsA) }),
                "the existing file is in the tar; the missing one is skipped");
        }

        [Test]
        public async Task Bulk_export_dedupes_names_that_collide_with_a_prior_rename() {
            // r2: basenames frame_1.fits, frame.fits, frame.fits — the naive
            // single-suffix rename of the third would collide with the first.
            var idA = await InsertFrameWithNamedFileAsync("frame_1.fits", subdir: "a");
            var idB = await InsertFrameWithNamedFileAsync("frame.fits", subdir: "b");
            var idC = await InsertFrameWithNamedFileAsync("frame.fits", subdir: "c");

            var prep = await _repo.PrepareExportAsync(
                new BulkExportRequestDto(FrameIds: [idA, idB, idC]), CancellationToken.None);

            Assert.That(prep!.Entries.Count, Is.EqualTo(3));
            var names = await StreamAndListEntriesAsync(prep);
            Assert.That(names, Is.Unique, "no tar entry may clobber another on extract");
            Assert.That(names.Count, Is.EqualTo(3));
        }

        // Streams the prep exactly like the endpoint's callback, then reads the
        // entry names back — proving the open handles produce a valid tar.
        private static async Task<List<string>> StreamAndListEntriesAsync(FrameExportPrep prep) {
            var ms = new MemoryStream();
            await using (var _ = prep)
            await using (var tar = new System.Formats.Tar.TarWriter(ms, leaveOpen: true)) {
                foreach (var (stream, name) in prep.Entries) {
                    var entry = new System.Formats.Tar.PaxTarEntry(
                        System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = stream };
                    await tar.WriteEntryAsync(entry, CancellationToken.None);
                }
            }
            ms.Position = 0;
            var names = new List<string>();
            await using (var reader = new System.Formats.Tar.TarReader(ms)) {
                while (await reader.GetNextEntryAsync(cancellationToken: CancellationToken.None) is { } entry) {
                    names.Add(entry.Name);
                }
            }
            return names;
        }

        private async Task<Guid> InsertFrameWithNamedFileAsync(string fileName, string subdir) {
            var dir = Path.Combine(_dir, subdir);
            Directory.CreateDirectory(dir);
            var fits = Path.Combine(dir, fileName);
            await File.WriteAllTextAsync(fits, "FITS");
            var id = Guid.NewGuid();
            await _repo.InsertAsync(new FrameDto(
                Id: id, SessionId: Session, TargetName: "M31", FrameType: FrameType.Light,
                FilterName: "Ha", ExposureSeconds: 300, Gain: 100, Offset: 10,
                TemperatureC: -10, CapturedUtc: DateTimeOffset.UtcNow, FilePath: fits,
                FileSizeBytes: 4, Width: 100, Height: 100, BitDepth: 16, Hfr: null,
                StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
                QualityScore: null, Rating: 0, Tags: []), CancellationToken.None);
            return id;
        }

        [Test]
        public async Task Bulk_export_with_nothing_exportable_returns_null() {
            var (id, fits) = await InsertFrameWithFilesAsync();
            File.Delete(fits);
            var prep = await _repo.PrepareExportAsync(
                new BulkExportRequestDto(FrameIds: [id, Guid.NewGuid()]), CancellationToken.None);
            Assert.That(prep, Is.Null, "unknown ids + missing files -> the endpoint 404s");
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
