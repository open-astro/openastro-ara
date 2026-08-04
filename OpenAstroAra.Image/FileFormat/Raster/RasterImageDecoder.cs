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
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.FileFormat.Raster;

/// <summary>
/// Cross-platform raster decoder. JPEG and 8-bit PNG are display imports through SkiaSharp;
/// managed PNG decoding preserves 16-bit samples, and TIFF retains unsigned 8/16-bit or
/// IEEE 32-bit sample precision through LibTiff.Net.
/// </summary>
public sealed class RasterImageDecoder : IRasterImageDecoder {
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly string DecoderIdentity =
        $"managed PNG16; SkiaSharp {typeof(SKCodec).Assembly.GetName().Version}; "
        + $"LibTiff.NET {typeof(Tiff).Assembly.GetName().Version}";

    public string Version => DecoderIdentity;

    public Task<DecodedRasterImage> DecodeFileAsync(string path, RasterImageFormat format,
            ImageLoadLimits limits, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Raster source does not exist.", fullPath);
        ValidateSourceSize(info.Length, limits);
        return Task.Run(() => DecodeFile(fullPath, format, limits, cancellationToken),
            cancellationToken);
    }

    public Task<DecodedRasterImage> DecodeBufferAsync(ReadOnlyMemory<byte> source,
            RasterImageFormat? expectedFormat, ImageLoadLimits limits,
            CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        ValidateSourceSize(source.Length, limits);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => DecodeBuffer(source, expectedFormat, limits, cancellationToken),
            cancellationToken);
    }

    public static RasterImageFormat? DetectSignature(ReadOnlySpan<byte> signature) {
        if (signature.StartsWith(PngSignature)) return RasterImageFormat.Png;
        if (signature.Length >= 3 && signature[0] == 0xff && signature[1] == 0xd8
            && signature[2] == 0xff) {
            return RasterImageFormat.Jpeg;
        }
        return IsTiffSignature(signature) ? RasterImageFormat.Tiff : null;
    }

    public static bool IsTiffSignature(ReadOnlySpan<byte> signature) => signature.Length >= 4
        && ((signature[0] == (byte)'I' && signature[1] == (byte)'I'
             && signature[2] is 0x2a or 0x2b && signature[3] == 0)
            || (signature[0] == (byte)'M' && signature[1] == (byte)'M'
                && signature[2] == 0 && signature[3] is 0x2a or 0x2b));

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "SkiaSharp's annotation declares SKCodec.Create non-null, but its native contract returns null for malformed or unsupported inputs.")]
    private static DecodedRasterImage DecodeFile(string path, RasterImageFormat format,
            ImageLoadLimits limits, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (format == RasterImageFormat.Tiff) {
            Span<byte> signature = stackalloc byte[4];
            using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                header.ReadExactly(signature);
            }
            ValidateTiffContainer(signature);
            using var image = Tiff.Open(path, "r")
                ?? throw new RasterImageDecodeException("TIFF header or first image directory is invalid.");
            return DecodeTiff(image, limits, cancellationToken);
        }

        RasterHeader headerInfo;
        using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            headerInfo = ReadRasterHeader(header, format, limits.MaxHeaderBytes);
        }
        ValidateGeometry(format.ToString(), headerInfo.Width, headerInfo.Height, limits);
        if (format == RasterImageFormat.Png && headerInfo.SourceBitDepth == 16) {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ReadFileBytes(path, limits, cancellationToken);
            return DecodePng16(source, headerInfo, limits, cancellationToken);
        }
        using var codec = SKCodec.Create(path, out var createResult)
            ?? throw DecodeFailure(format, "header", createResult);
        return DecodeSkia(codec, format, headerInfo, limits, 0, cancellationToken);
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "SkiaSharp's annotation declares SKCodec.Create non-null, but its native contract returns null for malformed or unsupported inputs.")]
    private static DecodedRasterImage DecodeBuffer(ReadOnlyMemory<byte> source,
            RasterImageFormat? expectedFormat, ImageLoadLimits limits,
            CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var detected = DetectSignature(source.Span);
        if (detected is null) {
            throw new RasterImageDecodeException("Raster signature is unsupported or malformed.");
        }
        if (expectedFormat is not null && detected != expectedFormat) {
            throw new RasterImageDecodeException(
                $"Raster signature is {detected}; expected {expectedFormat}.");
        }

        if (detected == RasterImageFormat.Tiff) {
            ValidateTiffContainer(source.Span);
            using var memory = new ReadOnlyMemoryStream(source);
            using var image = Tiff.ClientOpen("memory-raster", "r", memory, MemoryTiffStream.Instance)
                ?? throw new RasterImageDecodeException("TIFF header or first image directory is invalid.");
            return DecodeTiff(image, limits, cancellationToken, source.Length);
        }

        RasterHeader headerInfo;
        if (detected == RasterImageFormat.Png) {
            headerInfo = ReadPngHeader(source.Span);
        } else {
            var headerLength = Math.Min(source.Length, limits.MaxHeaderBytes);
            ValidateDecodedSize(detected.Value.ToString(), headerLength, limits);
            using var header = new MemoryStream(source[..headerLength].ToArray(), writable: false);
            headerInfo = ReadRasterHeader(header, detected.Value, limits.MaxHeaderBytes);
        }
        ValidateGeometry(detected.Value.ToString(), headerInfo.Width, headerInfo.Height, limits);
        if (detected == RasterImageFormat.Png && headerInfo.SourceBitDepth == 16) {
            return DecodePng16(source, headerInfo, limits, cancellationToken);
        }
        const int packedBytesPerPixel = 4;
        var outputBytesPerPixel = headerInfo.IsColor ? 8 : 2;
        ValidateDecodedSize(detected.Value.ToString(), checked(source.Length
            + (long)headerInfo.Width * headerInfo.Height
            * (packedBytesPerPixel + outputBytesPerPixel)), limits);
        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data)
            ?? throw new RasterImageDecodeException($"{detected} header is malformed.");
        return DecodeSkia(codec, detected.Value, headerInfo, limits,
            source.Length, cancellationToken);
    }

    private static DecodedRasterImage DecodeSkia(SKCodec codec, RasterImageFormat format,
            RasterHeader header, ImageLoadLimits limits, long encodedWorkingBytes,
            CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var encodedFormat = format switch {
            RasterImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            RasterImageFormat.Png => SKEncodedImageFormat.Png,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        if (codec.EncodedFormat != encodedFormat) {
            throw new RasterImageDecodeException(
                $"Raster codec identified {codec.EncodedFormat}; expected {encodedFormat}.");
        }
        if (codec.Info.Width != header.Width || codec.Info.Height != header.Height) {
            throw new RasterImageDecodeException(
                $"{format} decoded dimensions {codec.Info.Width}x{codec.Info.Height} do not match header "
                + $"{header.Width}x{header.Height}.");
        }
        if (codec.FrameCount > 1) {
            throw new NotSupportedException($"Animated {format} imports are unsupported; use one still frame.");
        }

        ValidateGeometry(format.ToString(), header.Width, header.Height, limits);
        var orientation = codec.EncodedOrigin;
        var (outputWidth, outputHeight) = OrientedDimensions(header.Width, header.Height, orientation);
        ValidateGeometry(format.ToString(), outputWidth, outputHeight, limits);
        var outputPixels = checked((long)outputWidth * outputHeight);
        const int packedBytesPerPixel = 4;
        var outputBytesPerPixel = header.IsColor ? 8 : 2;
        ValidateDecodedSize(format.ToString(),
            checked(outputPixels * (packedBytesPerPixel + outputBytesPerPixel)
                + encodedWorkingBytes), limits);

        using var srgb = SKColorSpace.CreateSrgb();
        var imageInfo = new SKImageInfo(header.Width, header.Height, SKColorType.Rgba8888,
            SKAlphaType.Unpremul, srgb);
        var packedLength = checked(imageInfo.RowBytes * imageInfo.Height);
        var packed = new byte[packedLength];
        var result = codec.GetPixels(imageInfo, packed);
        if (result != SKCodecResult.Success) throw DecodeFailure(format, "pixels", result);
        cancellationToken.ThrowIfCancellationRequested();

        var pixelCount = checked(outputWidth * outputHeight);
        ushort[] luminance;
        DecodedColorImage? colorData = null;
        if (header.IsColor) {
            var red = new ushort[pixelCount];
            var green = new ushort[pixelCount];
            var blue = new ushort[pixelCount];
            CopySkia8(packed, header.Width, header.Height, orientation,
                outputWidth, red, green, blue, cancellationToken);
            luminance = ColorPlaneMath.CreateLuminance(red, green, blue, cancellationToken);
            colorData = new DecodedColorImage(outputWidth, outputHeight, header.SourceBitDepth,
                red, green, blue, DecoderIdentity, "skia_srgb");
        } else {
            luminance = new ushort[pixelCount];
            CopySkiaGray8(packed, header.Width, header.Height, orientation,
                outputWidth, luminance, cancellationToken);
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["RASTERFORMAT"] = format.ToString(),
            ["RASTERDECODER"] = DecoderIdentity,
            ["RASTERORIENTATION"] = orientation.ToString(),
            ["RASTERCOLORSPACE"] = "sRGB",
            ["RASTERALPHA"] = "ignored_unpremultiplied",
            ["RASTERIMPORTMODE"] = "preview_only",
        };
        return new DecodedRasterImage(format, outputWidth, outputHeight,
            header.SourceBitDepth, luminance, colorData, DecoderIdentity, metadata);
    }

    private static void CopySkia8(byte[] packed, int width, int height,
            SKEncodedOrigin orientation, int outputWidth, ushort[] red, ushort[] green,
            ushort[] blue, CancellationToken cancellationToken) {
        for (var y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++) {
                var source = (y * width + x) * 4;
                var destination = OrientedIndex(x, y, width, height, orientation, outputWidth);
                red[destination] = (ushort)(packed[source] * 257);
                green[destination] = (ushort)(packed[source + 1] * 257);
                blue[destination] = (ushort)(packed[source + 2] * 257);
            }
        }
    }

    private static void CopySkiaGray8(byte[] packed, int width, int height,
            SKEncodedOrigin orientation, int outputWidth, ushort[] luminance,
            CancellationToken cancellationToken) {
        for (var y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++) {
                var destination = OrientedIndex(x, y, width, height, orientation, outputWidth);
                luminance[destination] = (ushort)(packed[(y * width + x) * 4] * 257);
            }
        }
    }

    private static DecodedRasterImage DecodePng16(ReadOnlyMemory<byte> source,
            RasterHeader header, ImageLoadLimits limits, CancellationToken cancellationToken) {
        var channels = header.ColorType switch {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException(
                $"16-bit PNG color type {header.ColorType} is unsupported."),
        };
        var pixelCount = checked((long)header.Width * header.Height);
        var outputBytes = checked(pixelCount * (header.IsColor ? 8 : 2));
        var rowBytes = checked((long)header.Width * channels * sizeof(ushort));
        ValidateDecodedSize("PNG", checked(source.Length + outputBytes + rowBytes * 2), limits);
        var pixels = Png16Decoder.Decode(source, header.Width, header.Height,
            header.ColorType, header.InterlaceMethod, cancellationToken);
        var colorData = pixels.Red is null ? null : new DecodedColorImage(
            header.Width, header.Height, 16, pixels.Red, pixels.Green!, pixels.Blue!,
            DecoderIdentity, "png_encoded_samples");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["RASTERFORMAT"] = RasterImageFormat.Png.ToString(),
            ["RASTERDECODER"] = DecoderIdentity,
            ["RASTERORIENTATION"] = SKEncodedOrigin.TopLeft.ToString(),
            ["RASTERCOLORSPACE"] = "PNG encoded samples",
            ["RASTERALPHA"] = "ignored_unpremultiplied",
            ["RASTERIMPORTMODE"] = "preview_only",
            ["PNGINTERLACE"] = header.InterlaceMethod == 0 ? "none" : "Adam7",
        };
        return new DecodedRasterImage(RasterImageFormat.Png, header.Width, header.Height,
            16, pixels.Luminance, colorData, DecoderIdentity, metadata);
    }

    private static RasterImageDecodeException DecodeFailure(RasterImageFormat format,
            string stage, SKCodecResult result) => result == SKCodecResult.IncompleteInput
        ? new RasterImageDecodeException($"{format} source is truncated during {stage} decode.")
        : new RasterImageDecodeException($"{format} {stage} decode failed: {result}.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "LibTiff.Net can surface malformed-directory faults as several runtime exception types; convert them to one safe image-decode failure while preserving cancellation and fatal exceptions.")]
    private static DecodedRasterImage DecodeTiff(Tiff image, ImageLoadLimits limits,
            CancellationToken cancellationToken, long encodedWorkingBytes = 0) {
        try {
            cancellationToken.ThrowIfCancellationRequested();
            if (image.GetField(TiffTag.DNGVERSION) is not null) {
                throw new NotSupportedException("DNG camera RAW data must be decoded through LibRaw, not TIFF.");
            }

            var width = RequiredTag(image, TiffTag.IMAGEWIDTH, "ImageWidth");
            var height = RequiredTag(image, TiffTag.IMAGELENGTH, "ImageLength");
            ValidateGeometry("TIFF", width, height, limits);
            var bits = DefaultedTag(image, TiffTag.BITSPERSAMPLE, 1);
            var samplesPerPixel = DefaultedTag(image, TiffTag.SAMPLESPERPIXEL, 1);
            var sampleFormat = (SampleFormat)DefaultedTag(image, TiffTag.SAMPLEFORMAT,
                (int)SampleFormat.UINT);
            var photometric = (Photometric)DefaultedTag(image, TiffTag.PHOTOMETRIC,
                (int)Photometric.MINISBLACK);
            var planar = (PlanarConfig)DefaultedTag(image, TiffTag.PLANARCONFIG,
                (int)PlanarConfig.CONTIG);
            var orientation = (Orientation)DefaultedTag(image, TiffTag.ORIENTATION,
                (int)Orientation.TOPLEFT);
            var compression = (Compression)DefaultedTag(image, TiffTag.COMPRESSION,
                (int)Compression.NONE);

            var isColor = photometric == Photometric.RGB;
            if (!isColor && photometric is not (Photometric.MINISBLACK or Photometric.MINISWHITE)) {
                throw new NotSupportedException(
                    $"TIFF photometric interpretation {photometric} is unsupported; grayscale or RGB required.");
            }
            var requiredSamples = isColor ? 3 : 1;
            if (samplesPerPixel < requiredSamples || samplesPerPixel > requiredSamples + 1) {
                throw new NotSupportedException(
                    $"TIFF has {samplesPerPixel} samples per pixel; {requiredSamples} plus optional alpha required.");
            }
            if (planar is not (PlanarConfig.CONTIG or PlanarConfig.SEPARATE)) {
                throw new NotSupportedException($"TIFF planar configuration {planar} is unsupported.");
            }
            ValidateTiffSampleType(bits, sampleFormat);
            var (outputWidth, outputHeight) = OrientedDimensions(width, height, orientation);
            ValidateGeometry("TIFF", outputWidth, outputHeight, limits);
            var pixelCount = checked(outputWidth * outputHeight);
            var scratchBytes = image.IsTiled() ? image.TileSize() : image.ScanlineSize();
            if (scratchBytes <= 0) {
                throw new RasterImageDecodeException("TIFF decoder reported an invalid row or tile size.");
            }
            var peakBytesPerPixel = sampleFormat == SampleFormat.IEEEFP
                ? (isColor ? 20 : 6)
                : (isColor ? 8 : 2);
            ValidateDecodedSize("TIFF", checked((long)pixelCount * peakBytesPerPixel
                + scratchBytes + encodedWorkingBytes), limits);

            ushort[] luminance;
            DecodedColorImage? colorData;
            if (sampleFormat == SampleFormat.IEEEFP) {
                (luminance, colorData) = DecodeFloatTiff(image, width, height, outputWidth,
                    orientation, planar, samplesPerPixel, isColor, photometric,
                    cancellationToken);
            } else {
                (luminance, colorData) = DecodeUnsignedTiff(image, width, height, outputWidth,
                    orientation, planar, samplesPerPixel, bits, isColor, photometric,
                    cancellationToken);
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["RASTERFORMAT"] = RasterImageFormat.Tiff.ToString(),
                ["RASTERDECODER"] = DecoderIdentity,
                ["TIFFBITSPERSAMPLE"] = bits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TIFFSAMPLEFORMAT"] = sampleFormat.ToString(),
                ["TIFFPHOTOMETRIC"] = photometric.ToString(),
                ["TIFFCOMPRESSION"] = compression.ToString(),
                ["TIFFPLANARCONFIG"] = planar.ToString(),
                ["TIFFORIENTATION"] = orientation.ToString(),
            };
            AddOptionalTag(image, TiffTag.SOFTWARE, "TIFFSOFTWARE", metadata);
            AddOptionalTag(image, TiffTag.IMAGEDESCRIPTION, "TIFFIMAGEDESCRIPTION", metadata);
            return new DecodedRasterImage(RasterImageFormat.Tiff, outputWidth, outputHeight,
                bits, luminance, colorData, DecoderIdentity, metadata);
        } catch (OperationCanceledException) {
            throw;
        } catch (InvalidDataException) {
            throw;
        } catch (Exception ex) when (ex is not OutOfMemoryException
                                     and not StackOverflowException
                                     and not RasterImageDecodeException
                                     and not NotSupportedException) {
            throw new RasterImageDecodeException("TIFF data is malformed or truncated.", ex);
        }
    }

    private static (ushort[] Luminance, DecodedColorImage? Color) DecodeUnsignedTiff(
            Tiff image, int width, int height, int outputWidth, Orientation orientation,
            PlanarConfig planar, int samplesPerPixel, int bits, bool isColor,
            Photometric photometric, CancellationToken cancellationToken) {
        var outputHeight = OrientedDimensions(width, height, orientation).Height;
        var pixelCount = checked(outputWidth * outputHeight);
        var red = isColor ? new ushort[pixelCount] : null;
        var green = isColor ? new ushort[pixelCount] : null;
        var blue = isColor ? new ushort[pixelCount] : null;
        var mono = isColor ? null : new ushort[pixelCount];
        ReadTiffSamples(image, width, height, planar, samplesPerPixel,
            isColor ? 3 : 1, bits / 8, (x, y, channel, buffer, offset) => {
                var sample = bits == 8
                    ? (ushort)(buffer[offset] * 257)
                    : ReadNativeUInt16(buffer.AsSpan(offset, 2));
                if (!isColor && photometric == Photometric.MINISWHITE) {
                    sample = (ushort)(ushort.MaxValue - sample);
                }
                var destination = OrientedIndex(x, y, width, height, orientation, outputWidth);
                if (!isColor) mono![destination] = sample;
                else if (channel == 0) red![destination] = sample;
                else if (channel == 1) green![destination] = sample;
                else blue![destination] = sample;
            }, cancellationToken);

        if (!isColor) return (mono!, null);
        var luminance = ColorPlaneMath.CreateLuminance(red!, green!, blue!, cancellationToken);
        return (luminance, new DecodedColorImage(outputWidth, outputHeight, bits,
            red!, green!, blue!, DecoderIdentity, "source_rgb_linear"));
    }

    private static (ushort[] Luminance, DecodedColorImage? Color) DecodeFloatTiff(
            Tiff image, int width, int height, int outputWidth, Orientation orientation,
            PlanarConfig planar, int samplesPerPixel, bool isColor, Photometric photometric,
            CancellationToken cancellationToken) {
        var outputHeight = OrientedDimensions(width, height, orientation).Height;
        var pixelCount = checked(outputWidth * outputHeight);
        var redFloat = isColor ? new float[pixelCount] : null;
        var greenFloat = isColor ? new float[pixelCount] : null;
        var blueFloat = isColor ? new float[pixelCount] : null;
        var monoFloat = isColor ? null : new float[pixelCount];
        ReadTiffSamples(image, width, height, planar, samplesPerPixel,
            isColor ? 3 : 1, sizeof(float), (x, y, channel, buffer, offset) => {
                var sample = BitConverter.ToSingle(buffer, offset);
                var destination = OrientedIndex(x, y, width, height, orientation, outputWidth);
                if (!isColor) monoFloat![destination] = sample;
                else if (channel == 0) redFloat![destination] = sample;
                else if (channel == 1) greenFloat![destination] = sample;
                else blueFloat![destination] = sample;
            }, cancellationToken);

        if (!isColor) {
            var range = FindFiniteRange(monoFloat!, null, null, cancellationToken);
            var mono = NormalizeFloat(monoFloat!, range, invert: photometric == Photometric.MINISWHITE,
                cancellationToken);
            return (mono, null);
        }

        var colorRange = FindFiniteRange(redFloat!, greenFloat, blueFloat, cancellationToken);
        var red = NormalizeFloat(redFloat!, colorRange, invert: false, cancellationToken);
        var green = NormalizeFloat(greenFloat!, colorRange, invert: false, cancellationToken);
        var blue = NormalizeFloat(blueFloat!, colorRange, invert: false, cancellationToken);
        var luminance = ColorPlaneMath.CreateLuminance(red, green, blue, cancellationToken);
        return (luminance, new DecodedColorImage(outputWidth, outputHeight, 32,
            red, green, blue, DecoderIdentity, "source_rgb_linear_float_normalized"));
    }

    private delegate void TiffSampleConsumer(
        int x, int y, int channel, byte[] buffer, int offset);

    private static void ReadTiffSamples(Tiff image, int width, int height,
            PlanarConfig planar, int samplesPerPixel, int requiredSamples, int bytesPerSample,
            TiffSampleConsumer consume, CancellationToken cancellationToken) {
        if (image.IsTiled()) {
            ReadTiffTiles(image, width, height, planar, samplesPerPixel, requiredSamples,
                bytesPerSample, consume, cancellationToken);
            return;
        }

        var row = new byte[image.ScanlineSize()];
        if (planar == PlanarConfig.CONTIG) {
            var requiredRowBytes = checked(width * samplesPerPixel * bytesPerSample);
            if (row.Length < requiredRowBytes) {
                throw new RasterImageDecodeException(
                    $"TIFF scanline is {row.Length} bytes; at least {requiredRowBytes} required.");
            }
            for (var y = 0; y < height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!image.ReadScanline(row, y, 0)) {
                    throw new RasterImageDecodeException($"TIFF scanline {y} is truncated.");
                }
                for (var x = 0; x < width; x++) {
                    var pixelOffset = x * samplesPerPixel * bytesPerSample;
                    for (var channel = 0; channel < requiredSamples; channel++) {
                        consume(x, y, channel, row, pixelOffset + channel * bytesPerSample);
                    }
                }
            }
            return;
        }

        var requiredPlaneBytes = checked(width * bytesPerSample);
        if (row.Length < requiredPlaneBytes) {
            throw new RasterImageDecodeException(
                $"TIFF planar scanline is {row.Length} bytes; at least {requiredPlaneBytes} required.");
        }
        for (short channel = 0; channel < requiredSamples; channel++) {
            for (var y = 0; y < height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!image.ReadScanline(row, y, channel)) {
                    throw new RasterImageDecodeException(
                        $"TIFF scanline {y}, plane {channel} is truncated.");
                }
                for (var x = 0; x < width; x++) {
                    consume(x, y, channel, row, x * bytesPerSample);
                }
            }
        }
    }

    private static void ReadTiffTiles(Tiff image, int width, int height,
            PlanarConfig planar, int samplesPerPixel, int requiredSamples, int bytesPerSample,
            TiffSampleConsumer consume, CancellationToken cancellationToken) {
        var tileWidth = RequiredTag(image, TiffTag.TILEWIDTH, "TileWidth");
        var tileHeight = RequiredTag(image, TiffTag.TILELENGTH, "TileLength");
        if (tileWidth <= 0 || tileHeight <= 0) {
            throw new RasterImageDecodeException("TIFF tile dimensions must be positive.");
        }
        var tile = new byte[image.TileSize()];
        var planeSamples = planar == PlanarConfig.CONTIG ? samplesPerPixel : 1;
        var requiredTileBytes = checked(tileWidth * tileHeight * planeSamples * bytesPerSample);
        if (tile.Length < requiredTileBytes) {
            throw new RasterImageDecodeException(
                $"TIFF tile is {tile.Length} bytes; at least {requiredTileBytes} required.");
        }
        var planeCount = planar == PlanarConfig.CONTIG ? 1 : requiredSamples;
        for (short plane = 0; plane < planeCount; plane++) {
            for (var tileY = 0; tileY < height; tileY += tileHeight) {
                for (var tileX = 0; tileX < width; tileX += tileWidth) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = image.ReadTile(tile, 0, tileX, tileY, 0, plane);
                    if (read < requiredTileBytes) {
                        throw new RasterImageDecodeException(
                            $"TIFF tile at {tileX},{tileY}, plane {plane} is truncated.");
                    }
                    var copyHeight = Math.Min(tileHeight, height - tileY);
                    var copyWidth = Math.Min(tileWidth, width - tileX);
                    for (var localY = 0; localY < copyHeight; localY++) {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var localX = 0; localX < copyWidth; localX++) {
                            var pixelOffset = (localY * tileWidth + localX)
                                * planeSamples * bytesPerSample;
                            if (planar == PlanarConfig.CONTIG) {
                                for (var channel = 0; channel < requiredSamples; channel++) {
                                    consume(tileX + localX, tileY + localY, channel, tile,
                                        pixelOffset + channel * bytesPerSample);
                                }
                            } else {
                                consume(tileX + localX, tileY + localY, plane, tile, pixelOffset);
                            }
                        }
                    }
                }
            }
        }
    }

    private static (float Minimum, float Maximum) FindFiniteRange(float[] first,
            float[]? second, float[]? third, CancellationToken cancellationToken) {
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        FindRange(first, ref minimum, ref maximum, cancellationToken);
        if (second is not null) FindRange(second, ref minimum, ref maximum, cancellationToken);
        if (third is not null) FindRange(third, ref minimum, ref maximum, cancellationToken);
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum)) {
            throw new RasterImageDecodeException("TIFF floating-point image contains no finite samples.");
        }
        return (minimum, maximum);
    }

    private static void FindRange(float[] values, ref float minimum, ref float maximum,
            CancellationToken cancellationToken) {
        for (var index = 0; index < values.Length; index++) {
            if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = values[index];
            if (!float.IsFinite(value)) continue;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
    }

    private static ushort[] NormalizeFloat(float[] values, (float Minimum, float Maximum) range,
            bool invert, CancellationToken cancellationToken) {
        var output = new ushort[values.Length];
        var span = (double)range.Maximum - range.Minimum;
        for (var index = 0; index < output.Length; index++) {
            if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = values[index];
            ushort normalized;
            if (!float.IsFinite(value) || span <= 0) normalized = 0;
            else normalized = (ushort)Math.Clamp(
                Math.Round(((value - range.Minimum) / span) * ushort.MaxValue),
                0, ushort.MaxValue);
            output[index] = invert ? (ushort)(ushort.MaxValue - normalized) : normalized;
        }
        return output;
    }

    private static void ValidateTiffSampleType(int bits, SampleFormat sampleFormat) {
        if (sampleFormat == SampleFormat.UINT && bits is 8 or 16) return;
        if (sampleFormat == SampleFormat.IEEEFP && bits == 32) return;
        throw new NotSupportedException(
            $"TIFF sample format {sampleFormat} with {bits} bits is unsupported. "
            + "Supported samples: unsigned 8/16-bit and IEEE 32-bit float.");
    }

    private static void AddOptionalTag(Tiff image, TiffTag tag, string key,
            Dictionary<string, string> metadata) {
        var values = image.GetField(tag);
        if (values is null || values.Length == 0) return;
        var value = values[0].ToString()?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrEmpty(value)) return;
        metadata[key] = value.Length <= 65_536 ? value : value[..65_536];
    }

    private static int RequiredTag(Tiff image, TiffTag tag, string name) {
        var values = image.GetField(tag);
        if (values is null || values.Length == 0) {
            throw new RasterImageDecodeException($"TIFF directory is missing required {name} tag.");
        }
        return values[0].ToInt();
    }

    private static int DefaultedTag(Tiff image, TiffTag tag, int defaultValue) {
        var values = image.GetFieldDefaulted(tag);
        return values is null || values.Length == 0 ? defaultValue : values[0].ToInt();
    }

    private static ushort ReadNativeUInt16(ReadOnlySpan<byte> bytes) => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static RasterHeader ReadRasterHeader(Stream source, RasterImageFormat format,
            int maxHeaderBytes) => format switch {
                RasterImageFormat.Png => ReadPngHeader(source),
                RasterImageFormat.Jpeg => ReadJpegHeader(source, maxHeaderBytes),
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };

    private static RasterHeader ReadPngHeader(Stream source) {
        var header = new byte[29];
        try {
            source.ReadExactly(header);
        } catch (EndOfStreamException ex) {
            throw new RasterImageDecodeException("PNG header is truncated.", ex);
        }
        return ReadPngHeader(header);
    }

    private static RasterHeader ReadPngHeader(ReadOnlySpan<byte> header) {
        if (header.Length < 29) {
            throw new RasterImageDecodeException("PNG header is truncated.");
        }
        if (!header[..8].SequenceEqual(PngSignature)
            || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8)) {
            throw new RasterImageDecodeException("PNG signature or IHDR chunk is invalid.");
        }
        var widthValue = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var heightValue = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (widthValue > int.MaxValue || heightValue > int.MaxValue) {
            throw new RasterImageDecodeException("PNG dimensions exceed the managed integer range.");
        }
        var bitDepth = header[24];
        var colorType = header[25];
        var validDepth = colorType switch {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (!validDepth) {
            throw new NotSupportedException(
                $"PNG color type {colorType} with {bitDepth}-bit samples is unsupported.");
        }
        if (header[26] != 0 || header[27] != 0 || header[28] is not (0 or 1)) {
            throw new RasterImageDecodeException("PNG IHDR compression, filter, or interlace method is invalid.");
        }
        return new RasterHeader((int)widthValue, (int)heightValue, bitDepth,
            colorType is 2 or 3 or 6, colorType, header[28]);
    }

    private static RasterHeader ReadJpegHeader(Stream source, int maxHeaderBytes) {
        var consumed = 0;
        if (ReadByte(source, ref consumed, maxHeaderBytes) != 0xff
            || ReadByte(source, ref consumed, maxHeaderBytes) != 0xd8) {
            throw new RasterImageDecodeException("JPEG start-of-image marker is invalid.");
        }
        while (consumed < maxHeaderBytes) {
            int markerPrefix;
            do {
                markerPrefix = ReadByte(source, ref consumed, maxHeaderBytes);
            } while (markerPrefix != 0xff);
            int marker;
            do {
                marker = ReadByte(source, ref consumed, maxHeaderBytes);
            } while (marker == 0xff);
            if (marker == 0x00) continue;
            if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (marker is 0xd9 or 0xda) {
                throw new RasterImageDecodeException("JPEG has no supported start-of-frame header.");
            }
            var length = (ReadByte(source, ref consumed, maxHeaderBytes) << 8)
                | ReadByte(source, ref consumed, maxHeaderBytes);
            if (length < 2) throw new RasterImageDecodeException("JPEG segment length is invalid.");
            if (IsStartOfFrame(marker)) {
                if (length < 8) throw new RasterImageDecodeException("JPEG start-of-frame is truncated.");
                var precision = ReadByte(source, ref consumed, maxHeaderBytes);
                var height = (ReadByte(source, ref consumed, maxHeaderBytes) << 8)
                    | ReadByte(source, ref consumed, maxHeaderBytes);
                var width = (ReadByte(source, ref consumed, maxHeaderBytes) << 8)
                    | ReadByte(source, ref consumed, maxHeaderBytes);
                var components = ReadByte(source, ref consumed, maxHeaderBytes);
                if (precision != 8) {
                    throw new NotSupportedException(
                        $"JPEG {precision}-bit samples are unsupported; 8-bit JPEG required.");
                }
                if (components is not (1 or 3 or 4)) {
                    throw new NotSupportedException(
                        $"JPEG has {components} components; grayscale, RGB, or CMYK required.");
                }
                return new RasterHeader(width, height, precision, components != 1);
            }
            SkipExactly(source, length - 2, ref consumed, maxHeaderBytes);
        }
        throw new RasterImageDecodeException(
            $"JPEG header exceeds the configured limit {maxHeaderBytes} bytes.");
    }

    private static bool IsStartOfFrame(int marker) => marker is 0xc0 or 0xc1 or 0xc2 or 0xc3
        or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static int ReadByte(Stream source, ref int consumed, int maxHeaderBytes) {
        if (consumed >= maxHeaderBytes) {
            throw new RasterImageDecodeException(
                $"Raster header exceeds the configured limit {maxHeaderBytes} bytes.");
        }
        var value = source.ReadByte();
        if (value < 0) throw new RasterImageDecodeException("Raster header is truncated.");
        consumed++;
        return value;
    }

    private static void SkipExactly(Stream source, int count, ref int consumed, int maxHeaderBytes) {
        if (count < 0 || consumed > maxHeaderBytes - count) {
            throw new RasterImageDecodeException(
                $"Raster header exceeds the configured limit {maxHeaderBytes} bytes.");
        }
        if (source.CanSeek) {
            if (source.Length - source.Position < count) {
                throw new RasterImageDecodeException("Raster header is truncated.");
            }
            source.Seek(count, SeekOrigin.Current);
            consumed += count;
            return;
        }
        Span<byte> buffer = stackalloc byte[4096];
        var remaining = count;
        while (remaining > 0) {
            var read = source.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read == 0) throw new RasterImageDecodeException("Raster header is truncated.");
            remaining -= read;
            consumed += read;
        }
    }

    private static void ValidateTiffContainer(ReadOnlySpan<byte> signature) {
        if (!IsTiffSignature(signature)) {
            throw new RasterImageDecodeException("TIFF byte-order or version signature is invalid.");
        }
        var bigTiff = (signature[0] == (byte)'I' && signature[2] == 0x2b)
            || (signature[0] == (byte)'M' && signature[3] == 0x2b);
        if (bigTiff) {
            throw new NotSupportedException("BigTIFF is unsupported; classic TIFF with 32-bit offsets required.");
        }
    }

    private static void ValidateSourceSize(long bytes, ImageLoadLimits limits) {
        if (bytes <= 0 || bytes > limits.MaxFileBytes || bytes > Array.MaxLength) {
            throw new InvalidDataException(
                $"Raster source size {bytes} is outside the supported range "
                + $"1-{Math.Min(limits.MaxFileBytes, Array.MaxLength)} bytes.");
        }
    }

    private static byte[] ReadFileBytes(string path, ImageLoadLimits limits,
            CancellationToken cancellationToken) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        ValidateSourceSize(stream.Length, limits);
        var source = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < source.Length) {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(source.AsSpan(offset,
                Math.Min(1024 * 1024, source.Length - offset)));
            if (read == 0) {
                throw new RasterImageDecodeException("PNG source changed or was truncated while reading.");
            }
            offset += read;
        }
        return source;
    }

    private static void ValidateGeometry(string format, int width, int height,
            ImageLoadLimits limits) {
        if (width <= 0 || height <= 0 || width > limits.MaxDimension
            || height > limits.MaxDimension) {
            throw new InvalidDataException(
                $"{format} dimensions {width}x{height} exceed the configured dimension limit "
                + $"{limits.MaxDimension}.");
        }
        var pixels = (long)width * height;
        if (pixels > limits.MaxPixelCount || pixels > int.MaxValue) {
            throw new InvalidDataException(
                $"{format} pixel count {pixels} exceeds the configured limit {limits.MaxPixelCount}.");
        }
    }

    private static void ValidateDecodedSize(string format, long bytes, ImageLoadLimits limits) {
        if (bytes <= 0 || bytes > limits.MaxDecodedBytes || bytes > int.MaxValue) {
            throw new InvalidDataException(
                $"{format} decoded working set {bytes} bytes exceeds the configured limit "
                + $"{Math.Min(limits.MaxDecodedBytes, int.MaxValue)} bytes.");
        }
    }

    private static (int Width, int Height) OrientedDimensions(int width, int height,
            SKEncodedOrigin orientation) => orientation is SKEncodedOrigin.LeftTop
                or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom
                or SKEncodedOrigin.LeftBottom
            ? (height, width)
            : (width, height);

    private static (int Width, int Height) OrientedDimensions(int width, int height,
            Orientation orientation) => orientation is Orientation.LEFTTOP
                or Orientation.RIGHTTOP or Orientation.RIGHTBOT or Orientation.LEFTBOT
            ? (height, width)
            : orientation is Orientation.TOPLEFT or Orientation.TOPRIGHT
                or Orientation.BOTRIGHT or Orientation.BOTLEFT
                ? (width, height)
                : throw new NotSupportedException($"TIFF orientation {orientation} is unsupported.");

    private static int OrientedIndex(int x, int y, int width, int height,
            SKEncodedOrigin orientation, int outputWidth) {
        (int X, int Y) mapped = orientation switch {
            SKEncodedOrigin.TopLeft => (x, y),
            SKEncodedOrigin.TopRight => (width - 1 - x, y),
            SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y),
            SKEncodedOrigin.BottomLeft => (x, height - 1 - y),
            SKEncodedOrigin.LeftTop => (y, x),
            SKEncodedOrigin.RightTop => (height - 1 - y, x),
            SKEncodedOrigin.RightBottom => (height - 1 - y, width - 1 - x),
            SKEncodedOrigin.LeftBottom => (y, width - 1 - x),
            _ => throw new NotSupportedException($"Raster orientation {orientation} is unsupported."),
        };
        return checked(mapped.Y * outputWidth + mapped.X);
    }

    private static int OrientedIndex(int x, int y, int width, int height,
            Orientation orientation, int outputWidth) {
        (int X, int Y) mapped = orientation switch {
            Orientation.TOPLEFT => (x, y),
            Orientation.TOPRIGHT => (width - 1 - x, y),
            Orientation.BOTRIGHT => (width - 1 - x, height - 1 - y),
            Orientation.BOTLEFT => (x, height - 1 - y),
            Orientation.LEFTTOP => (y, x),
            Orientation.RIGHTTOP => (height - 1 - y, x),
            Orientation.RIGHTBOT => (height - 1 - y, width - 1 - x),
            Orientation.LEFTBOT => (y, width - 1 - x),
            _ => throw new NotSupportedException($"TIFF orientation {orientation} is unsupported."),
        };
        return checked(mapped.Y * outputWidth + mapped.X);
    }

    private sealed record RasterHeader(int Width, int Height, int SourceBitDepth, bool IsColor,
        byte ColorType = 0, byte InterlaceMethod = 0);

    private sealed class MemoryTiffStream : TiffStream {
        public static MemoryTiffStream Instance { get; } = new();

        public override int Read(object clientData, byte[] buffer, int offset, int count) =>
            ((Stream)clientData).Read(buffer, offset, count);

        public override void Write(object clientData, byte[] buffer, int offset, int count) =>
            ((Stream)clientData).Write(buffer, offset, count);

        public override long Seek(object clientData, long offset, SeekOrigin origin) =>
            ((Stream)clientData).Seek(offset, origin);

        public override void Close(object clientData) {
            // Caller owns the managed stream.
        }

        public override long Size(object clientData) => ((Stream)clientData).Length;
    }

    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> source) : Stream {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position {
            get => _position;
            set {
                if (value < 0 || value > source.Length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var count = Math.Min(buffer.Length, source.Length - _position);
            source.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) {
            var position = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => source.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = position;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}