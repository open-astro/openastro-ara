#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.FileFormat.RAW;
using OpenAstroAra.Image.FileFormat.XISF;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System.Globalization;
using System.Text;

namespace OpenAstroAra.Server.Services;

public enum SourceImageFormat {
    Fits,
    Xisf,
    Raw,
}

/// <summary>Decoded one-plane source data. Pixels are normalized to unsigned 16-bit.</summary>
public sealed record SourceImageData(
    string Path,
    SourceImageFormat Format,
    IImageArray Data,
    int Width,
    int Height,
    int SourceBitDepth,
    string? CfaPattern,
    ImageMetaData MetaData,
    DecodedRawImage? ColorData = null);

public interface ISourceImageDataFactory {
    string DecoderCacheIdentity { get; }

    Task<SourceImageData> LoadAsync(string path, CancellationToken ct);

    IImageData CreateImageData(SourceImageData source, IProfileService? profileService = null);
}

/// <summary>Clear failure for a file whose signature is not a supported source-image format.</summary>
public sealed class UnsupportedSourceImageFormatException : NotSupportedException {
    public UnsupportedSourceImageFormatException() { }

    public UnsupportedSourceImageFormatException(string message) : base(message) { }

    public UnsupportedSourceImageFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Headless source factory used by previews, thumbnails, and plate solving.
/// Signatures are authoritative, extensions provide mismatch diagnostics, and
/// allocation limits are enforced before pixel decode.
/// </summary>
public sealed partial class SourceImageDataFactory : ISourceImageDataFactory, IImageDataFactory {
    private static readonly byte[] XisfSignature = "XISF0100"u8.ToArray();
    private static readonly byte[] FitsSignature = "SIMPLE  ="u8.ToArray();
    private static readonly IStarDetection NoOpStarDetection = new NoOpStarDetector();
    private static readonly IStarAnnotator PreviewStarAnnotator = new StarAnnotator();

    private readonly IProfileService _profileService;
    private readonly ImageLoadLimits _limits;
    private readonly ILogger<SourceImageDataFactory> _logger;
    private readonly IRawImageDecoder _rawDecoder;

    public SourceImageDataFactory(IProfileService profileService,
            ImageLoadLimits? limits = null, ILogger<SourceImageDataFactory>? logger = null,
            IRawImageDecoder? rawDecoder = null) {
        ArgumentNullException.ThrowIfNull(profileService);
        _profileService = profileService;
        _limits = limits ?? ImageLoadLimits.Default;
        _limits.Validate();
        _logger = logger ?? NullLogger<SourceImageDataFactory>.Instance;
        _rawDecoder = rawDecoder ?? new LibRawDecoder();
    }

    public string DecoderCacheIdentity => $"source-v3|libraw:{_rawDecoder.Version ?? "unavailable"}";

    public async Task<SourceImageData> LoadAsync(string path, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Source image does not exist.", fullPath);
        if (info.Length <= 0 || info.Length > _limits.MaxFileBytes) {
            throw new InvalidDataException(
                $"Source image size {info.Length} is outside the supported range 1-{_limits.MaxFileBytes} bytes.");
        }

        var format = await DetectFormatAsync(fullPath, ct).ConfigureAwait(false);
        LogLoadingSource(fullPath, format, info.Length);
        return format switch {
            SourceImageFormat.Fits => await Task.Run(() => LoadFits(fullPath, ct), ct).ConfigureAwait(false),
            SourceImageFormat.Xisf => await LoadXisfAsync(fullPath, ct).ConfigureAwait(false),
            SourceImageFormat.Raw => await LoadRawAsync(fullPath, ct).ConfigureAwait(false),
            _ => throw new UnsupportedSourceImageFormatException("Unsupported source-image format."),
        };
    }

    public IImageData CreateImageData(SourceImageData source, IProfileService? profileService = null) {
        ArgumentNullException.ThrowIfNull(source);
        return new BaseImageData(source.Data, source.Width, source.Height, bitDepth: 16,
            isBayered: source.CfaPattern is not null, source.MetaData,
            profileService ?? _profileService, NoOpStarDetection, PreviewStarAnnotator);
    }

