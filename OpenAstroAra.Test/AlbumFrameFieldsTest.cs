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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Fits;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sky Album slice 2 (#1517, docs/specs/sky-album-v1.md "Daemon"): each light frame keeps its J2000
    /// pointing, its pixel scale and whether Ara captured it, from the capture path, a drive rescan or
    /// the one-time startup backfill over older rows.
    /// </summary>
    [TestFixture]
    [Category("IO")] // real SQLite + FITS files on disk
    public class AlbumFrameFieldsTest {
        private static readonly Guid Session = Guid.Parse("17171717-1717-1717-1717-171717171717");
        private static readonly DateTimeOffset Captured = new(2026, 3, 2, 11, 20, 31, TimeSpan.Zero);

        private string _dir = string.Empty;
        private string _store = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _repo = null!;
        private CaptureScanService _scan = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-album-{Guid.NewGuid():N}");
            _store = Path.Combine(_dir, "store");
            Directory.CreateDirectory(_store);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            var profile = new InMemoryProfileStore();
            profile.PutStorageSettings(profile.GetStorageSettings() with { SaveDirectory = _store });
            _repo = new SqliteFrameRepository(_db, profile);
            _scan = new CaptureScanService(profile, _db, logger: null);
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            _scan.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task A_capture_stores_J2000_pointing_and_pixel_scale_and_counts_as_Ara() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.EnsureManualCaptureSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Session);
            var profile = new InMemoryProfileStore();
            profile.PutOpticsSettings(profile.GetOpticsSettings() with { FocalLengthMm = 500, ReducerFactor = 0.8, PixelSizeUm = 3.76 });
            using var camera = new CameraService(frames: frames.Object, profileStore: profile);
            var path = Path.Combine(_dir, "capture.fits");
            await File.WriteAllBytesAsync(path, new byte[64]);

            var j2000 = Guid.NewGuid();
            await camera.RegisterFrameAsync(j2000, new ExposureRequestDto(180, 100, BinX: 2, BinY: 2), FrameType.Light, "M31",
                Captured, path, 100, 80, appliedBin: 2, pointing: new CameraService.FramePointing(0.712, 41.27, Epoch.J2000));
            // 206.265 x (3.76 um x bin 2) / (500 mm x 0.8) = 3.8778 arcsec per stored pixel.
            frames.Verify(f => f.InsertAsync(It.Is<FrameDto>(d => d.Id == j2000
                && d.RaDeg.HasValue && Math.Abs(d.RaDeg.Value - 10.68) < 1e-9 && d.DecDeg == 41.27
                && d.PixelScaleArcsec.HasValue && Math.Abs(d.PixelScaleArcsec.Value - 3.8778) < 1e-3
                && d.CapturedByAra == true), It.IsAny<CancellationToken>()), Times.Once);

            var jnow = Guid.NewGuid();
            await camera.RegisterFrameAsync(jnow, new ExposureRequestDto(180, 100), FrameType.Light, "M31",
                Captured, path, 100, 80, pointing: new CameraService.FramePointing(0.72, 41.3, Epoch.JNOW));
            frames.Verify(f => f.InsertAsync(It.Is<FrameDto>(d => d.Id == jnow
                && d.RaDeg == null && d.DecDeg == null && d.CapturedByAra == true), It.IsAny<CancellationToken>()), Times.Once,
                "JNOW (SOFA missing) stores no pointing: the album falls back to the target name");
        }

        [Test]
        public async Task The_repository_round_trips_the_album_fields() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id, "x.fits") with { RaDeg = 10.68, DecDeg = 41.27, PixelScaleArcsec = 1.56, CapturedByAra = true },
                CancellationToken.None);
            var got = (await _repo.GetAsync(id, CancellationToken.None))!;
            Assert.That((got.RaDeg, got.DecDeg, got.PixelScaleArcsec, got.CapturedByAra), Is.EqualTo((10.68, 41.27, 1.56, (bool?)true)));
            Assert.That(await ScalarAsync("SELECT updated_utc FROM frames WHERE id = $p;", id.ToString()), Is.Not.Null,
                "insert stamps updated_utc");
        }

        [Test]
        public async Task The_backfill_fills_older_rows_from_their_headers_once() {
            var ara = await OldRowAsync("ara.fits", h => {
                h.SetHeader("SWCREATE", "OpenAstro Ara");
                h.SetHeader("OBJCTRA", "00 42 44.3");
                h.SetHeader("OBJCTDEC", "+41 16 09");
                h.SetHeader("EQUINOX", 2000.0);
                h.SetHeader("FOCALLEN", 400.0);
                h.SetHeader("XPIXSZ", 7.52);
            });
            var nina = await OldRowAsync("nina.fits", h => {
                h.SetHeader("SWCREATE", "N.I.N.A. 3.1.2.9001");
                h.SetHeader("OBJCTRA", "00 42 44.3");
                h.SetHeader("OBJCTDEC", "+41 16 09");
                h.SetHeader("EQUINOX", 2000.0);
            });
            var jnow = await OldRowAsync("jnow.fits", h => {
                h.SetHeader("SWCREATE", "OpenAstro Ara");
                h.SetHeader("OBJCTRA", "00 43 59.0");
                h.SetHeader("OBJCTDEC", "+41 22 30");
                h.SetHeader("EQUINOX", 2026.17);
            });
            var bare = await OldRowAsync("bare.fits", _ => { });
            var missing = Guid.NewGuid();
            await _repo.InsertAsync(Frame(missing, Path.Combine(_store, "gone.fits")), CancellationToken.None);

            await _scan.BackfillAlbumFieldsAsync(CancellationToken.None);

            var a = (await _repo.GetAsync(ara, CancellationToken.None))!;
            Assert.That(a.RaDeg, Is.EqualTo(10.6846).Within(1e-3));
            Assert.That(a.DecDeg, Is.EqualTo(41.269).Within(1e-3));
            // XPIXSZ is already binned: 206.265 x 7.52 / 400, no further bin factor.
            Assert.That(a.PixelScaleArcsec, Is.EqualTo(3.8778).Within(1e-3));
            Assert.That(a.CapturedByAra, Is.True);

            var n = (await _repo.GetAsync(nina, CancellationToken.None))!;
            Assert.That(n.CapturedByAra, Is.False, "a NINA header is a foreign frame");
            Assert.That(n.RaDeg, Is.Not.Null);

            var j = (await _repo.GetAsync(jnow, CancellationToken.None))!;
            Assert.That((j.RaDeg, j.DecDeg, j.CapturedByAra), Is.EqualTo(((double?)null, (double?)null, (bool?)true)),
                "only EQUINOX 2000.0 pointing is stored");

            var b = (await _repo.GetAsync(bare, CancellationToken.None))!;
            Assert.That((b.RaDeg, b.PixelScaleArcsec, b.CapturedByAra), Is.EqualTo(((double?)null, (double?)null, (bool?)false)));
            Assert.That(await ProbedAsync(bare), Is.Not.Null, "a header without the cards is still probed");
            Assert.That(await ProbedAsync(missing), Is.Null, "an unreadable file is probed again next boot");

            var stamps = await ScalarAsync("SELECT group_concat(id || updated_utc || pointing_probed_utc) FROM frames WHERE id = $p;", ara.ToString());
            await _scan.BackfillAlbumFieldsAsync(CancellationToken.None);
            Assert.That(await ScalarAsync("SELECT group_concat(id || updated_utc || pointing_probed_utc) FROM frames WHERE id = $p;", ara.ToString()),
                Is.EqualTo(stamps), "the second pass touches nothing");
        }

        [Test]
        public async Task A_rescan_recovers_the_album_fields_with_the_orphan() {
            WriteFits(Path.Combine(_store, "orphan.fits"), h => {
                h.SetHeader("SWCREATE", "OpenAstro Ara");
                h.SetHeader("OBJCTRA", "00 42 44.3");
                h.SetHeader("OBJCTDEC", "+41 16 09");
                h.SetHeader("EQUINOX", 2000.0);
                h.SetHeader("FOCALLEN", 400.0);
                h.SetHeader("XPIXSZ", 3.76);
            });

            await _scan.RunAsync(CancellationToken.None);

            var id = await ScalarAsync("SELECT id FROM frames WHERE file_path LIKE $p;", "%orphan.fits");
            var got = (await _repo.GetAsync(Guid.Parse(id!), CancellationToken.None))!;
            Assert.That(got.RaDeg, Is.EqualTo(10.6846).Within(1e-3));
            Assert.That(got.PixelScaleArcsec, Is.EqualTo(1.9389).Within(1e-3));
            Assert.That(got.CapturedByAra, Is.True);
            Assert.That(await ProbedAsync(got.Id), Is.Not.Null, "the rescan read the header, so the backfill skips the row");
        }

        private async Task<Guid> OldRowAsync(string name, Action<FitsImage> headers) {
            var path = Path.Combine(_store, name);
            WriteFits(path, headers);
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id, path), CancellationToken.None);
            // A row from a build before the album columns: nothing stamped yet.
            await ScalarAsync("UPDATE frames SET updated_utc = NULL WHERE id = $p;", id.ToString());
            return id;
        }

        private static void WriteFits(string path, Action<FitsImage> headers) {
            using var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort);
            fits.SetHeader("IMAGETYP", "LIGHT");
            fits.SetHeader("OBJECT", "M31");
            headers(fits);
            fits.WriteImageData(new ushort[16]);
            fits.Complete();
        }

        private Task<string?> ProbedAsync(Guid id) =>
            ScalarAsync("SELECT pointing_probed_utc FROM frames WHERE id = $p;", id.ToString());

        private static FrameDto Frame(Guid id, string path) => new(
            Id: id, SessionId: Session, TargetName: "M31", FrameType: FrameType.Light, FilterName: "Ha",
            ExposureSeconds: 180, Gain: 100, Offset: 10, TemperatureC: -10.0, CapturedUtc: Captured,
            FilePath: path, FileSizeBytes: 1000, Width: 4, Height: 4, BitDepth: 16,
            Hfr: null, StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null, Rating: 0, Tags: Array.Empty<string>());

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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Every caller passes a compile-time-constant query; values travel via the $p parameter.")]
        private async Task<string?> ScalarAsync(string sql, string param) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", param);
            var value = await cmd.ExecuteScalarAsync(CancellationToken.None);
            return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
