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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1148 — Settings → Storage's file format reaches disk. XISF frames are written for real
    /// (lossless Zstandard + byte shuffling when compression is on, and for darks/biases under
    /// "Compress darks and bias"), carry the FITS path's keyword set, and every library reader
    /// (catalog scan, header hint, preview/histogram) opens them. FITS stays plain and uncompressed.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class FrameFileFormatTest {

        private static readonly DateTimeOffset Captured = new(2026, 10, 27, 3, 14, 15, 250, TimeSpan.Zero);

        // CFITSIO's own structural cards; the XISF Image element carries these as attributes.
        private static readonly HashSet<string> Structural = new(StringComparer.OrdinalIgnoreCase) {
            "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "BZERO", "BSCALE",
        };

        // Every group the capture path writes, so the comparison below covers the whole set.
        private static readonly string[] ExpectedKeywords = {
            "IMAGETYP", "EXPTIME", "GAIN", "OFFSET", "XBINNING", "YBINNING", "DATE-OBS", "FILTER", "FOCUSPOS", "ROTATOR",
            "SWCREATE", "OBJECT", "OBJCTRA", "OBJCTDEC", "RA", "DEC", "EQUINOX", "CCD-TEMP", "SET-TEMP",
            "TELESCOP", "FOCALLEN", "APTDIA", "XPIXSZ", "YPIXSZ", "OBSERVER", "SITELAT", "SITELONG", "SITEELEV",
            "SQM", "SKYTEMP", "AMBTEMP", "HUMIDITY", "PRESSURE", "WINDSPD", "SUNALT", "MOONALT", "MOONILL", "MOONPHSE",
        };

        private string _root = string.Empty;
        private InMemoryProfileStore _profile = null!;
        private CameraService _camera = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), $"oara-1148-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _profile = new InMemoryProfileStore();
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { SaveDirectory = _root });
            // Every optional header group populated, so the comparison covers the whole set.
            _profile.PutSiteSettings(_profile.GetSiteSettings() with { LatitudeDeg = 47.5, LongitudeDeg = -122.25, ElevationM = 120, ObserverName = "J. Troy" });
            _profile.PutOpticsSettings(_profile.GetOpticsSettings() with { FocalLengthMm = 250, ReducerFactor = 1, ApertureMm = 51, PixelSizeUm = 3.76, TelescopeName = "RedCat 51" });
            _camera = new CameraService(profileStore: _profile, fallbackFramesDir: _root);
        }

        [TearDown]
        public void TearDown() {
            _camera.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void UseStorage(string format, string compression, bool compressDarksAndBias = false) {
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { FileFormat = format, Compression = compression });
            _profile.PutFilenamesSettings(_profile.GetFilenamesSettings() with { CompressDarksAndBias = compressDarksAndBias });
        }

        // A smooth gradient compresses (random noise can come out larger, and the writer then
        // stores it raw), with both extremes present.
        private static ushort[] Pattern(int w, int h) {
            var px = new ushort[w * h];
            for (var i = 0; i < px.Length; i++) {
                px[i] = (ushort)(i * 7 % 4096 + 1000);
            }
            px[0] = 0;
            px[^1] = ushort.MaxValue;
            return px;
        }

        private string Capture(ushort[] pixels, int w, int h, string imageType = "LIGHT", FrameWriteOptions? options = null) =>
            _camera.WriteFrame(Guid.NewGuid(), pixels, w, h,
                new ExposureRequestDto(ExposureSec: 180, Gain: 100, BinX: 1, BinY: 1, FilterName: "Ha", CameraOffset: 50),
                imageType, Captured, focuserPosition: 12345, targetName: "NGC 7000", sensorTemp: -10.2, tempSetPoint: -10,
                conditions: new ObservingConditionsDto("w", "Weather", EquipmentConnectionState.Connected,
                    TemperatureC: 8.5, HumidityPct: 71, DewPointC: 3.4, PressureHpa: 1013.2, CloudCoverPct: 5,
                    WindSpeedMs: 2.5, WindGustMs: 4, WindDirectionDeg: 270, RainRate: 0, Safe: true, CapturedAt: "",
                    SkyQualityMagArcsec2: 20.8, SkyTemperatureC: -25),
                pointing: new CameraService.FramePointing(RaHours: 20.98, DecDegrees: 44.33, Epoch.J2000),
                rotatorMechanicalDeg: 34.8, options: options);

        private static string? CompressionAttribute(string path) =>
            XISF.ReadHeader(path).Image?.Attribute("compression")?.Value;

        private static string[] TempFiles(string root) =>
            Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories);

        [Test]
        public void An_xisf_frame_reads_back_with_the_fits_frames_pixels_and_keywords() {
            const int w = 33, h = 17;
            var pixels = Pattern(w, h);
            UseStorage("fits", "zstd");
            var fitsPath = Capture(pixels, w, h);
            UseStorage("xisf", "zstd");
            var xisfPath = Capture(pixels, w, h);

            Assert.That(Path.GetExtension(fitsPath), Is.EqualTo(".fits"));
            Assert.That(Path.GetExtension(xisfPath), Is.EqualTo(".xisf"));
            var fits = FrameFileReader.Read(fitsPath);
            var xisf = FrameFileReader.Read(xisfPath);
            Assert.Multiple(() => {
                Assert.That(xisf.Width, Is.EqualTo(w));
                Assert.That(xisf.Height, Is.EqualTo(h));
                Assert.That(xisf.Pixels, Is.EqualTo(pixels), "lossless");
                Assert.That(fits.Pixels, Is.EqualTo(pixels));
            });

            var fitsKeys = fits.Headers.Where(kv => !Structural.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var xisfKeys = xisf.Headers.ToDictionary(kv => kv.Key, kv => kv.Value);
            // Every group the capture path writes is in the set being compared.
            Assert.That(fitsKeys.Keys, Is.SupersetOf(ExpectedKeywords));
            Assert.That(xisfKeys, Is.EquivalentTo(fitsKeys), "same keywords, same card text, from either container");
            // The values the library readers parse come out usable.
            Assert.Multiple(() => {
                Assert.That(SqliteFrameRepository.BinningFromHeaders(xisf.Headers), Is.EqualTo(1));
                Assert.That(SqliteFrameRepository.ParseTargetCoordinates(xisf.Headers["OBJCTRA"], xisf.Headers["OBJCTDEC"]), Is.Not.Null);
                Assert.That(CaptureScanService.ParseDateObs(xisf.Headers), Is.EqualTo(CaptureScanService.ParseDateObs(fits.Headers)));
            });
        }

        [Test]
        public void An_xisf_frame_carries_the_spec_image_type_and_native_properties() {
            UseStorage("xisf", "zstd");
            var path = Capture(Pattern(8, 8), 8, 8, imageType: "DARKFLAT");
            var image = XISF.ReadHeader(path).Image!;
            var ns = image.Name.Namespace;
            string? Property(string id) => image.Elements(ns + "Property").FirstOrDefault(p => p.Attribute("id")?.Value == id)?.Attribute("value")?.Value
                ?? image.Elements(ns + "Property").FirstOrDefault(p => p.Attribute("id")?.Value == id)?.Value;
            Assert.Multiple(() => {
                Assert.That(image.Attribute("imageType")?.Value, Is.EqualTo("Dark"), "XISF 1.0 has no DARKFLAT; a dark flat is a dark");
                Assert.That(FrameFileReader.ReadHeaders(path)["IMAGETYP"], Is.EqualTo("DARKFLAT"), "the keyword keeps Ara's own type");
                Assert.That(Property("Instrument:ExposureTime"), Is.EqualTo("180"));
                Assert.That(Property("Instrument:Filter:Name"), Is.EqualTo("Ha"));
                Assert.That(double.Parse(Property("Instrument:Telescope:FocalLength")!, CultureInfo.InvariantCulture), Is.EqualTo(0.25).Within(1e-9), "metres in XISF");
                Assert.That(Property("Observation:Time:Start"), Is.EqualTo("2026-10-27T03:14:15.250Z"));
            });
        }

        [Test]
        public void Compression_follows_the_profile_for_lights_and_the_darks_and_bias_switch_for_calibration() {
            UseStorage("xisf", "zstd");
            Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64)), Does.StartWith("zstd+sh:"), "compression on: zstd with byte shuffling");

            UseStorage("xisf", "off");
            Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64)), Is.Null, "compression off: a light is stored raw");

            UseStorage("xisf", "off", compressDarksAndBias: true);
            Assert.Multiple(() => {
                Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "DARK")), Does.StartWith("zstd+sh:"));
                Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "BIAS")), Does.StartWith("zstd+sh:"));
                Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "DARKFLAT")), Does.StartWith("zstd+sh:"));
                Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "LIGHT")), Is.Null, "the switch is for darks and biases only");
                Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "FLAT")), Is.Null);
            });

            UseStorage("xisf", "off", compressDarksAndBias: false);
            Assert.That(CompressionAttribute(Capture(Pattern(64, 64), 64, 64, "DARK")), Is.Null);
        }

        [Test]
        public void Fits_is_always_plain_whatever_the_compression_settings_say() {
            UseStorage("fits", "zstd", compressDarksAndBias: true);
            var path = Capture(Pattern(16, 16), 16, 16, "DARK");
            Assert.That(Path.GetExtension(path), Is.EqualTo(".fits"));
            var bytes = File.ReadAllBytes(path);
            Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 6), Is.EqualTo("SIMPLE"));
            Assert.That(FrameFileReader.Read(path).Headers.ContainsKey("ZCMPTYPE"), Is.False, "no tile compression");
            Assert.That(new FileInfo(path).Length, Is.GreaterThanOrEqualTo(16 * 16 * 2));
        }

        [Test]
        public void The_xisf_extension_follows_the_template_and_its_de_dup_and_no_tmp_is_left() {
            UseStorage("xisf", "zstd");
            var first = Capture(Pattern(8, 8), 8, 8);
            var second = Capture(Pattern(8, 8), 8, 8);
            Assert.Multiple(() => {
                Assert.That(first, Does.EndWith(".xisf"));
                Assert.That(second, Is.EqualTo(Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " (2).xisf")),
                    "the same template expansion de-dups with the XISF extension");
                Assert.That(File.Exists(first) && File.Exists(second), Is.True);
                Assert.That(TempFiles(_root), Is.Empty, "the atomic write renamed its temp");
            });
        }

        [Test]
        public void The_id_fallback_name_uses_the_xisf_extension() {
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { FileFormat = "xisf", FilenameTemplate = "" });
            var id = Guid.NewGuid();
            var path = _camera.ResolveTemplatedPath(_root, id, new ExposureRequestDto(1, null), "LIGHT", Captured, null, null,
                extension: _camera.ResolveWriteOptions("LIGHT").Extension);
            Assert.That(path, Is.EqualTo(Path.Combine(_root, $"{id}.xisf")));
        }

        [Test]
        public void An_abandoned_xisf_write_leaves_neither_the_frame_nor_its_temp() {
            var path = Path.Combine(_root, "abandoned.xisf");
            using (var writer = FrameFileWriter.Create(path, 4, 4, new FrameWriteOptions(FrameFileFormat.Xisf, true))) {
                writer.WriteImageData(new ushort[16]);
                writer.SetHeader("IMAGETYP", "LIGHT", "frame type");
            }
            Assert.That(File.Exists(path), Is.False);
            Assert.That(TempFiles(_root), Is.Empty);
        }

        [Test]
        public void A_stale_temp_from_a_crash_does_not_block_the_next_xisf_write() {
            var path = Path.Combine(_root, "frame.xisf");
            File.WriteAllText(path + ".tmp", "torn");
            using (var writer = FrameFileWriter.Create(path, 2, 2, new FrameWriteOptions(FrameFileFormat.Xisf, false))) {
                writer.WriteImageData(new ushort[] { 1, 2, 3, 4 });
                writer.Complete();
            }
            Assert.That(FrameFileReader.Read(path).Pixels, Is.EqualTo(new ushort[] { 1, 2, 3, 4 }));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }

        [Test]
        public void The_reader_dispatches_on_the_extension() {
            var fitsPath = Path.Combine(_root, "a.fit");
            var xisfPath = Path.Combine(_root, "a.XISF");
            foreach (var (path, options) in new[] { (fitsPath, FrameWriteOptions.PlainFits), (xisfPath, new FrameWriteOptions(FrameFileFormat.Xisf, true)) }) {
                using var writer = FrameFileWriter.Create(path, 3, 2, options);
                writer.WriteImageData(new ushort[] { 0, 1, 2, 65535, 4, 5 });
                writer.SetHeader("OBJECT", "M 31", "target name");
                writer.SetHeader("XBINNING", 2, "binning X");
                writer.Complete();
            }
            Assert.Multiple(() => {
                Assert.That(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(xisfPath), 0, 8), Is.EqualTo("XISF0100"));
                Assert.That(FrameFileReader.IsXisf(xisfPath) && FrameFileReader.IsFits(fitsPath), Is.True);
                Assert.That(FrameFileReader.IsFits(Path.Combine(_root, "b.fts")), Is.True);
                foreach (var path in new[] { fitsPath, xisfPath }) {
                    var frame = FrameFileReader.Read(path);
                    Assert.That(frame.Pixels, Is.EqualTo(new ushort[] { 0, 1, 2, 65535, 4, 5 }), path);
                    Assert.That((frame.Width, frame.Height), Is.EqualTo((3, 2)), path);
                    Assert.That(frame.Headers["OBJECT"], Is.EqualTo("M 31"), path);
                    Assert.That(SqliteFrameRepository.BinningFromHeaders(frame.Headers), Is.EqualTo(2), path);
                    var info = FrameFileReader.ReadHeaderInfo(path);
                    Assert.That((info.Width, info.Height), Is.EqualTo((3, 2)), path);
                }
            });
        }

        [Test]
        public void A_quote_in_a_string_keyword_survives_the_xisf_round_trip() {
            var path = Path.Combine(_root, "q.xisf");
            using (var writer = FrameFileWriter.Create(path, 1, 1, new FrameWriteOptions(FrameFileFormat.Xisf, false))) {
                writer.WriteImageData(new ushort[] { 7 });
                writer.SetHeader("OBJECT", "Barnard's Loop", "target name");
                writer.Complete();
            }
            Assert.That(FrameFileReader.ReadHeaders(path)["OBJECT"], Is.EqualTo("Barnard's Loop"));
        }

        [Test]
        public void A_corrupt_xisf_file_is_a_read_fault_not_a_crash() {
            var path = Path.Combine(_root, "bad.xisf");
            File.WriteAllText(path, "not an xisf file at all");
            var ex = Assert.Catch(() => FrameFileReader.ReadHeaders(path));
            Assert.That(FrameFileReader.IsReadFault(ex!), Is.True, ex!.GetType().Name);
        }

        [Test]
        public async Task The_catalog_scan_and_the_library_readers_open_an_xisf_frame() {
            var profileDir = Path.Combine(Path.GetTempPath(), $"oara-1148p-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            try {
                var db = new SqliteAraDatabase(profileDir, logger: null);
                await db.InitializeAsync(CancellationToken.None);
                UseStorage("xisf", "zstd");
                var pixels = Pattern(40, 30);
                var path = Capture(pixels, 40, 30);

                using (var scan = new CaptureScanService(_profile, db, logger: null)) {
                    var result = await scan.RunAsync(CancellationToken.None);
                    Assert.That(result.FramesRecovered, Is.EqualTo(1), "the scan picks up .xisf");
                }
                string id;
                await using (var conn = db.OpenConnection()) {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id, target_name, frame_type, filter_name, exposure_seconds, gain, width, height FROM frames WHERE file_path = $p;";
                    cmd.Parameters.AddWithValue("$p", path);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    Assert.That(await reader.ReadAsync(), Is.True);
                    id = reader.GetString(0);
                    Assert.Multiple(() => {
                        Assert.That(reader.GetString(1), Is.EqualTo("NGC 7000"));
                        Assert.That(reader.GetString(2), Is.EqualTo("light"));
                        Assert.That(reader.GetString(3), Is.EqualTo("Ha"));
                        Assert.That(reader.GetDouble(4), Is.EqualTo(180));
                        Assert.That(reader.GetInt32(5), Is.EqualTo(100));
                        Assert.That((reader.GetInt32(6), reader.GetInt32(7)), Is.EqualTo((40, 30)));
                    });
                }

                var repo = new SqliteFrameRepository(db, _profile, captureRoots: () => new[] { _root });
                var target = await repo.TryReadTargetCoordinatesAsync(Guid.Parse(id), CancellationToken.None);
                Assert.That(target, Is.Not.Null, "the solve hint reads OBJCTRA/OBJCTDEC from the XISF keywords");
                Assert.That(target!.Value.RaDegrees, Is.EqualTo(20.98 * 15).Within(0.01));
                var histogram = await repo.GetHistogramAsync(Guid.Parse(id), CancellationToken.None);
                Assert.That(histogram, Is.Not.Null, "the pixel readers decode the XISF block");
                var image = await repo.LoadImageDataAsync(Guid.Parse(id), null!, CancellationToken.None);
                Assert.That(image!.Data.FlatArray, Is.EqualTo(pixels));
            } finally {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---- review round 1 ----------------------------------------------------------------

        [Test]
        public void The_frame_counter_seeds_from_every_frame_extension_the_reader_knows() {
            var root = Path.Combine(_root, "counted");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            foreach (var name in new[] { "a.fits", "b.fit", "c.xisf", "d.fts", Path.Combine("nested", "e.XISF"), Path.Combine("nested", "f.FIT") }) {
                File.WriteAllText(Path.Combine(root, name), "x");
            }
            foreach (var name in new[] { "g.txt", "h.xisf.tmp", "i.thumb.jpg", "j.fits.tmp" }) {
                File.WriteAllText(Path.Combine(root, name), "x");
            }
            using var camera = new CameraService(profileStore: _profile, fallbackFramesDir: _root);
            Assert.That(camera.NextFrameNumber(root), Is.EqualTo(7), "six frames on disk (FITS as .fits/.fit/.fts, XISF) → the next is 7");
            Assert.That(camera.NextFrameNumber(root), Is.EqualTo(8), "then it climbs per capture");
        }

        private static void WriteForeignXisf(string path, string geometry, string colorSpace, string sampleFormat) {
            var header = new XISFHeader();
            header.AddImageMetaData(new OpenAstroAra.Image.ImageData.ImageProperties(2, 2, 16, false, 0, 0), "LIGHT");
            header.Image!.SetAttributeValue("geometry", geometry);
            header.Image.SetAttributeValue("colorSpace", colorSpace);
            header.Image.SetAttributeValue("sampleFormat", sampleFormat);
            var xisf = new XISF(header);
            xisf.AddAttachedImage(new ushort[12], new OpenAstroAra.Image.FileFormat.FileSaveInfo { FileType = OpenAstroAra.Core.Enums.FileType.XISF });
            using var fs = File.Create(path);
            xisf.Save(fs);
        }

        [Test]
        public async Task The_catalog_scan_skips_a_colour_or_non_16_bit_xisf_it_could_not_show() {
            var profileDir = Path.Combine(Path.GetTempPath(), $"oara-1148q-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            try {
                var db = new SqliteAraDatabase(profileDir, logger: null);
                await db.InitializeAsync(CancellationToken.None);
                UseStorage("xisf", "zstd");
                var ours = Capture(Pattern(8, 8), 8, 8);
                var rgb = Path.Combine(_root, "master_RGB.xisf");
                var float32 = Path.Combine(_root, "integration_L.xisf");
                WriteForeignXisf(rgb, "2:2:3", "RGB", "UInt16");
                WriteForeignXisf(float32, "2:2:1", "Gray", "Float32");

                Assert.Multiple(() => {
                    Assert.Throws<NotSupportedException>(() => FrameFileReader.ReadHeaderInfo(rgb));
                    Assert.Throws<NotSupportedException>(() => FrameFileReader.ReadHeaderInfo(float32));
                    Assert.That(FrameFileReader.ReadHeaderInfo(ours).Width, Is.EqualTo(8));
                });
                using (var scan = new CaptureScanService(_profile, db, logger: null)) {
                    var result = await scan.RunAsync(CancellationToken.None);
                    Assert.That(result.FramesRecovered, Is.EqualTo(1), "only the single-channel UInt16 frame is catalogued");
                }
                await using var conn = db.OpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT file_path FROM frames;";
                Assert.That(await cmd.ExecuteScalarAsync(), Is.EqualTo(ours));
                Assert.That(File.Exists(rgb) && File.Exists(float32), Is.True, "skipped files stay on disk");
            } finally {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void A_non_finite_double_card_is_refused_by_both_writers_alike(double value) {
            foreach (var (name, options) in new[] { ("nan.fits", FrameWriteOptions.PlainFits), ("nan.xisf", new FrameWriteOptions(FrameFileFormat.Xisf, false)) }) {
                var path = Path.Combine(_root, name);
                using (var writer = FrameFileWriter.Create(path, 2, 2, options)) {
                    writer.WriteImageData(new ushort[4]);
                    var ex = Assert.Throws<OpenAstroAra.Fits.FitsException>(() => writer.SetHeader("HUMIDITY", value, "relative humidity pct"), name);
                    Assert.That(ex!.StatusCode, Is.EqualTo(402), "CFITSIO's BAD_F2C");
                    writer.SetHeader("AMBTEMP", 8.5, "ambient temperature C");
                    writer.Complete();
                }
                var headers = FrameFileReader.ReadHeaders(path);
                Assert.That(headers.ContainsKey("HUMIDITY"), Is.False, name);
                Assert.That(headers["AMBTEMP"], Is.EqualTo("8.5"), name);
                File.Delete(path);
            }
        }

        [Test]
        public void A_long_string_card_reads_back_the_same_from_either_container() {
            // CFITSIO keeps at most 68 characters of a string card (an apostrophe counts twice once
            // doubled); the XISF FITSKeyword must carry what the FITS card does, not the whole value.
            var name = "Sh2-132 Lion Nebula 'deep' mosaic panel 3 of 6, " + new string('x', 60);
            var read = new Dictionary<string, string>();
            foreach (var (file, options) in new[] { ("long.fits", FrameWriteOptions.PlainFits), ("long.xisf", new FrameWriteOptions(FrameFileFormat.Xisf, false)) }) {
                var path = Path.Combine(_root, file);
                using (var writer = FrameFileWriter.Create(path, 2, 2, options)) {
                    writer.WriteImageData(new ushort[4]);
                    writer.SetHeader("OBJECT", name, "target name");
                    writer.Complete();
                }
                read[file] = FrameFileReader.ReadHeaders(path)["OBJECT"];
            }
            Assert.That(read["long.xisf"], Is.EqualTo(read["long.fits"]));
            Assert.That(read["long.fits"], Is.Not.EqualTo(name), "CFITSIO truncates; the probe needs a value it cuts");
        }

        [Test]
        public void A_null_string_card_is_refused_by_both_writers_alike() {
            foreach (var (file, options) in new[] { ("null.fits", FrameWriteOptions.PlainFits), ("null.xisf", new FrameWriteOptions(FrameFileFormat.Xisf, false)) }) {
                using var writer = FrameFileWriter.Create(Path.Combine(_root, file), 2, 2, options);
                Assert.Throws<ArgumentNullException>(() => writer.SetHeader("OBJECT", (string)null!, "target name"), file);
            }
        }

        private static readonly string[] WeatherCardsAfterHumidity = ["DEWPOINT", "PRESSURE", "WINDSPD", "WINDGUST", "WINDDIR", "CLOUDCVR"];

        [Test]
        public void A_nan_weather_reading_costs_only_its_own_card_in_either_container() {
            ushort[] px = Pattern(8, 8);
            var conditions = new ObservingConditionsDto("w", "Weather", EquipmentConnectionState.Connected,
                TemperatureC: 8.5, HumidityPct: double.NaN, DewPointC: 3.4, PressureHpa: 1013.2, CloudCoverPct: 5,
                WindSpeedMs: 2.5, WindGustMs: 4, WindDirectionDeg: 270, RainRate: 0, Safe: true, CapturedAt: "");
            string Write(FrameWriteOptions options) => _camera.WriteFrame(Guid.NewGuid(), px, 8, 8,
                new ExposureRequestDto(ExposureSec: 60, Gain: 100), "LIGHT", Captured, targetName: "M 31", conditions: conditions, options: options);
            var fits = FrameFileReader.ReadHeaders(Write(FrameWriteOptions.PlainFits)).Where(kv => !Structural.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var xisf = FrameFileReader.ReadHeaders(Write(new FrameWriteOptions(FrameFileFormat.Xisf, true))).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(fits.ContainsKey("HUMIDITY"), Is.False);
            // The cards after it in the weather group still land.
            Assert.That(fits.Keys, Is.SupersetOf(WeatherCardsAfterHumidity));
            Assert.That(xisf, Is.EquivalentTo(fits));
        }

        // ---- #1456 review notes ------------------------------------------------------------

        [Test]
        public void A_nan_rotator_reading_costs_only_its_own_card_in_either_container() {
            // ROTATOR is written before the optional-header pass; a driver that reports NaN must
            // not cost the frame there either.
            string Write(FrameWriteOptions options) => _camera.WriteFrame(Guid.NewGuid(), Pattern(8, 8), 8, 8,
                new ExposureRequestDto(ExposureSec: 60, Gain: 100), "LIGHT", Captured, focuserPosition: 100,
                rotatorMechanicalDeg: double.NaN, options: options);
            foreach (var options in new[] { FrameWriteOptions.PlainFits, new FrameWriteOptions(FrameFileFormat.Xisf, true) }) {
                var headers = FrameFileReader.ReadHeaders(Write(options));
                Assert.That(headers.ContainsKey("ROTATOR"), Is.False, options.Extension);
                Assert.That(headers["FOCUSPOS"], Is.EqualTo("100"), options.Extension);
                Assert.That(headers.ContainsKey("SWCREATE"), Is.True, $"{options.Extension}: the optional pass still runs");
            }
        }

        [Test]
        public void A_format_switch_does_not_reuse_the_other_containers_name() {
            // The default template has no per-frame token, so the second frame's name collides
            // with the first's. Thumbnails and previews are keyed on the stem, so Light.fits and
            // Light.xisf side by side would share them.
            UseStorage("fits", "zstd");
            var fits = Capture(Pattern(8, 8), 8, 8);
            UseStorage("xisf", "zstd");
            var xisf = Capture(Pattern(8, 8), 8, 8);
            UseStorage("fits", "zstd");
            var fitsAgain = Capture(Pattern(8, 8), 8, 8);
            var stem = Path.Combine(Path.GetDirectoryName(fits)!, Path.GetFileNameWithoutExtension(fits));
            Assert.Multiple(() => {
                Assert.That(xisf, Is.EqualTo(stem + " (2).xisf"));
                Assert.That(fitsAgain, Is.EqualTo(stem + " (3).fits"));
            });
        }

        [Test]
        public void An_int32_xisf_property_never_carries_a_fraction() {
            var path = Path.Combine(_root, "bin.xisf");
            using (var writer = FrameFileWriter.Create(path, 2, 2, new FrameWriteOptions(FrameFileFormat.Xisf, false))) {
                writer.WriteImageData(new ushort[4]);
                writer.SetHeader("XBINNING", 2.0, "binning X");
                writer.SetHeader("YBINNING", 2.5, "binning Y");
                writer.Complete();
            }
            var image = XISF.ReadHeader(path).Image!;
            var ns = image.Name.Namespace;
            string? Property(string id) => image.Elements(ns + "Property").FirstOrDefault(p => p.Attribute("id")?.Value == id)?.Attribute("value")?.Value;
            Assert.Multiple(() => {
                Assert.That(Property("Instrument:Camera:XBinning"), Is.EqualTo("2"), "a whole double lands as the Int32 it is");
                Assert.That(Property("Instrument:Camera:YBinning"), Is.Null, "a fraction is no Int32; the property is left out");
                Assert.That(FrameFileReader.ReadHeaders(path)["YBINNING"], Is.EqualTo("2.5"), "the keyword still carries what was written");
            });
        }

        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<CameraService> {
            public List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
        }

        [Test]
        public void The_store_timing_line_names_the_write_not_a_container() {
            var log = new RecordingLogger();
            using var camera = new CameraService(logger: log);
            camera.LogCaptureStoreTiming(Guid.Empty, 720, 15);
            Assert.That(log.Messages.Single(), Does.Contain("store timing: write 720ms, register 15ms"));
            Assert.That(log.Messages.Single(), Does.Not.Contain("fits"));
        }

        // ---- legacy profile values ---------------------------------------------------------

        [TestCase("fits", "fits")]
        [TestCase("xisf", "xisf")]
        [TestCase("XISF", "xisf")]
        [TestCase("fits_rice", "fits")]
        [TestCase("fits_gzip", "fits")]
        [TestCase(null, "fits")]
        [TestCase("tiff", "fits")]
        public void A_file_format_token_normalizes(string? stored, string expected) {
            Assert.That(StorageFormatTokens.NormalizeFileFormat(stored), Is.EqualTo(expected));
        }

        [TestCase("off", "off")]
        [TestCase("OFF", "off")]
        [TestCase("zstd", "zstd")]
        [TestCase("rice", "zstd")]
        [TestCase("gzip", "zstd")]
        [TestCase(null, "zstd")]
        public void A_compression_token_normalizes(string? stored, string expected) {
            Assert.That(StorageFormatTokens.NormalizeCompression(stored), Is.EqualTo(expected));
        }

        [Test]
        public void A_legacy_profile_snapshot_reads_as_plain_fits_with_compression_on() {
            var legacy = ProfileSnapshotNormalizer.Defaults with {
                Storage = ProfileSnapshotNormalizer.Defaults.Storage with { FileFormat = "fits_rice", Compression = "rice" },
            };
            var normalized = ProfileSnapshotNormalizer.Normalize(legacy, _ => false);
            Assert.That((normalized.Storage.FileFormat, normalized.Storage.Compression), Is.EqualTo(("fits", "zstd")));
            Assert.That((ProfileSnapshotNormalizer.Defaults.Storage.FileFormat, ProfileSnapshotNormalizer.Defaults.Storage.Compression),
                Is.EqualTo(("fits", "zstd")), "the defaults carry only current tokens");
        }

        [Test]
        public void A_legacy_profile_json_on_disk_loads_with_current_tokens() {
            var dir = Path.Combine(Path.GetTempPath(), $"oara-1148s-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try {
                var json = System.Text.Json.JsonSerializer.Serialize(ProfileSnapshotNormalizer.Defaults with {
                    Storage = ProfileSnapshotNormalizer.Defaults.Storage with { FileFormat = "fits_gzip", Compression = "gzip" },
                }, OpenAstroAra.Server.AraJsonSerializerContext.Default.ProfileSnapshotDto);
                File.WriteAllText(Path.Combine(dir, "profile.json"), json);
                var store = new FileProfileStore(dir);
                var storage = store.GetStorageSettings();
                Assert.That((storage.FileFormat, storage.Compression), Is.EqualTo(("fits", "zstd")));
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [Test]
        public void A_legacy_put_is_stored_and_echoed_with_current_tokens() {
            var store = new InMemoryProfileStore();
            var body = store.GetStorageSettings() with { FileFormat = "fits_rice", Compression = "gzip" };
            var result = ProfileEndpoints.PutStorageSettings(body, store);
            var echoed = (Microsoft.AspNetCore.Http.HttpResults.Ok<StorageSettingsDto>)result;
            Assert.Multiple(() => {
                Assert.That((echoed.Value!.FileFormat, echoed.Value.Compression), Is.EqualTo(("fits", "zstd")));
                Assert.That((store.GetStorageSettings().FileFormat, store.GetStorageSettings().Compression), Is.EqualTo(("fits", "zstd")));
            });
        }

        [TestCase("fits_rice", "rice", false, false)]
        [TestCase("fits_gzip", "gzip", false, false)]
        [TestCase("xisf", "rice", true, true)]
        [TestCase("xisf", "gzip", true, true)]
        [TestCase("xisf", "off", true, false)]
        public void Legacy_tokens_resolve_to_the_write_they_meant(string format, string compression, bool expectXisf, bool expectCompress) {
            var storage = new StorageSettingsDto("/x", format, compression, "t");
            var options = FrameWriteOptions.For(storage, compressDarksAndBias: false, "LIGHT");
            Assert.That((options.Format == FrameFileFormat.Xisf, options.Compress), Is.EqualTo((expectXisf, expectCompress)));
        }

        [Test]
        public void A_stored_zlib_xisf_choice_reads_as_zstandard() {
            var settings = new OpenAstroAra.Profile.ImageFileSettings {
                XISFCompressionType = OpenAstroAra.Core.Enums.XISFCompressionType.ZLIB,
            };
            Assert.That(settings.XISFCompressionType, Is.EqualTo(OpenAstroAra.Core.Enums.XISFCompressionType.ZSTD),
                "zlib is read-only; no profile path can hand it to the writer");
        }
    }
}