    public BaseImageData CreateBaseImageData(ushort[] input, int width, int height,
            int bitDepth, bool isBayered, ImageMetaData metaData) =>
        new(input, width, height, bitDepth, isBayered, metaData,
            _profileService, NoOpStarDetection, PreviewStarAnnotator);

    public BaseImageData CreateBaseImageData(IImageArray imageArray, int width, int height,
            int bitDepth, bool isBayered, ImageMetaData metaData) =>
        new(imageArray, width, height, bitDepth, isBayered, metaData,
            _profileService, NoOpStarDetection, PreviewStarAnnotator);

    public async Task<IImageData> CreateFromFile(string path, int bitDepth, bool isBayered,
            RawConverter rawConverter, CancellationToken ct = default) {
        var source = await LoadAsync(path, ct).ConfigureAwait(false);
        if (isBayered && source.Format != SourceImageFormat.Raw && source.CfaPattern is null) {
            source = source with { CfaPattern = SensorType.RGGB.ToString() };
        }
        return CreateImageData(source);
    }

    private static async Task<SourceImageFormat> DetectFormatAsync(string path, CancellationToken ct) {
        var signature = new byte[32];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAtLeastAsync(signature, signature.Length,
            throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read >= XisfSignature.Length
            && signature.AsSpan(0, XisfSignature.Length).SequenceEqual(XisfSignature)) {
            return SourceImageFormat.Xisf;
        }
        if (read >= FitsSignature.Length
            && signature.AsSpan(0, FitsSignature.Length).SequenceEqual(FitsSignature)) {
            return SourceImageFormat.Fits;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (LooksLikeRawSignature(signature.AsSpan(0, read)) || LibRawDecoder.IsKnownFileExtension(extension)) {
            return SourceImageFormat.Raw;
        }
        if (extension is ".fit" or ".fits" or ".fts" or ".fz" or ".xisf") {
            throw new InvalidDataException(
                $"Source image extension '{extension}' does not match a valid FITS or XISF signature.");
        }
        throw new UnsupportedSourceImageFormatException(
            $"Source image format is unsupported (extension '{extension}'). Supported formats: FITS, XISF, camera RAW.");
    }

    private async Task<SourceImageData> LoadRawAsync(string path, CancellationToken ct) {
        var decoded = await _rawDecoder.DecodeFileAsync(path, _limits, ct).ConfigureAwait(false);
        ValidateGeometry("RAW", decoded.Width, decoded.Height);
        var red = decoded.BorrowRedPlane();
        var green = decoded.BorrowGreenPlane();
        var blue = decoded.BorrowBluePlane();
        var expected = checked(decoded.Width * decoded.Height);
        if (red.Length != expected || green.Length != expected || blue.Length != expected) {
            throw new InvalidDataException("RAW decoded color-plane lengths do not match its dimensions.");
        }
        var luminance = LibRawConverter.CreateLuminance(red, green, blue, ct);
        var metadata = new ImageMetaData {
            GenericHeaders = [
                new StringMetaDataHeader("RAWDECODER", $"LibRaw {decoded.DecoderVersion}"),
                new StringMetaDataHeader("DEBAYER", decoded.DebayerMethod),
                new IntMetaDataHeader("RAWSOURCEBITDEPTH", decoded.SourceBitDepth),
                .. LibRawConverter.RawMetadataHeaders(decoded),
            ],
        };
        var identity = string.Join(' ', new[] { decoded.CameraMake, decoded.CameraModel }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (identity.Length > 0) metadata.Camera.Name = identity;
        metadata.Camera.SensorType = SensorType.Color;
        return new SourceImageData(path, SourceImageFormat.Raw, new ImageArray(luminance),
            decoded.Width, decoded.Height, decoded.SourceBitDepth, null, metadata, decoded);
    }

    private static bool LooksLikeRawSignature(ReadOnlySpan<byte> signature) {
        if (signature.Length >= 4
            && ((signature[0] == (byte)'I' && signature[1] == (byte)'I'
                 && signature[2] is 0x2a or 0x55 && signature[3] == 0)
                || (signature[0] == (byte)'M' && signature[1] == (byte)'M'
                    && signature[2] == 0 && signature[3] == 0x2a))) {
            return true;
        }
        if (signature.StartsWith("IIRO"u8) || signature.StartsWith("MMOR"u8)
            || signature.StartsWith("FUJIFILMCCD-RAW "u8)) {
            return true;
        }
        return signature.Length >= 12
            && signature.Slice(4, 4).SequenceEqual("ftyp"u8)
            && (signature.Slice(8, 3).SequenceEqual("crx"u8)
                || signature.Slice(8, 3).SequenceEqual("cr3"u8));
    }

    private SourceImageData LoadFits(string path, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var primaryHeaders = ReadFitsPrimaryHeaders(path, ct);
        if (!primaryHeaders.TryGetValue("SIMPLE", out var simple)
            || !string.Equals(simple, "T", StringComparison.Ordinal)) {
            throw new InvalidDataException("FITS primary header must declare SIMPLE = T.");
        }
        var naxis = ParseRequiredFitsInteger(primaryHeaders, "NAXIS");
        if (naxis != 2) {
            throw new UnsupportedSourceImageFormatException(
                $"FITS image has {naxis} axes; only two-dimensional source images are supported.");
        }
        var expectedWidth = ParseRequiredFitsInteger(primaryHeaders, "NAXIS1");
        var expectedHeight = ParseRequiredFitsInteger(primaryHeaders, "NAXIS2");
        ValidateGeometry("FITS", expectedWidth, expectedHeight);
        var bitpix = ParseRequiredFitsInteger(primaryHeaders, "BITPIX");
        if (bitpix is not (8 or 16 or 32 or 64 or -32 or -64)) {
            throw new UnsupportedSourceImageFormatException(
                $"FITS BITPIX {bitpix} is unsupported. Supported values: 8, 16, 32, 64, -32, -64.");
        }

        using var fits = OpenAstroAra.Fits.FitsImage.Open(path);
        var (width, height) = fits.GetDimensions();
        if (width != expectedWidth || height != expectedHeight) {
            throw new InvalidDataException(
                $"FITS decoded dimensions {width}x{height} do not match primary header {expectedWidth}x{expectedHeight}.");
        }
        var headers = fits.ReadHeaders();
        ct.ThrowIfCancellationRequested();
        var pixels = bitpix switch {
            8 => ScaleBytePlane(fits.ReadImageData16()),
            16 => fits.ReadImageData16(),
            _ => NormalizeFloatPlane(fits.ReadImageDataFloat()),
        };
        if (pixels.LongLength != (long)width * height) {
            throw new InvalidDataException("FITS decoded pixel count does not match its dimensions.");
        }
        ct.ThrowIfCancellationRequested();

        var cfaPattern = NormalizeCfa(headers.TryGetValue("BAYERPAT", out var rawCfa) ? rawCfa : null);
        var metadata = new ImageMetaData {
            GenericHeaders = headers.Select(pair =>
                (IGenericMetaDataHeader)new StringMetaDataHeader(pair.Key, pair.Value)).ToArray(),
        };
        if (cfaPattern is not null) metadata.Camera.SensorType = ImageMetaData.StringToSensorType(cfaPattern);
        return new SourceImageData(path, SourceImageFormat.Fits, new ImageArray(pixels), width, height,
            Math.Abs(bitpix), cfaPattern, metadata);
    }

    private async Task<SourceImageData> LoadXisfAsync(string path, CancellationToken ct) {
        var image = await XISF.Load(new Uri(path), isBayered: false, this, _limits, ct).ConfigureAwait(false);
        ValidateGeometry("XISF", image.Properties.Width, image.Properties.Height);
        var pixels = image.Data.FlatArray;
        if (pixels.LongLength != (long)image.Properties.Width * image.Properties.Height) {
            throw new InvalidDataException("XISF decoded pixel count does not match its dimensions.");
        }
        ct.ThrowIfCancellationRequested();
        var genericCfa = image.MetaData.GenericHeaders
            .OfType<IGenericMetaDataHeader<string>>()
            .FirstOrDefault(header => string.Equals(header.Key, "BAYERPAT", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var cfaPattern = NormalizeCfa(genericCfa) ?? image.MetaData.Camera.SensorType switch {
            SensorType.RGGB or SensorType.BGGR or SensorType.GBRG or SensorType.GRBG =>
                image.MetaData.Camera.SensorType.ToString().ToUpperInvariant(),
            _ => null,
        };
        return new SourceImageData(path, SourceImageFormat.Xisf, image.Data,
            image.Properties.Width, image.Properties.Height, image.Properties.BitDepth,
            cfaPattern, image.MetaData);
    }

    private void ValidateGeometry(string format, int width, int height) {
        if (width <= 0 || height <= 0 || width > _limits.MaxDimension || height > _limits.MaxDimension) {
            throw new InvalidDataException(
                $"{format} dimensions {width}x{height} exceed the configured dimension limit {_limits.MaxDimension}.");
        }
        var pixels = (long)width * height;
        if (pixels > _limits.MaxPixelCount) {
            throw new InvalidDataException(
                $"{format} pixel count {pixels} exceeds the configured limit {_limits.MaxPixelCount}.");
        }
    }

    private static int ParseRequiredFitsInteger(Dictionary<string, string> headers, string key) {
        if (!headers.TryGetValue(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
            throw new InvalidDataException($"FITS header is missing a valid {key} value.");
        }
        return value;
    }

    private Dictionary<string, string> ReadFitsPrimaryHeaders(string path, CancellationToken ct) {
        const int blockSize = 2880;
        const int cardSize = 80;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: blockSize, FileOptions.SequentialScan);
        var bytesRead = 0L;
        var block = new byte[blockSize];
        while (bytesRead + blockSize <= _limits.MaxHeaderBytes) {
            ct.ThrowIfCancellationRequested();
            if (stream.Length - stream.Position < blockSize) {
                throw new InvalidDataException("FITS primary header is truncated before its END card.");
            }
            stream.ReadExactly(block);
            bytesRead += blockSize;
            for (var offset = 0; offset < block.Length; offset += cardSize) {
                var card = block.AsSpan(offset, cardSize);
                var key = Encoding.ASCII.GetString(card[..8]).Trim();
                if (string.Equals(key, "END", StringComparison.Ordinal)) return headers;
                if (key.Length == 0 || card[8] != (byte)'=') continue;
                var rawValue = Encoding.ASCII.GetString(card[10..]);
                var comment = rawValue.IndexOf('/', StringComparison.Ordinal);
                headers[key] = (comment >= 0 ? rawValue[..comment] : rawValue).Trim().Trim('\'');
            }
        }
        throw new InvalidDataException(
            $"FITS primary header exceeds the configured limit {_limits.MaxHeaderBytes} bytes.");
    }

    private static string? NormalizeCfa(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Trim('\'').ToUpperInvariant();
        return normalized switch {
            "RGGB" or "BGGR" or "GBRG" or "GRBG" => normalized,
            _ => throw new InvalidDataException(
                $"Source image CFA pattern '{normalized}' is unsupported. Supported patterns: RGGB, BGGR, GBRG, GRBG."),
        };
    }

    private static ushort[] ScaleBytePlane(ushort[] source) {
        for (var index = 0; index < source.Length; index++) {
            source[index] = (ushort)Math.Min(ushort.MaxValue, source[index] * 257);
        }
        return source;
    }

    private static ushort[] NormalizeFloatPlane(float[] source) {
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        foreach (var value in source) {
            if (!float.IsFinite(value)) continue;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum)) {
            throw new InvalidDataException("FITS floating-point image contains no finite pixels.");
        }

        var output = new ushort[source.Length];
        if (maximum <= minimum) return output;
        var scale = ushort.MaxValue / (double)(maximum - minimum);
        for (var index = 0; index < source.Length; index++) {
            var value = source[index];
            if (!float.IsFinite(value)) continue;
            output[index] = (ushort)Math.Clamp(
                (int)Math.Round((value - minimum) * scale, MidpointRounding.AwayFromZero),
                ushort.MinValue, ushort.MaxValue);
        }
        return output;
    }

    private sealed class NoOpStarDetector : IStarDetection {
        public Task<StarDetectionResult> Detect(IRenderedImage image, string format,
                StarDetectionParams parameters, IProgress<ApplicationStatus> progress, CancellationToken token) =>
            Task.FromResult(new StarDetectionResult());
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Loading source image {Path} as {Format} ({Bytes} bytes)")]
    private partial void LogLoadingSource(string path, SourceImageFormat format, long bytes);
}