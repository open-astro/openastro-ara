#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using BitMiracle.LibTiff.Classic;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.FileFormat.Raster;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Stretch;
using SkiaSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class RasterImageDecoderTest {
    private string _root = null!;
    private SourceImageDataFactory _factory = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), $"oara-raster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new SourceImageDataFactory(new HeadlessProfileService());
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Png_and_jpeg_decode_from_file_and_buffer_as_preview_only_color() {
        var png = EncodeSkiaRaster(SKEncodedImageFormat.Png, 13, 9);
        var jpeg = EncodeSkiaRaster(SKEncodedImageFormat.Jpeg, 13, 9);
        var pngPath = Path.Combine(_root, "renamed-png.bin");
        var jpegPath = Path.Combine(_root, "renamed-jpeg.data");
        await File.WriteAllBytesAsync(pngPath, png);
        await File.WriteAllBytesAsync(jpegPath, jpeg);
        var decoder = new RasterImageDecoder();

        var pngFile = await decoder.DecodeFileAsync(pngPath, RasterImageFormat.Png,
            ImageLoadLimits.Default, CancellationToken.None);
        var pngBuffer = await decoder.DecodeBufferAsync(png, expectedFormat: null,
            ImageLoadLimits.Default, CancellationToken.None);
        var jpegFile = await decoder.DecodeFileAsync(jpegPath, RasterImageFormat.Jpeg,
            ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(pngFile.Format, Is.EqualTo(RasterImageFormat.Png));
            Assert.That(pngFile.IsPreviewOnly, Is.True);
            Assert.That(pngFile.SourceBitDepth, Is.EqualTo(8));
            Assert.That(pngFile.Width, Is.EqualTo(13));
            Assert.That(pngFile.Height, Is.EqualTo(9));
            Assert.That(pngFile.ColorData, Is.Not.Null);
            Assert.That(pngFile.ColorData!.ProcessingMethod, Is.EqualTo("skia_srgb"));
            Assert.That(pngBuffer.BorrowLuminancePlane(), Is.EqualTo(pngFile.BorrowLuminancePlane()));
            Assert.That(jpegFile.Format, Is.EqualTo(RasterImageFormat.Jpeg));
            Assert.That(jpegFile.SourceBitDepth, Is.EqualTo(8));
            Assert.That(jpegFile.ColorData, Is.Not.Null);
            Assert.That(jpegFile.BorrowLuminancePlane(), Has.Length.EqualTo(13 * 9));
        });
    }

    [Test]
    public async Task Sixteen_bit_png_preserves_grayscale_samples_exactly() {
        ushort[] expected = [0, 1, 255, 256, 4095, 32768, 65534, 65535];
        var bytes = EncodePng16(expected, width: 4, height: 2);
        var decoded = await new RasterImageDecoder().DecodeBufferAsync(bytes,
            RasterImageFormat.Png, ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(decoded.SourceBitDepth, Is.EqualTo(16));
            Assert.That(decoded.ColorData, Is.Null);
            Assert.That(decoded.BorrowLuminancePlane(), Is.EqualTo(expected));
        });
    }

    [Test]
    public async Task Sixteen_bit_png_preserves_all_filters_and_adam7_samples_exactly() {
        const int width = 13;
        const int height = 11;
        var expected = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)((value * 7919 + 257) & 0xffff)).ToArray();
        var bytes = EncodePng16(expected, width, height, interlaced: true,
            exerciseFilters: true);

        var decoded = await new RasterImageDecoder().DecodeBufferAsync(bytes,
            RasterImageFormat.Png, ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(decoded.BorrowLuminancePlane(), Is.EqualTo(expected));
            Assert.That(decoded.Metadata["PNGINTERLACE"], Is.EqualTo("Adam7"));
        });
    }

    [Test]
    public async Task Sixteen_bit_rgba_png_preserves_color_and_ignores_alpha() {
        const int width = 7;
        const int height = 5;
        var red = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)(value * 1201)).ToArray();
        var green = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)(ushort.MaxValue - value * 997)).ToArray();
        var blue = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)(value * 313)).ToArray();
        var alpha = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)(value * 1873)).ToArray();
        var bytes = EncodePng16Planes([red, green, blue, alpha], width, height,
            colorType: 6, interlaced: false, exerciseFilters: true);

        var decoded = await new RasterImageDecoder().DecodeBufferAsync(bytes,
            RasterImageFormat.Png, ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(decoded.ColorData, Is.Not.Null);
            Assert.That(decoded.ColorData!.BorrowRedPlane(), Is.EqualTo(red));
            Assert.That(decoded.ColorData.BorrowGreenPlane(), Is.EqualTo(green));
            Assert.That(decoded.ColorData.BorrowBluePlane(), Is.EqualTo(blue));
            Assert.That(decoded.ColorData.ProcessingMethod, Is.EqualTo("png_encoded_samples"));
            Assert.That(decoded.BorrowLuminancePlane(), Has.Length.EqualTo(width * height));
        });
    }

    [TestCase(TIFFCompressionType.NONE)]
    [TestCase(TIFFCompressionType.LZW)]
    [TestCase(TIFFCompressionType.ZIP)]
    public async Task Tiff_writer_round_trips_linear_16_bit_pixels_and_compression(
            TIFFCompressionType compression) {
        const int width = 37;
        const int height = 19;
        var expected = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)((value * 997) & 0xffff)).ToArray();
        var path = Path.Combine(_root, $"roundtrip-{compression}.tif");
        TiffImageWriter.WriteGrayscale16(path, expected, width, height, compression,
            "SIMPLE  =                    T\nEND\n", CancellationToken.None);

        var decoded = await new RasterImageDecoder().DecodeFileAsync(path,
            RasterImageFormat.Tiff, ImageLoadLimits.Default, CancellationToken.None);
        using var image = Tiff.Open(path, "r");
        var storedCompression = (Compression)image.GetFieldDefaulted(TiffTag.COMPRESSION)[0].ToInt();

        Assert.Multiple(() => {
            Assert.That(decoded.SourceBitDepth, Is.EqualTo(16));
            Assert.That(decoded.ColorData, Is.Null);
            Assert.That(decoded.BorrowLuminancePlane(), Is.EqualTo(expected));
            Assert.That(storedCompression, Is.EqualTo(compression switch {
                TIFFCompressionType.NONE => Compression.NONE,
                TIFFCompressionType.LZW => Compression.LZW,
                TIFFCompressionType.ZIP => Compression.ADOBE_DEFLATE,
                _ => throw new ArgumentOutOfRangeException(nameof(compression)),
            }));
        });
    }

    [Test]
    public async Task Tiled_rgb_tiff_honors_right_top_orientation_and_color_planes() {
        var path = Path.Combine(_root, "tiled-rgb.tif");
        WriteTiledRgbTiff(path, width: 18, height: 17, Orientation.RIGHTTOP);
        var decoded = await new RasterImageDecoder().DecodeFileAsync(path,
            RasterImageFormat.Tiff, ImageLoadLimits.Default, CancellationToken.None);
        var color = decoded.ColorData!;

        Assert.Multiple(() => {
            Assert.That(decoded.Width, Is.EqualTo(17));
            Assert.That(decoded.Height, Is.EqualTo(18));
            Assert.That(color, Is.Not.Null);
            Assert.That(color.SourceBitDepth, Is.EqualTo(8));
            Assert.That(color.ProcessingMethod, Is.EqualTo("source_rgb_linear"));
            Assert.That(color.BorrowRedPlane()[16], Is.EqualTo(0));
            Assert.That(color.BorrowGreenPlane()[16], Is.EqualTo(0));
            Assert.That(color.BorrowRedPlane()[17 * 17], Is.EqualTo(17 * 257));
            Assert.That(color.BorrowGreenPlane()[17 * 17], Is.EqualTo(16 * 257));
        });
    }

    [Test]
    public async Task Separate_planar_lzw_tiff_decodes_all_rgb_planes() {
        var path = Path.Combine(_root, "separate.tif");
        WriteSeparateRgbTiff(path, width: 11, height: 7);
        var decoded = await new RasterImageDecoder().DecodeFileAsync(path,
            RasterImageFormat.Tiff, ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(decoded.ColorData, Is.Not.Null);
            Assert.That(decoded.ColorData!.BorrowRedPlane()[10], Is.EqualTo(10 * 257));
            Assert.That(decoded.ColorData.BorrowGreenPlane()[6 * 11], Is.EqualTo(6 * 257));
            Assert.That(decoded.ColorData.BorrowBluePlane(), Has.All.EqualTo(77 * 257));
        });
    }

    [Test]
    public async Task Float32_tiff_normalizes_global_finite_range_and_handles_nonfinite_samples() {
        var path = Path.Combine(_root, "float32.tif");
        WriteFloatTiff(path, [-1f, 0f, 2f, float.NaN]);
        var decoded = await new RasterImageDecoder().DecodeFileAsync(path,
            RasterImageFormat.Tiff, ImageLoadLimits.Default, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(decoded.SourceBitDepth, Is.EqualTo(32));
            Assert.That(decoded.BorrowLuminancePlane(), Is.EqualTo(new ushort[] {
                0, 21845, 65535, 0,
            }));
        });
    }

    [Test]
    public void Malformed_truncated_wrong_format_and_unsupported_depth_have_clear_failures() {
        var malformed = "not an image"u8.ToArray();
        var truncatedPng = EncodeSkiaRaster(SKEncodedImageFormat.Png, 8, 8)[..20];
        var unsupportedTiff = Path.Combine(_root, "unsupported-depth.tif");
        WriteHeaderOnlyTiff(unsupportedTiff, 8, 8, bits: 12);

        var malformedError = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(malformed, expectedFormat: null,
                ImageLoadLimits.Default, CancellationToken.None));
        var truncatedError = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(truncatedPng, RasterImageFormat.Png,
                ImageLoadLimits.Default, CancellationToken.None));
        var mismatch = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(EncodeSkiaRaster(
                    SKEncodedImageFormat.Jpeg, 8, 8), RasterImageFormat.Png,
                ImageLoadLimits.Default, CancellationToken.None));
        var depth = Assert.ThrowsAsync<NotSupportedException>(() =>
            new RasterImageDecoder().DecodeFileAsync(unsupportedTiff, RasterImageFormat.Tiff,
                ImageLoadLimits.Default, CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(malformedError!.Message, Does.Contain("signature"));
            Assert.That(truncatedError!.Message, Does.Contain("truncated"));
            Assert.That(mismatch!.Message, Does.Contain("expected Png"));
            Assert.That(depth!.Message, Does.Contain("12 bits"));
        });
    }

    [Test]
    public void Sixteen_bit_png_rejects_bad_crc_and_trailing_data() {
        var valid = EncodePng16([1, 2, 3, 4], width: 2, height: 2);
        var badCrc = valid.ToArray();
        badCrc[^1] ^= 0x80;
        var trailing = valid.Concat(new byte[] { 1 }).ToArray();
        var animated = InsertPngChunkAfterHeader(valid, "acTL", new byte[8]);

        var crcError = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(badCrc, RasterImageFormat.Png,
                ImageLoadLimits.Default, CancellationToken.None));
        var trailingError = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(trailing, RasterImageFormat.Png,
                ImageLoadLimits.Default, CancellationToken.None));
        var animationError = Assert.ThrowsAsync<NotSupportedException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(animated, RasterImageFormat.Png,
                ImageLoadLimits.Default, CancellationToken.None));

        Assert.Multiple(() => {
            Assert.That(crcError!.Message, Does.Contain("CRC"));
            Assert.That(trailingError!.Message, Does.Contain("trailing data"));
            Assert.That(animationError!.Message, Does.Contain("Animated PNG"));
        });
    }

    [Test]
    public void Geometry_file_decoded_memory_header_and_cancellation_limits_reject_before_decode() {
        var hugeTiff = Path.Combine(_root, "huge.tif");
        WriteHeaderOnlyTiff(hugeTiff, 5000, 4000, bits: 16);
        var png = EncodeSkiaRaster(SKEncodedImageFormat.Png, 20, 20);
        var hugePng16 = EncodePng16(new ushort[20 * 20], 20, 20);
        BinaryPrimitives.WriteUInt32BigEndian(hugePng16.AsSpan(16, 4), 5000);
        var jpegWithLargeHeader = new byte[] {
            0xff, 0xd8, 0xff, 0xe1, 0x00, 0x20, 1, 2, 3, 4, 5, 6, 7, 8,
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var geometry = Assert.ThrowsAsync<InvalidDataException>(() =>
            new RasterImageDecoder().DecodeFileAsync(hugeTiff, RasterImageFormat.Tiff,
                new ImageLoadLimits(MaxDimension: 100), CancellationToken.None));
        var pngGeometry = Assert.ThrowsAsync<InvalidDataException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(hugePng16, RasterImageFormat.Png,
                new ImageLoadLimits(MaxDimension: 100), CancellationToken.None));
        var file = Assert.ThrowsAsync<InvalidDataException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(png, RasterImageFormat.Png,
                new ImageLoadLimits(MaxFileBytes: 10), CancellationToken.None));
        var decoded = Assert.ThrowsAsync<InvalidDataException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(png, RasterImageFormat.Png,
                new ImageLoadLimits(MaxDecodedBytes: 100), CancellationToken.None));
        var encodedWorkingSet = Assert.ThrowsAsync<InvalidDataException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(png, RasterImageFormat.Png,
                new ImageLoadLimits(MaxDecodedBytes: 20 * 20 * 12 + png.Length - 1L),
                CancellationToken.None));
        var header = Assert.ThrowsAsync<RasterImageDecodeException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(jpegWithLargeHeader,
                RasterImageFormat.Jpeg, new ImageLoadLimits(MaxHeaderBytes: 8),
                CancellationToken.None));
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RasterImageDecoder().DecodeBufferAsync(png, RasterImageFormat.Png,
                ImageLoadLimits.Default, cancelled.Token));

        Assert.Multiple(() => {
            Assert.That(geometry!.Message, Does.Contain("5000x4000"));
            Assert.That(pngGeometry!.Message, Does.Contain("5000x20"));
            Assert.That(file!.Message, Does.Contain("source size"));
            Assert.That(decoded!.Message, Does.Contain("working set"));
            Assert.That(encodedWorkingSet!.Message, Does.Contain("working set"));
            Assert.That(header!.Message, Does.Contain("configured limit"));
        });
    }

    [Test]
    public async Task Base_save_load_and_bitmap_source_close_all_inherited_raster_stubs() {
        const int width = 12;
        const int height = 7;
        var pixels = Enumerable.Range(0, width * height)
            .Select(static value => (ushort)(value * 719)).ToArray();
        var metadata = new ImageMetaData();
        metadata.Camera.Name = "Roundtrip Camera";
        metadata.Camera.Gain = 120;
        metadata.Camera.SensorType = SensorType.RGGB;
        metadata.Image.ExposureTime = 42.5;
        metadata.Target.Name = "M42";
        var image = new BaseImageData(pixels, width, height, 16, true, metadata,
            null!, null!, null!);
        var save = new FileSaveInfo {
            FilePath = Path.Combine(_root, "base-roundtrip"),
            FileType = OpenAstroAra.Core.Enums.FileType.TIFF,
            TIFFCompressionType = TIFFCompressionType.LZW,
        };

        var path = await image.SaveToDisk(save, forceFileType: true);
        var loaded = await BaseImageData.FromFile(path, bitDepth: 16, isBayered: false,
            rawConverter: null, _factory, CancellationToken.None);
        var png = EncodeSkiaRaster(SKEncodedImageFormat.Png, 9, 5);
        var exposure = await ImageArrayExposureData.FromBitmapSource(png, _factory);
        var bitmapImage = await exposure.ToImageData();
        var tiffExposure = await ImageArrayExposureData.FromBitmapSource(
            await File.ReadAllBytesAsync(path), _factory);
        var tiffBitmapImage = await tiffExposure.ToImageData();
        var sourceImage = await _factory.LoadAsync(path, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(path, Does.EndWith(".tif"));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(BaseImageData.FileIsSupported(path), Is.True);
            Assert.That(loaded.Data.FlatArray, Is.EqualTo(pixels));
            Assert.That(loaded.MetaData.Camera.Name, Is.EqualTo("Roundtrip Camera"));
            Assert.That(loaded.MetaData.Camera.Gain, Is.EqualTo(120));
            Assert.That(loaded.MetaData.Camera.SensorType, Is.EqualTo(SensorType.RGGB));
            Assert.That(loaded.Properties.IsBayered, Is.True);
            Assert.That(loaded.MetaData.Image.ExposureTime, Is.EqualTo(42.5));
            Assert.That(loaded.MetaData.Target.Name, Is.EqualTo("M42"));
            Assert.That(bitmapImage.Properties.Width, Is.EqualTo(9));
            Assert.That(bitmapImage.Properties.Height, Is.EqualTo(5));
            Assert.That(bitmapImage.Properties.BitDepth, Is.EqualTo(16));
            Assert.That(bitmapImage.Properties.IsBayered, Is.False);
            Assert.That(tiffBitmapImage.Properties.IsBayered, Is.True);
            Assert.That(tiffBitmapImage.MetaData.Camera.SensorType, Is.EqualTo(SensorType.RGGB));
            Assert.That(sourceImage.CfaPattern, Is.EqualTo("RGGB"));
            Assert.That(sourceImage.MetaData.Camera.SensorType, Is.EqualTo(SensorType.RGGB));
        });
    }

    [Test]
    public async Task Source_factory_uses_raster_signatures_and_preview_keeps_color() {
        var pngPath = Path.Combine(_root, "renamed-png.fits");
        var jpegPath = Path.Combine(_root, "renamed-jpeg.png");
        var tiffPath = Path.Combine(_root, "renamed-tiff.bin");
        await File.WriteAllBytesAsync(pngPath, EncodeSkiaRaster(SKEncodedImageFormat.Png, 13, 9));
        await File.WriteAllBytesAsync(jpegPath, EncodeSkiaRaster(SKEncodedImageFormat.Jpeg, 13, 9));
        TiffImageWriter.WriteGrayscale16(tiffPath, new ushort[24], 6, 4,
            TIFFCompressionType.LZW, imageDescription: null, CancellationToken.None);

        var png = await _factory.LoadAsync(pngPath, CancellationToken.None);
        var jpeg = await _factory.LoadAsync(jpegPath, CancellationToken.None);
        var tiff = await _factory.LoadAsync(tiffPath, CancellationToken.None);
        using var previewService = new PreviewImageService(Path.Combine(_root, "preview-cache"),
            _factory);
        var preview = await previewService.RenderAsync(new PreviewRenderRequest(
            Guid.NewGuid(), pngPath, null, StretchAlgorithm.Linear, new StretchParams(),
            MaxDimension: 512, ApplyDebayer: false, PreviewChannelMode.Rgb,
            Invert: false, Saturation: 1, CropX: null, CropY: null,
            CropWidth: null, CropHeight: null), CancellationToken.None);
        using var bitmap = SKBitmap.Decode(preview.Bytes);

        Assert.Multiple(() => {
            Assert.That(png.Format, Is.EqualTo(SourceImageFormat.Png));
            Assert.That(png.ColorData, Is.Not.Null);
            Assert.That(png.MetaData.GenericHeaders.Any(static header =>
                header.Key == "RASTERPREVIEWONLY"), Is.True);
            Assert.That(jpeg.Format, Is.EqualTo(SourceImageFormat.Jpeg));
            Assert.That(jpeg.ColorData, Is.Not.Null);
            Assert.That(tiff.Format, Is.EqualTo(SourceImageFormat.Tiff));
            Assert.That(tiff.ColorData, Is.Null);
            Assert.That(bitmap, Is.Not.Null);
            Assert.That(bitmap!.Width, Is.EqualTo(13));
            Assert.That(bitmap.Height, Is.EqualTo(9));
            Assert.That(preview.Metadata.ChannelMode, Is.EqualTo("rgb"));
            Assert.That(preview.Metadata.DebayerMode, Is.EqualTo("skia_srgb"));
        });
    }

    [Test]
    public void Cancelled_tiff_write_removes_partial_output() {
        var path = Path.Combine(_root, "cancelled.tif");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TiffImageWriter.WriteGrayscale16(path,
            new ushort[16], 4, 4, TIFFCompressionType.ZIP, null, cancellation.Token));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void Raster_metadata_preserves_embedded_cfa_and_color_planes_take_precedence() {
        var embedded = new ImageMetaData();
        embedded.Camera.SensorType = SensorType.GBRG;
        var color = new ImageMetaData();
        color.Camera.SensorType = SensorType.RGGB;
        var assumed = new ImageMetaData();

        var embeddedCfa = RasterMetadata.ApplyColorModel(embedded, hasColorPlanes: false);
        var colorCfa = RasterMetadata.ApplyColorModel(color, hasColorPlanes: true);
        var assumedCfa = RasterMetadata.ApplyColorModel(assumed,
            hasColorPlanes: false, assumeBayered: true);

        Assert.Multiple(() => {
            Assert.That(embeddedCfa, Is.EqualTo("GBRG"));
            Assert.That(embedded.Camera.SensorType, Is.EqualTo(SensorType.GBRG));
            Assert.That(colorCfa, Is.Null);
            Assert.That(color.Camera.SensorType, Is.EqualTo(SensorType.Color));
            Assert.That(assumedCfa, Is.EqualTo("RGGB"));
            Assert.That(assumed.Camera.SensorType, Is.EqualTo(SensorType.RGGB));
        });
    }

    private static byte[] EncodeSkiaRaster(SKEncodedImageFormat format, int width, int height) {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
            SKAlphaType.Opaque));
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / Math.Max(1, width - 1)),
                    (byte)(y * 255 / Math.Max(1, height - 1)), (byte)(x + y), 255));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 95 : 100);
        return data.ToArray();
    }

    private static byte[] EncodePng16(ushort[] pixels, int width, int height,
            bool interlaced = false, bool exerciseFilters = false) =>
        EncodePng16Planes([pixels], width, height, colorType: 0,
            interlaced, exerciseFilters);

    private static byte[] EncodePng16Planes(ushort[][] planes, int width, int height,
            byte colorType, bool interlaced, bool exerciseFilters) {
        Assert.That(planes, Is.Not.Empty);
        Assert.That(planes, Has.All.Length.EqualTo(width * height));
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 16;
        ihdr[9] = colorType;
        ihdr[12] = interlaced ? (byte)1 : (byte)0;
        WritePngChunk(output, "IHDR", ihdr);
        using var raw = new MemoryStream();
        if (!interlaced) {
            WritePngPass(raw, planes, width, height, startX: 0, startY: 0,
                stepX: 1, stepY: 1, passNumber: 0, exerciseFilters);
        } else {
            int[] startX = [0, 4, 0, 2, 0, 1, 0];
            int[] startY = [0, 0, 4, 0, 2, 0, 1];
            int[] stepX = [8, 8, 4, 4, 2, 2, 1];
            int[] stepY = [8, 8, 8, 4, 4, 2, 2];
            for (var pass = 0; pass < startX.Length; pass++) {
                WritePngPass(raw, planes, width, height, startX[pass], startY[pass],
                    stepX[pass], stepY[pass], pass, exerciseFilters);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngPass(Stream output, ushort[][] planes, int imageWidth,
            int imageHeight, int startX, int startY, int stepX, int stepY, int passNumber,
            bool exerciseFilters) {
        var width = PassLength(imageWidth, startX, stepX);
        var height = PassLength(imageHeight, startY, stepY);
        if (width == 0 || height == 0) return;
        var bytesPerPixel = planes.Length * sizeof(ushort);
        var previous = new byte[width * bytesPerPixel];
        for (var passY = 0; passY < height; passY++) {
            var current = new byte[previous.Length];
            var y = startY + passY * stepY;
            for (var passX = 0; passX < width; passX++) {
                var x = startX + passX * stepX;
                for (var channel = 0; channel < planes.Length; channel++) {
                    BinaryPrimitives.WriteUInt16BigEndian(
                        current.AsSpan(passX * bytesPerPixel + channel * 2, 2),
                        planes[channel][y * imageWidth + x]);
                }
            }
            var filter = exerciseFilters ? (byte)((passY + passNumber) % 5) : (byte)0;
            output.WriteByte(filter);
            output.Write(FilterPngRow(current, previous, bytesPerPixel, filter));
            previous = current;
        }
    }

    private static byte[] FilterPngRow(byte[] current, byte[] previous,
            int bytesPerPixel, byte filter) {
        var encoded = new byte[current.Length];
        for (var index = 0; index < current.Length; index++) {
            var left = index < bytesPerPixel ? 0 : current[index - bytesPerPixel];
            var above = previous[index];
            var upperLeft = index < bytesPerPixel ? 0 : previous[index - bytesPerPixel];
            var predictor = filter switch {
                0 => 0,
                1 => left,
                2 => above,
                3 => (left + above) >> 1,
                4 => PngPaeth(left, above, upperLeft),
                _ => throw new ArgumentOutOfRangeException(nameof(filter)),
            };
            encoded[index] = unchecked((byte)(current[index] - predictor));
        }
        return encoded;
    }

    private static int PngPaeth(int left, int above, int upperLeft) {
        var predictor = left + above - upperLeft;
        var leftDistance = Math.Abs(predictor - left);
        var aboveDistance = Math.Abs(predictor - above);
        var upperLeftDistance = Math.Abs(predictor - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static int PassLength(int length, int start, int step) => length <= start
        ? 0
        : (length - start + step - 1) / step;

    private static void WritePngChunk(Stream destination, string type, byte[] data) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        destination.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(length, PngCrc32(crcInput));
        destination.Write(length);
    }

    private static byte[] InsertPngChunkAfterHeader(byte[] png, string type, byte[] data) {
        const int afterHeader = 8 + 12 + 13;
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, afterHeader));
        WritePngChunk(output, type, data);
        output.Write(png.AsSpan(afterHeader));
        return output.ToArray();
    }

    private static uint PngCrc32(ReadOnlySpan<byte> bytes) {
        var crc = uint.MaxValue;
        foreach (var value in bytes) {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void WriteTiledRgbTiff(string path, int width, int height,
            Orientation orientation) {
        using var image = Tiff.Open(path, "w");
        SetTiffFields(image, width, height, bits: 8, samples: 3,
            Photometric.RGB, PlanarConfig.CONTIG, orientation, Compression.LZW);
        image.SetField(TiffTag.TILEWIDTH, 16);
        image.SetField(TiffTag.TILELENGTH, 16);
        var tile = new byte[image.TileSize()];
        for (var tileY = 0; tileY < height; tileY += 16) {
            for (var tileX = 0; tileX < width; tileX += 16) {
                Array.Clear(tile);
                for (var localY = 0; localY < 16; localY++) {
                    for (var localX = 0; localX < 16; localX++) {
                        var x = tileX + localX;
                        var y = tileY + localY;
                        if (x >= width || y >= height) continue;
                        var offset = (localY * 16 + localX) * 3;
                        tile[offset] = (byte)x;
                        tile[offset + 1] = (byte)y;
                        tile[offset + 2] = 55;
                    }
                }
                Assert.That(image.WriteTile(tile, tileX, tileY, 0, 0), Is.GreaterThan(0));
            }
        }
        Assert.That(image.WriteDirectory(), Is.True);
    }

    private static void WriteSeparateRgbTiff(string path, int width, int height) {
        using var image = Tiff.Open(path, "w");
        SetTiffFields(image, width, height, bits: 8, samples: 3,
            Photometric.RGB, PlanarConfig.SEPARATE, Orientation.TOPLEFT, Compression.LZW);
        image.SetField(TiffTag.ROWSPERSTRIP, height);
        var row = new byte[width];
        for (short plane = 0; plane < 3; plane++) {
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    row[x] = plane switch {
                        0 => (byte)x,
                        1 => (byte)y,
                        _ => 77,
                    };
                }
                Assert.That(image.WriteScanline(row, y, plane), Is.True);
            }
        }
        Assert.That(image.WriteDirectory(), Is.True);
    }

    private static void WriteFloatTiff(string path, float[] values) {
        using var image = Tiff.Open(path, "w");
        SetTiffFields(image, values.Length, 1, bits: 32, samples: 1,
            Photometric.MINISBLACK, PlanarConfig.CONTIG, Orientation.TOPLEFT, Compression.NONE);
        image.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
        image.SetField(TiffTag.ROWSPERSTRIP, 1);
        var row = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, row, 0, row.Length);
        Assert.That(image.WriteScanline(row, 0, 0), Is.True);
        Assert.That(image.WriteDirectory(), Is.True);
    }

    private static void WriteHeaderOnlyTiff(string path, int width, int height, int bits) {
        using var image = Tiff.Open(path, "w");
        SetTiffFields(image, width, height, bits, samples: 1,
            Photometric.MINISBLACK, PlanarConfig.CONTIG, Orientation.TOPLEFT, Compression.NONE);
        image.SetField(TiffTag.ROWSPERSTRIP, height);
        var row = new byte[image.ScanlineSize()];
        Assert.That(image.WriteScanline(row, 0, 0), Is.True);
        Assert.That(image.WriteDirectory(), Is.True);
    }

    private static void SetTiffFields(Tiff image, int width, int height, int bits, int samples,
            Photometric photometric, PlanarConfig planar, Orientation orientation,
            Compression compression) {
        image.SetField(TiffTag.IMAGEWIDTH, width);
        image.SetField(TiffTag.IMAGELENGTH, height);
        image.SetField(TiffTag.BITSPERSAMPLE, bits);
        image.SetField(TiffTag.SAMPLESPERPIXEL, samples);
        image.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        image.SetField(TiffTag.PHOTOMETRIC, photometric);
        image.SetField(TiffTag.PLANARCONFIG, planar);
        image.SetField(TiffTag.ORIENTATION, orientation);
        image.SetField(TiffTag.COMPRESSION, compression);
    }
}