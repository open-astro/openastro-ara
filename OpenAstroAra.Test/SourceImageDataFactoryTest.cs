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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Fits;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.FileFormat.RAW;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Stretch;
using OpenAstroAra.Test.TestData;
using SkiaSharp;
using System.Text;

namespace OpenAstroAra.Test;

/// <summary>Rank 1: bounded, signature-selected FITS, XISF, and camera-RAW source loading.</summary>
[TestFixture]
public sealed class SourceImageDataFactoryTest {
    private string _root = null!;
    private HeadlessProfileService _profileService = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), $"oara-source-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _profileService = new HeadlessProfileService();
    }

    [TearDown]
    public void TearDown() {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Fits_signature_is_authoritative_and_preserves_pixels_and_cfa() {
        var path = Path.Combine(_root, "renamed-source.bin");
        var expected = WriteFits(path, 7, 5, "RGGB");
        var source = await Factory().LoadAsync(path, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(source.Format, Is.EqualTo(SourceImageFormat.Fits));
            Assert.That(source.Width, Is.EqualTo(7));
            Assert.That(source.Height, Is.EqualTo(5));
            Assert.That(source.SourceBitDepth, Is.EqualTo(16));
            Assert.That(source.CfaPattern, Is.EqualTo("RGGB"));
            Assert.That(source.Data.FlatArray, Is.EqualTo(expected));
        });
    }

    [Test]
    public async Task Float32_fits_is_normalized_without_clipping_dynamic_range() {
        var path = Path.Combine(_root, "float32.fits");
        var pixels = Enumerable.Range(0, 12).Select(i => i / 11.0f).ToArray();
        using (var fits = FitsImage.Create(path, 4, 3, FitsBitDepth.Real32)) {
            fits.WriteImageData(pixels);
            fits.Complete();
        }

        var source = await Factory().LoadAsync(path, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(source.SourceBitDepth, Is.EqualTo(32));
            Assert.That(source.Data.FlatArray[0], Is.EqualTo(0));
            Assert.That(source.Data.FlatArray[^1], Is.EqualTo(ushort.MaxValue));
            Assert.That(source.Data.FlatArray, Is.Ordered.Ascending);
        });
    }

    [Test]
    public async Task Byte_fits_is_scaled_to_the_full_unsigned_16_bit_plane() {
        var path = Path.Combine(_root, "byte.fits");
        using (var fits = FitsImage.Create(path, 2, 1, FitsBitDepth.Byte)) {
            fits.WriteImageData(new ushort[] { 0, byte.MaxValue });
            fits.Complete();
        }

        var source = await Factory().LoadAsync(path, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(source.SourceBitDepth, Is.EqualTo(8));
            Assert.That(source.Data.FlatArray, Is.EqualTo(new ushort[] { 0, ushort.MaxValue }));
        });
    }

    [Test]
    public void Unsupported_fits_bit_depth_has_clear_error_before_native_decode() {
        var path = Path.Combine(_root, "unsupported-bitpix.fits");
        WriteFits(path, 4, 3);
        RewriteFitsIntegerCard(path, "BITPIX", 12);
        var ex = Assert.ThrowsAsync<UnsupportedSourceImageFormatException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("BITPIX 12"));
    }

    [Test]
    public void Fits_with_false_simple_card_is_rejected_before_native_decode() {
        var path = Path.Combine(_root, "not-simple.fits");
        WriteFits(path, 4, 3);
        RewriteFitsValueCard(path, "SIMPLE", "F");
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("SIMPLE = T"));
    }

    [Test]
    public void Truncated_fits_primary_header_is_rejected() {
        var path = Path.Combine(_root, "truncated.fits");
        WriteFits(path, 4, 3);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(100);
        }
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("truncated"));
    }

    [Test]
    public void Fits_dimension_limit_rejects_before_native_decode() {
        var path = Path.Combine(_root, "wide.fits");
        WriteFits(path, 10, 2);
        var factory = Factory(new ImageLoadLimits(MaxDimension: 5));
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("10x2"));
    }

    [Test]
    public async Task Xisf_signature_overrides_wrong_extension_and_materializes_without_null_dependencies() {
        var path = Path.Combine(_root, "renamed-source.fits");
        var expected = WriteXisf(path, 8, 6, "BGGR");
        var factory = Factory();
        var source = await factory.LoadAsync(path, CancellationToken.None);
        var image = factory.CreateImageData(source);

        Assert.Multiple(() => {
            Assert.That(source.Format, Is.EqualTo(SourceImageFormat.Xisf));
            Assert.That(source.Width, Is.EqualTo(8));
            Assert.That(source.Height, Is.EqualTo(6));
            Assert.That(source.CfaPattern, Is.EqualTo("BGGR"));
            Assert.That(source.Data.FlatArray, Is.EqualTo(expected));
            Assert.That(image.Properties.IsBayered, Is.True);
            Assert.That(image.RenderBitmapSource(), Has.Length.EqualTo(48));
        });
    }

    [TestCase(XISFCompressionType.LZ4)]
    [TestCase(XISFCompressionType.ZLIB)]
    public async Task Compressed_shuffled_checksummed_xisf_round_trips(XISFCompressionType compression) {
        var path = Path.Combine(_root, $"compressed-{compression}.xisf");
        var expected = WriteXisf(path, 64, 64, checksum: XISFChecksumType.SHA256,
            compression: compression, byteShuffling: true, compressible: true);
        var source = await Factory().LoadAsync(path, CancellationToken.None);
        Assert.That(source.Data.FlatArray, Is.EqualTo(expected));
    }

    [Test]
    public async Task UInt32_xisf_scales_the_complete_unsigned_range() {
        var path = Path.Combine(_root, "uint32.xisf");
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(3, 1, 32, false, 0, 0),
            "LIGHT", XISFSampleFormat.UInt32);
        var xisf = new OpenAstroAra.Image.FileFormat.XISF.XISF(header);
        xisf.AddAttachedImageInt(new[] { 0, int.MinValue, -1 }, new FileSaveInfo());
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            Assert.That(xisf.Save(stream), Is.True);
        }

        var source = await Factory().LoadAsync(path, CancellationToken.None);

        Assert.That(source.Data.FlatArray,
            Is.EqualTo(new ushort[] { 0, 32768, ushort.MaxValue }));
    }

    [Test]
    public void Known_extension_with_bad_signature_is_a_malformed_file() {
        var path = Path.Combine(_root, "bad.fits");
        File.WriteAllText(path, "not an image");
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("does not match"));
    }

    [Test]
    public void Unknown_signature_and_extension_has_clear_unsupported_error() {
        var path = Path.Combine(_root, "bad.dat");
        File.WriteAllText(path, "not an image");
        var ex = Assert.ThrowsAsync<UnsupportedSourceImageFormatException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("Supported formats: FITS, XISF"));
    }

    [Test]
    public void File_size_limit_rejects_before_decode() {
        var path = Path.Combine(_root, "large.fits");
        WriteFits(path, 4, 3);
        var size = new FileInfo(path).Length;
        var factory = Factory(new ImageLoadLimits(MaxFileBytes: size - 1));
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("size"));
    }

    [Test]
    public void Fits_pixel_limit_rejects_before_pixel_allocation() {
        var path = Path.Combine(_root, "bounded.fits");
        WriteFits(path, 10, 10);
        var factory = Factory(new ImageLoadLimits(MaxPixelCount: 50));
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("pixel count 100"));
    }

    [Test]
    public void Xisf_pixel_limit_rejects_before_data_block_allocation() {
        var path = Path.Combine(_root, "bounded.xisf");
        WriteXisf(path, 10, 10);
        var factory = Factory(new ImageLoadLimits(MaxPixelCount: 50));
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("pixel count 100"));
    }

    [Test]
    public void Pre_cancelled_load_does_no_decode() {
        var path = Path.Combine(_root, "cancelled.fits");
        WriteFits(path, 4, 3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => Factory().LoadAsync(path, cts.Token));
    }

    [Test]
    public void Truncated_xisf_data_block_is_rejected() {
        var path = Path.Combine(_root, "truncated.xisf");
        WriteXisf(path, 10, 10);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(stream.Length - 2);
        }
        Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public void Oversized_xisf_header_is_rejected_before_allocation() {
        var path = Path.Combine(_root, "header.xisf");
        File.WriteAllBytes(path, [.. "XISF0100"u8.ToArray(), 0xff, 0xff, 0xff, 0x7f, 0, 0, 0, 0]);
        var factory = Factory(new ImageLoadLimits(MaxHeaderBytes: 1024));
        Assert.ThrowsAsync<InvalidDataException>(() =>
            factory.LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public void Malformed_xisf_xml_header_has_a_clear_data_error() {
        var path = Path.Combine(_root, "malformed-xml.xisf");
        WriteXisf(path, 6, 4);
        ReplaceAscii(path, "<xisf", "<1isf");
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("XML header is malformed"));
    }

    [Test]
    public void Unsupported_xisf_channel_layout_has_clear_error() {
        var path = Path.Combine(_root, "planar.xisf");
        WriteXisf(path, 6, 4);
        ReplaceAscii(path, "6:4:1", "6:4:3");
        var ex = Assert.ThrowsAsync<NotSupportedException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("3 channels"));
    }

    [Test]
    public void Unsupported_xisf_sample_format_has_clear_error() {
        var path = Path.Combine(_root, "sample.xisf");
        WriteXisf(path, 6, 4);
        ReplaceAscii(path, "UInt16", "BadFmt");
        Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
    }

    [Test]
    public void Unknown_fits_cfa_pattern_is_not_silently_rendered_as_monochrome() {
        var path = Path.Combine(_root, "unknown-cfa.fits");
        WriteFits(path, 6, 4, "XXXX");
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("CFA pattern 'XXXX'"));
    }

    [Test]
    public void Unknown_xisf_cfa_pattern_is_not_silently_rendered_as_monochrome() {
        var path = Path.Combine(_root, "unknown-cfa.xisf");
        WriteXisf(path, 6, 4, "RGGB");
        ReplaceAllAscii(path, "RGGB", "XXXX");
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("CFA pattern 'XXXX'"));
    }

    [Test]
    public void Xisf_checksum_mismatch_is_rejected() {
        var path = Path.Combine(_root, "checksum.xisf");
        WriteXisf(path, 8, 6, checksum: XISFChecksumType.SHA256);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xff));
        }
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("checksum"));
    }

    [Test]
    public void Invalid_limits_fail_at_factory_construction() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Factory(new ImageLoadLimits(MaxPixelCount: 0)));
    }

    [Test]
    public async Task Camera_raw_signature_is_authoritative_and_preview_keeps_libraw_color() {
        var path = Path.Combine(_root, "renamed-camera-source.bin");
        await File.WriteAllBytesAsync(path, SyntheticDng.Create());
        var factory = Factory();

        var source = await factory.LoadAsync(path, CancellationToken.None);
        var inherited = await factory.CreateFromFile(path, bitDepth: 14, isBayered: true,
            RawConverter.DCRAW, CancellationToken.None);
        using var service = new PreviewImageService(Path.Combine(_root, "raw-preview-cache"), factory);
        var rgb = await service.RenderAsync(RawPreviewRequest(path, PreviewChannelMode.Rgb),
            CancellationToken.None);
        var red = await service.RenderAsync(RawPreviewRequest(path, PreviewChannelMode.Red),
            CancellationToken.None);
        var cropRequest = RawPreviewRequest(path, PreviewChannelMode.Rgb) with {
            CropX = 7,
            CropY = 5,
            CropWidth = 20,
            CropHeight = 16,
            AnnotateStars = true,
        };
        var crop = await service.RenderAsync(cropRequest, CancellationToken.None);
        using var rgbBitmap = SKBitmap.Decode(rgb.Bytes);
        using var redBitmap = SKBitmap.Decode(red.Bytes);
        using var cropBitmap = SKBitmap.Decode(crop.Bytes);

        Assert.Multiple(() => {
            Assert.That(source.Format, Is.EqualTo(SourceImageFormat.Raw));
            Assert.That(source.Width, Is.EqualTo(64));
            Assert.That(source.Height, Is.EqualTo(48));
            Assert.That(source.CfaPattern, Is.Null);
            Assert.That(source.ColorData, Is.Not.Null);
            Assert.That(source.ColorData!.DebayerMethod, Is.EqualTo("libraw_ahd"));
            Assert.That(source.ColorData.OriginalCfaPattern, Is.EqualTo("RGGB"));
            Assert.That(source.Data.FlatArray, Has.Length.EqualTo(64 * 48));
            Assert.That(source.MetaData.Camera.SensorType, Is.EqualTo(SensorType.Color));
            Assert.That(source.MetaData.Camera.Name, Is.EqualTo("OpenAstro Synthetic RGGB"));
            Assert.That(inherited.Properties.BitDepth, Is.EqualTo(16));
            Assert.That(inherited.Properties.IsBayered, Is.False);
            Assert.That(rgb.Metadata.DebayerMode, Is.EqualTo("libraw_ahd"));
            Assert.That(rgb.Metadata.ChannelMode, Is.EqualTo("rgb"));
            Assert.That(red.Metadata.ChannelMode, Is.EqualTo("red"));
            Assert.That(rgbBitmap, Is.Not.Null);
            Assert.That(rgbBitmap!.Width, Is.EqualTo(64));
            Assert.That(rgbBitmap.Height, Is.EqualTo(48));
            Assert.That(redBitmap, Is.Not.Null);
            Assert.That(redBitmap!.Width, Is.EqualTo(64));
            Assert.That(red.Bytes, Is.Not.EqualTo(rgb.Bytes));
            Assert.That(cropBitmap, Is.Not.Null);
            Assert.That(cropBitmap!.Width, Is.EqualTo(20));
            Assert.That(cropBitmap.Height, Is.EqualTo(16));
            Assert.That(crop.Metadata.DebayerMode, Is.EqualTo("libraw_ahd"));
            Assert.That(crop.Metadata.Annotated, Is.True);
        });
    }

    [Test]
    public void Known_raw_extension_with_bad_data_has_safe_decode_error() {
        var path = Path.Combine(_root, "broken.dng");
        File.WriteAllText(path, "not camera raw");
        var ex = Assert.ThrowsAsync<RawImageDecodeException>(() =>
            Factory().LoadAsync(path, CancellationToken.None));
        Assert.Multiple(() => {
            Assert.That(ex!.Message, Does.StartWith("LibRaw failed to identify camera RAW data:"));
            Assert.That(ex.Message, Does.Not.Contain(path));
        });
    }

    [Test]
    public async Task Repository_thumbnail_preserves_libraw_color() {
        var path = Path.Combine(_root, "thumbnail-source.dng");
        await File.WriteAllBytesAsync(path, SyntheticDng.Create());
        var profile = new InMemoryProfileStore();
        var db = new SqliteAraDatabase(_root, logger: null);
        await db.InitializeAsync(CancellationToken.None);
        var sessionId = Guid.NewGuid();
        await using (var conn = db.OpenConnection()) {
            await using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $started);";
            insert.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            insert.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var repo = new SqliteFrameRepository(db, profile, sourceImages: Factory());
        var frameId = Guid.NewGuid();
        await repo.InsertAsync(Frame(frameId, sessionId, path), CancellationToken.None);

        var thumbnail = await repo.GetThumbnailAsync(frameId, CancellationToken.None);
        using var bitmap = SKBitmap.Decode(thumbnail!.Value.Bytes);
        Assert.That(bitmap, Is.Not.Null);
        var center = bitmap!.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        Assert.Multiple(() => {
            Assert.That(thumbnail.Value.ContentType, Is.EqualTo("image/jpeg"));
            Assert.That(bitmap.Width, Is.EqualTo(64));
            Assert.That(bitmap.Height, Is.EqualTo(48));
            Assert.That(center.Red, Is.GreaterThan(center.Green));
            Assert.That(center.Red, Is.GreaterThan(center.Blue));
        });
    }

    [Test]
    public async Task Repository_preview_thumbnail_and_plate_solve_load_support_xisf() {
        var path = Path.Combine(_root, "catalog-source.xisf");
        WriteXisf(path, 8, 6, "GRBG");
        var profile = new InMemoryProfileStore();
        var db = new SqliteAraDatabase(_root, logger: null);
        await db.InitializeAsync(CancellationToken.None);
        var sessionId = Guid.NewGuid();
        await using (var conn = db.OpenConnection()) {
            await using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $started);";
            insert.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            insert.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var factory = Factory();
        var repo = new SqliteFrameRepository(db, profile, sourceImages: factory);
        var frameId = Guid.NewGuid();
        await repo.InsertAsync(Frame(frameId, sessionId, path), CancellationToken.None);

        var preview = await repo.GetPreviewAsync(frameId,
            new FramePreviewRequestDto("auto_stf", null, null, null, 512, true), CancellationToken.None);
        var cachedPreview = await repo.GetPreviewAsync(frameId,
            new FramePreviewRequestDto("auto_stf", null, null, null, 512, true), CancellationToken.None);
        var rawPreview = await repo.GetPreviewAsync(frameId,
            new FramePreviewRequestDto("auto_stf", null, null, null, 512, false), CancellationToken.None);
        var thumbnail = await repo.GetThumbnailAsync(frameId, CancellationToken.None);
        var image = await repo.LoadImageDataAsync(frameId, _profileService, CancellationToken.None);
        using var decodedPreview = SKBitmap.Decode(preview!.Value.Bytes);
        using var decodedRawPreview = SKBitmap.Decode(rawPreview!.Value.Bytes);
        using var decodedThumbnail = SKBitmap.Decode(thumbnail!.Value.Bytes);

        Assert.Multiple(() => {
            Assert.That(decodedPreview, Is.Not.Null);
            Assert.That(decodedPreview!.Width, Is.EqualTo(4)); // super-pixel debayer
            Assert.That(decodedPreview.Height, Is.EqualTo(3));
            Assert.That(preview.Value.Metadata.DebayerMode, Is.EqualTo("super_pixel"));
            Assert.That(cachedPreview!.Value.CacheHit, Is.True);
            Assert.That(decodedRawPreview!.Width, Is.EqualTo(8));
            Assert.That(decodedRawPreview.Height, Is.EqualTo(6));
            Assert.That(rawPreview.Value.Metadata.DebayerMode, Is.EqualTo("none"));
            Assert.That(decodedThumbnail, Is.Not.Null);
            Assert.That(image, Is.Not.Null);
            Assert.That(image!.Properties.Width, Is.EqualTo(8));
            Assert.That(image.Properties.Height, Is.EqualTo(6));
            Assert.That(image.Properties.IsBayered, Is.True);
            Assert.That(Directory.EnumerateFiles(_root, "catalog-source.preview*.jpg"), Is.Empty);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "preview-cache"), "*.jpg",
                SearchOption.AllDirectories), Is.Not.Empty);
        });

        var invalid = new FramePreviewRequestDto("bogus", null, null, null, 512, true);
        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            repo.GetPreviewAsync(frameId, invalid, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("Unknown stretch palette"));

        var invalidColor = new FramePreviewRequestDto("auto_stf", null, null, null, 512, true,
            AnnotateStars: true, AnnotationColor: "chartreuse");
        var colorError = Assert.ThrowsAsync<ArgumentException>(() =>
            repo.GetPreviewAsync(frameId, invalidColor, CancellationToken.None));
        Assert.That(colorError!.Message, Does.Contain("Unknown annotation color"));
    }

    private SourceImageDataFactory Factory(ImageLoadLimits? limits = null) =>
        new(_profileService, limits);

    private static PreviewRenderRequest RawPreviewRequest(string path, PreviewChannelMode channel) =>
        new(Guid.NewGuid(), path, null, StretchAlgorithm.AutoStf, new StretchParams(),
            MaxDimension: 512, ApplyDebayer: false, channel, Invert: false, Saturation: 1,
            CropX: null, CropY: null, CropWidth: null, CropHeight: null);

    private static ushort[] WriteFits(string path, int width, int height, string? cfaPattern = null) {
        var pixels = Enumerable.Range(0, width * height).Select(i => (ushort)(i * 101)).ToArray();
        using var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort);
        fits.WriteImageData(pixels);
        if (cfaPattern is not null) fits.SetHeader("BAYERPAT", cfaPattern);
        fits.Complete();
        return pixels;
    }

    private static ushort[] WriteXisf(string path, int width, int height,
            string? cfaPattern = null, XISFChecksumType checksum = XISFChecksumType.NONE,
            XISFCompressionType compression = XISFCompressionType.NONE,
            bool byteShuffling = false, bool compressible = false) {
        var pixels = compressible
            ? Enumerable.Repeat((ushort)1234, width * height).ToArray()
            : Enumerable.Range(0, width * height).Select(i => (ushort)(i * 101)).ToArray();
        var header = new XISFHeader();
        header.AddImageMetaData(new ImageProperties(width, height, 16,
            isBayered: cfaPattern is not null, gain: 0, offset: 0), "LIGHT");
        if (cfaPattern is not null) {
            header.AddImageFITSKeyword("BAYERPAT", cfaPattern);
            header.AddCfaAttribute(cfaPattern, 2, 2);
        }
        var xisf = new OpenAstroAra.Image.FileFormat.XISF.XISF(header);
        xisf.AddAttachedImage(pixels, new FileSaveInfo {
            XISFChecksumType = checksum,
            XISFCompressionType = compression,
            XISFByteShuffling = byteShuffling,
        });
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Assert.That(xisf.Save(stream), Is.True);
        return pixels;
    }

    private static void ReplaceAscii(string path, string original, string replacement) {
        Assert.That(replacement, Has.Length.EqualTo(original.Length));
        var bytes = File.ReadAllBytes(path);
        var oldBytes = Encoding.ASCII.GetBytes(original);
        var newBytes = Encoding.ASCII.GetBytes(replacement);
        var index = bytes.AsSpan().IndexOf(oldBytes);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        newBytes.CopyTo(bytes, index);
        File.WriteAllBytes(path, bytes);
    }

    private static void ReplaceAllAscii(string path, string original, string replacement) {
        Assert.That(replacement, Has.Length.EqualTo(original.Length));
        var bytes = File.ReadAllBytes(path);
        var oldBytes = Encoding.ASCII.GetBytes(original);
        var newBytes = Encoding.ASCII.GetBytes(replacement);
        var replacements = 0;
        var offset = 0;
        while (offset <= bytes.Length - oldBytes.Length) {
            var relative = bytes.AsSpan(offset).IndexOf(oldBytes);
            if (relative < 0) break;
            var index = offset + relative;
            newBytes.CopyTo(bytes, index);
            replacements++;
            offset = index + newBytes.Length;
        }
        Assert.That(replacements, Is.GreaterThan(0));
        File.WriteAllBytes(path, bytes);
    }

    private static void RewriteFitsIntegerCard(string path, string key, int value) {
        RewriteFitsValueCard(path, key,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void RewriteFitsValueCard(string path, string key, string value) {
        var bytes = File.ReadAllBytes(path);
        var cardPrefix = Encoding.ASCII.GetBytes(key.PadRight(8) + "= ");
        var index = bytes.AsSpan().IndexOf(cardPrefix);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        var field = Encoding.ASCII.GetBytes(value.PadLeft(20));
        field.CopyTo(bytes, index + 10);
        File.WriteAllBytes(path, bytes);
    }

    private static FrameDto Frame(Guid id, Guid sessionId, string path) => new(
        Id: id,
        SessionId: sessionId,
        TargetName: "M31",
        FrameType: FrameType.Light,
        FilterName: "L",
        ExposureSeconds: 60,
        Gain: 100,
        Offset: 10,
        TemperatureC: -10,
        CapturedUtc: DateTimeOffset.UtcNow,
        FilePath: path,
        FileSizeBytes: new FileInfo(path).Length,
        Width: 8,
        Height: 6,
        BitDepth: 16,
        Hfr: null,
        StarCount: null,
        Eccentricity: null,
        GuidingRmsArcsec: null,
        SnrEstimate: null,
        QualityScore: null,
        Rating: 0,
        Tags: []);
}