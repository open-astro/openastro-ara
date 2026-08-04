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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Stretch;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Bounded, source-preserving preview renderer. Cache entries live under the profile directory,
/// never beside source frames. Keys include source checksum/fingerprint and every render option.
/// </summary>
public sealed partial class PreviewImageService : IPreviewImageService, IDisposable {
    private const int CacheSchemaVersion = 2;
    private const string JpegContentType = "image/jpeg";

    private readonly string _cacheRoot;
    private readonly ISourceImageDataFactory _sourceImages;
    private readonly IStarAnnotator _starAnnotator;
    private readonly PreviewCacheOptions _options;
    private readonly ILogger<PreviewImageService> _logger;
    private readonly ConcurrentDictionary<string, SourceFingerprint> _sourceHashes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evictionGate = new(1, 1);

    public PreviewImageService(IAraDatabase database, ISourceImageDataFactory sourceImages,
            IStarAnnotator starAnnotator,
            ILogger<PreviewImageService>? logger = null)
        : this(Path.Combine(Path.GetDirectoryName(database.DatabasePath)
            ?? throw new ArgumentException("Database path has no parent directory.", nameof(database)),
            "preview-cache"), sourceImages, new PreviewCacheOptions(), logger, starAnnotator) { }

    public PreviewImageService(string cacheRoot, ISourceImageDataFactory sourceImages,
            PreviewCacheOptions? options = null, ILogger<PreviewImageService>? logger = null,
            IStarAnnotator? starAnnotator = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(sourceImages);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _sourceImages = sourceImages;
        _starAnnotator = starAnnotator ?? new StarAnnotator();
        _options = options ?? new PreviewCacheOptions();
        _options.Validate();
        _logger = logger ?? NullLogger<PreviewImageService>.Instance;
    }

    public async Task<FramePreviewResult> RenderAsync(PreviewRenderRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ct.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(request.SourcePath);
        var fingerprint = await ResolveFingerprintAsync(sourcePath,
            request.SourceChecksumSha256, ct).ConfigureAwait(false);
        var cacheKey = ComputeCacheKey(request, fingerprint);
        var (jpegPath, metadataPath) = GetCachePaths(request.FrameId, fingerprint.ChecksumSha256, cacheKey);
        var keyLock = _keyLocks.GetOrAdd(jpegPath, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            var cached = await TryReadAsync(jpegPath, metadataPath, cacheKey,
                fingerprint.ChecksumSha256, ct).ConfigureAwait(false);
            if (cached is not null) return cached.Value;

            var source = await _sourceImages.LoadAsync(sourcePath, ct).ConfigureAwait(false);
            var afterLoad = ReadFileIdentity(sourcePath);
            if (afterLoad.Length != fingerprint.Length
                || afterLoad.LastWriteUtcTicks != fingerprint.LastWriteUtcTicks) {
                _sourceHashes.TryRemove(sourcePath, out _);
                throw new IOException("Source image changed while its preview was being rendered; retry the request.");
            }

            var rendered = await RenderAsync(source, request, ct).ConfigureAwait(false);
            var metadata = new PreviewCacheMetadata(
                SchemaVersion: CacheSchemaVersion,
                FrameId: request.FrameId,
                SourceChecksumSha256: fingerprint.ChecksumSha256,
                CacheKey: cacheKey,
                Width: rendered.Width,
                Height: rendered.Height,
                Algorithm: AlgorithmToWire(request.Algorithm),
                AppliedParameters: rendered.AppliedParameters,
                DebayerMode: rendered.DebayerMode,
                ChannelMode: rendered.ChannelMode,
                Inverted: request.Invert,
                Saturation: request.Saturation,
                CreatedUtc: DateTimeOffset.UtcNow,
                Annotated: request.AnnotateStars,
                AnnotationCount: rendered.AnnotationCount,
                RejectedAnnotationCount: rendered.RejectedAnnotationCount,
                AnnotationColor: request.AnnotateStars
                    ? ColorToWire(request.AnnotationOptions ?? new StarAnnotationOptions())
                    : null,
                AnnotationLabels: request.AnnotateStars
                    && (request.AnnotationOptions?.ShowLabels ?? false));
            var result = new FramePreviewResult(rendered.Bytes, JpegContentType, metadata, CacheHit: false);

            await TryWriteAsync(jpegPath, metadataPath, result, ct).ConfigureAwait(false);
            await EnforceBoundsAsync(request.FrameId, ct).ConfigureAwait(false);
            return result;
        } finally {
            keyLock.Release();
            _keyLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(jpegPath, keyLock));
        }
    }

    public async Task DeleteFrameEntriesAsync(Guid frameId, CancellationToken ct) {
        if (frameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        var frameRoot = Path.Combine(_cacheRoot, frameId.ToString("N"));
        if (!Directory.Exists(frameRoot)) return;

        await _evictionGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            foreach (var file in Directory.EnumerateFiles(frameRoot, "*", SearchOption.AllDirectories)) {
                ct.ThrowIfCancellationRequested();
                TryDelete(file);
            }
            foreach (var directory in Directory.EnumerateDirectories(frameRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length)) {
                TryDeleteEmptyDirectory(directory);
            }
            TryDeleteEmptyDirectory(frameRoot);
        } catch (DirectoryNotFoundException) {
            // Concurrent eviction already removed it.
        } catch (UnauthorizedAccessException ex) {
            LogCacheFailure(ex, frameRoot);
        } finally {
            _evictionGate.Release();
        }
    }

    private void ValidateRequest(PreviewRenderRequest request) {
        if (request.FrameId == Guid.Empty) throw new ArgumentException("Frame id must not be empty.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.MaxDimension <= 0 || request.MaxDimension > _options.MaxDimension) {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxDimension,
                $"Preview maximum dimension must be between 1 and {_options.MaxDimension} pixels.");
        }
        if (!double.IsFinite(request.Saturation) || request.Saturation is < 0 or > 2) {
            throw new ArgumentOutOfRangeException(nameof(request), request.Saturation,
                "Preview saturation must be between 0 and 2.");
        }
        if (!double.IsFinite(request.StarSensitivity)
            || request.StarSensitivity is < 0.5 or > 50) {
            throw new ArgumentOutOfRangeException(nameof(request), request.StarSensitivity,
                "Star sensitivity threshold must be between 0.5 and 50 sigma.");
        }
        if (request.StarNoiseReduction is < 0 or > 3) {
            throw new ArgumentOutOfRangeException(nameof(request), request.StarNoiseReduction,
                "Star noise reduction must be between 0 and 3.");
        }
        if (request.AnnotateStars) ValidateAnnotationOptions(
            request.AnnotationOptions ?? new StarAnnotationOptions());
        var cropCount = new[] { request.CropX, request.CropY, request.CropWidth, request.CropHeight }
            .Count(static value => value.HasValue);
        if (cropCount is not (0 or 4)) {
            throw new ArgumentException("CropX, CropY, CropWidth, and CropHeight must be supplied together.",
                nameof(request));
        }
        if (cropCount == 4 && (request.CropX < 0 || request.CropY < 0
            || request.CropWidth <= 0 || request.CropHeight <= 0)) {
            throw new ArgumentException("Preview crop origin must be non-negative and dimensions must be positive.",
                nameof(request));
        }
    }

    private static void ValidateAnnotationOptions(StarAnnotationOptions options) {
        if (!float.IsFinite(options.StrokeWidth) || options.StrokeWidth <= 0 || options.StrokeWidth > 32) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation stroke width must be in (0, 32].");
        }
        if (!float.IsFinite(options.MinimumOutputRadius)
            || options.MinimumOutputRadius <= 0 || options.MinimumOutputRadius > 128) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation minimum output radius must be in (0, 128].");
        }
        if (!float.IsFinite(options.FontSize) || options.FontSize <= 0 || options.FontSize > 128) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation font size must be in (0, 128].");
        }
        if (options.FontFamily?.Length > 128) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation font family must not exceed 128 characters.");
        }
        if (options.MaxAnnotations <= 0 || options.MaxAnnotations > 10000) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Maximum annotations must be between 1 and 10000.");
        }
        if (!double.IsFinite(options.RadiusScale) || options.RadiusScale <= 0 || options.RadiusScale > 100) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation radius scale must be in (0, 100].");
        }
        if (!double.IsFinite(options.MinimumSourceRadius)
            || options.MinimumSourceRadius <= 0 || options.MinimumSourceRadius > 1000) {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Annotation minimum source radius must be in (0, 1000].");
        }
    }

    private async Task<RenderedPreview> RenderAsync(
            SourceImageData source, PreviewRenderRequest request, CancellationToken ct) {
        var pixels = source.Data.FlatArray;
        var width = source.Width;
        var height = source.Height;
        var cfaPattern = source.CfaPattern;
        if (request.CropX.HasValue) {
            var cropX = request.CropX.Value;
            var cropY = request.CropY!.Value;
            var cropWidth = request.CropWidth!.Value;
            var cropHeight = request.CropHeight!.Value;
            if ((long)cropX + cropWidth > width || (long)cropY + cropHeight > height) {
                throw new ArgumentException(
                    $"Preview crop {cropX},{cropY} {cropWidth}x{cropHeight} exceeds source dimensions {width}x{height}.",
                    nameof(request));
            }
            pixels = Crop(pixels, width, cropX, cropY, cropWidth, cropHeight);
            width = cropWidth;
            height = cropHeight;
            if (Debayer.TryParse(cfaPattern, out var originalPattern)) {
                cfaPattern = ShiftPattern(originalPattern, cropX, cropY).ToString();
            }
        }

        if (request.ApplyDebayer && Debayer.TryParse(cfaPattern, out var pattern)) {
            return await RenderDebayeredAsync(pixels, width, height, pattern, request, ct)
                .ConfigureAwait(false);
        }
        if (request.ChannelMode is PreviewChannelMode.Red or PreviewChannelMode.Green or PreviewChannelMode.Blue) {
            throw new ArgumentException("Red, green, and blue channel modes require debayering a CFA source.",
                nameof(request));
        }

        var detailed = Stretcher.ApplyDetailed(request.Algorithm, pixels, request.Parameters);
        if (request.Invert) Invert(detailed.Pixels);
        var annotation = await EncodeAsync(detailed.Pixels, IsColor: false, pixels,
            width, height, request, ct).ConfigureAwait(false);
        var (outputWidth, outputHeight) = ScaleToFit(width, height, request.MaxDimension);
        return new RenderedPreview(annotation.Bytes, outputWidth, outputHeight, detailed.AppliedParameters,
            DebayerMode: "none", ChannelMode: source.CfaPattern is null ? "luminance" : "raw_cfa",
            annotation.AnnotationCount, annotation.RejectedCount);
    }

    private async Task<RenderedPreview> RenderDebayeredAsync(ushort[] pixels, int width, int height,
            BayerPattern pattern, PreviewRenderRequest request, CancellationToken ct) {
        var (red, green, blue, outputWidth, outputHeight) = Debayer.SuperPixel(pixels, width, height, pattern);
        var luminance = Luminance(red, green, blue);
        if (request.ChannelMode == PreviewChannelMode.Rgb) {
            var applied = Stretcher.ResolveParameters(request.Algorithm, luminance, request.Parameters);
            var redDisplay = Stretcher.ApplyResolved(request.Algorithm, red, applied);
            var greenDisplay = Stretcher.ApplyResolved(request.Algorithm, green, applied);
            var blueDisplay = Stretcher.ApplyResolved(request.Algorithm, blue, applied);
            var rgb = Interleave(redDisplay, greenDisplay, blueDisplay);
            ApplySaturation(rgb, request.Saturation);
            if (request.Invert) Invert(rgb);
            var annotation = await EncodeAsync(rgb, IsColor: true, luminance,
                outputWidth, outputHeight, request, ct).ConfigureAwait(false);
            var scaled = ScaleToFit(outputWidth, outputHeight, request.MaxDimension);
            return new RenderedPreview(annotation.Bytes, scaled.Width, scaled.Height, applied,
                DebayerMode: "super_pixel", ChannelMode: "rgb",
                annotation.AnnotationCount, annotation.RejectedCount);
        }

        var plane = request.ChannelMode switch {
            PreviewChannelMode.Red => red,
            PreviewChannelMode.Green => green,
            PreviewChannelMode.Blue => blue,
            _ => luminance,
        };
        var detailed = Stretcher.ApplyDetailed(request.Algorithm, plane, request.Parameters);
        if (request.Invert) Invert(detailed.Pixels);
        var annotationResult = await EncodeAsync(detailed.Pixels, IsColor: false, plane,
            outputWidth, outputHeight, request, ct).ConfigureAwait(false);
        var dimensions = ScaleToFit(outputWidth, outputHeight, request.MaxDimension);
        return new RenderedPreview(annotationResult.Bytes, dimensions.Width, dimensions.Height,
            detailed.AppliedParameters, DebayerMode: "super_pixel",
            ChannelMode: request.ChannelMode.ToString().ToLowerInvariant(),
            annotationResult.AnnotationCount, annotationResult.RejectedCount);
    }

    private async Task<AnnotationEncodingResult> EncodeAsync(byte[] displayPixels, bool IsColor,
            ushort[] detectionPlane, int width, int height, PreviewRenderRequest request,
            CancellationToken ct) {
        if (!request.AnnotateStars) {
            var bytes = IsColor
                ? JpegEncoder.EncodeColor(displayPixels, width, height, maxDim: request.MaxDimension)
                : JpegEncoder.EncodeGray(displayPixels, width, height, maxDim: request.MaxDimension);
            return new AnnotationEncodingResult(bytes, 0, 0);
        }

        var options = request.AnnotationOptions ?? new StarAnnotationOptions();
        var detection = StarDetector.Detect(detectionPlane, width, height,
            new StarDetectionParams {
                Sensitivity = request.StarSensitivity,
                NoiseReduction = request.StarNoiseReduction,
                IsAutoFocus = false,
                MaxNumberOfStars = 0,
            }, ct);
        var result = await _starAnnotator.AnnotateAsync(new StarAnnotationRequest(
            displayPixels, width, height, IsColor, request.MaxDimension, detection, options), ct)
            .ConfigureAwait(false);
        return new AnnotationEncodingResult(result.Image, result.AnnotationCount, result.RejectedCount);
    }

    private async Task<SourceFingerprint> ResolveFingerprintAsync(string sourcePath,
            string? checksumHint, CancellationToken ct) {
        var identity = ReadFileIdentity(sourcePath);
        if (_sourceHashes.TryGetValue(sourcePath, out var cached)
            && cached.Length == identity.Length
            && cached.LastWriteUtcTicks == identity.LastWriteUtcTicks) {
            return cached;
        }

        string checksum;
        if (IsSha256(checksumHint)) {
            checksum = checksumHint!.ToLowerInvariant();
        } else {
            await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
                .ToLowerInvariant();
        }
        var fingerprint = new SourceFingerprint(identity.Length, identity.LastWriteUtcTicks, checksum);
        _sourceHashes[sourcePath] = fingerprint;
        return fingerprint;
    }

    private static FileIdentity ReadFileIdentity(string sourcePath) {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Preview source image does not exist.", sourcePath);
        return new FileIdentity(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string ComputeCacheKey(PreviewRenderRequest request, SourceFingerprint fingerprint) {
        var p = request.Parameters;
        var canonical = string.Join('|',
            CacheSchemaVersion.ToString(CultureInfo.InvariantCulture),
            fingerprint.ChecksumSha256,
            fingerprint.Length.ToString(CultureInfo.InvariantCulture),
            fingerprint.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture),
            AlgorithmToWire(request.Algorithm),
            p.Blackpoint.ToString("R", CultureInfo.InvariantCulture),
            p.Midpoint.ToString("R", CultureInfo.InvariantCulture),
            p.Whitepoint.ToString("R", CultureInfo.InvariantCulture),
            p.Beta.ToString("R", CultureInfo.InvariantCulture),
            p.LinearClipLow.ToString("R", CultureInfo.InvariantCulture),
            p.LinearClipHigh.ToString("R", CultureInfo.InvariantCulture),
            request.MaxDimension.ToString(CultureInfo.InvariantCulture),
            request.ApplyDebayer ? "1" : "0",
            request.ChannelMode.ToString(),
            request.Invert ? "1" : "0",
            request.Saturation.ToString("R", CultureInfo.InvariantCulture),
            request.CropX?.ToString(CultureInfo.InvariantCulture) ?? "-",
            request.CropY?.ToString(CultureInfo.InvariantCulture) ?? "-",
            request.CropWidth?.ToString(CultureInfo.InvariantCulture) ?? "-",
            request.CropHeight?.ToString(CultureInfo.InvariantCulture) ?? "-",
            request.AnnotateStars ? "1" : "0",
            ColorToWire(request.AnnotationOptions ?? new StarAnnotationOptions()),
            (request.AnnotationOptions?.StrokeWidth ?? 2f).ToString("R", CultureInfo.InvariantCulture),
            (request.AnnotationOptions?.MinimumOutputRadius ?? 6f).ToString("R", CultureInfo.InvariantCulture),
            (request.AnnotationOptions?.FontSize ?? 12f).ToString("R", CultureInfo.InvariantCulture),
            request.AnnotationOptions?.FontFamily ?? "-",
            request.AnnotationOptions?.ShowLabels == true ? "1" : "0",
            (request.AnnotationOptions?.MaxAnnotations ?? 250).ToString(CultureInfo.InvariantCulture),
            (request.AnnotationOptions?.RadiusScale ?? 3.0).ToString("R", CultureInfo.InvariantCulture),
            (request.AnnotationOptions?.MinimumSourceRadius ?? 5.0).ToString("R", CultureInfo.InvariantCulture),
            request.StarSensitivity.ToString("R", CultureInfo.InvariantCulture),
            request.StarNoiseReduction.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private (string JpegPath, string MetadataPath) GetCachePaths(Guid frameId,
            string sourceChecksum, string cacheKey) {
        var directory = Path.Combine(_cacheRoot, frameId.ToString("N"), sourceChecksum);
        return (Path.Combine(directory, cacheKey + ".jpg"),
            Path.Combine(directory, cacheKey + ".json"));
    }

    private static async Task<FramePreviewResult?> TryReadAsync(string jpegPath, string metadataPath,
            string expectedCacheKey, string expectedChecksum, CancellationToken ct) {
        if (!File.Exists(jpegPath) || !File.Exists(metadataPath)) return null;
        try {
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath, ct).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize(metadataBytes,
                AraJsonSerializerContext.Default.PreviewCacheMetadata);
            if (metadata is null || metadata.SchemaVersion != CacheSchemaVersion
                || !string.Equals(metadata.CacheKey, expectedCacheKey, StringComparison.Ordinal)
                || !string.Equals(metadata.SourceChecksumSha256, expectedChecksum, StringComparison.Ordinal)) {
                TryDelete(jpegPath);
                TryDelete(metadataPath);
                return null;
            }
            var jpeg = await File.ReadAllBytesAsync(jpegPath, ct).ConfigureAwait(false);
            if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9) {
                TryDelete(jpegPath);
                TryDelete(metadataPath);
                return null;
            }
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(jpegPath, now);
            File.SetLastWriteTimeUtc(metadataPath, now);
            return new FramePreviewResult(jpeg, JpegContentType, metadata, CacheHit: true);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        } catch (JsonException) {
            TryDelete(jpegPath);
            TryDelete(metadataPath);
            return null;
        }
    }

    private async Task TryWriteAsync(string jpegPath, string metadataPath,
            FramePreviewResult result, CancellationToken ct) {
        var directory = Path.GetDirectoryName(jpegPath)!;
        var unique = Guid.NewGuid().ToString("N");
        var jpegTemp = jpegPath + "." + unique + ".tmp";
        var metadataTemp = metadataPath + "." + unique + ".tmp";
        try {
            Directory.CreateDirectory(directory);
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(result.Metadata,
                AraJsonSerializerContext.Default.PreviewCacheMetadata);
            await File.WriteAllBytesAsync(jpegTemp, result.Bytes, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(metadataTemp, metadataBytes, ct).ConfigureAwait(false);
            File.Move(jpegTemp, jpegPath, overwrite: true);
            File.Move(metadataTemp, metadataPath, overwrite: true);
        } catch (IOException ex) {
            LogCacheFailure(ex, jpegPath);
        } catch (UnauthorizedAccessException ex) {
            LogCacheFailure(ex, jpegPath);
        } finally {
            TryDelete(jpegTemp);
            TryDelete(metadataTemp);
        }
    }

    private async Task EnforceBoundsAsync(Guid frameId, CancellationToken ct) {
        await _evictionGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            var frameRoot = Path.Combine(_cacheRoot, frameId.ToString("N"));
            EvictOldest(EnumerateEntries(frameRoot), _options.MaxVariantsPerFrame, long.MaxValue);
            EvictOldest(EnumerateEntries(_cacheRoot), _options.MaxEntries, _options.MaxBytes);
        } finally {
            _evictionGate.Release();
        }
    }

    private static CacheEntry[] EnumerateEntries(string root) {
        if (!Directory.Exists(root)) return Array.Empty<CacheEntry>();
        try {
            return Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories)
                .Select(static jpeg => {
                    var metadata = Path.ChangeExtension(jpeg, ".json");
                    var jpegInfo = new FileInfo(jpeg);
                    var metadataLength = File.Exists(metadata) ? new FileInfo(metadata).Length : 0;
                    return new CacheEntry(jpeg, metadata, jpegInfo.Length + metadataLength,
                        jpegInfo.LastWriteTimeUtc);
                })
                .OrderByDescending(static entry => entry.LastUsedUtc)
                .ToArray();
        } catch (DirectoryNotFoundException) {
            return Array.Empty<CacheEntry>();
        } catch (UnauthorizedAccessException) {
            return Array.Empty<CacheEntry>();
        }
    }

    private static void EvictOldest(IReadOnlyList<CacheEntry> newestFirst, int maxEntries, long maxBytes) {
        var totalBytes = newestFirst.Sum(static entry => entry.SizeBytes);
        for (var index = newestFirst.Count - 1;
             index >= 0 && (index + 1 > maxEntries || totalBytes > maxBytes);
             index--) {
            var entry = newestFirst[index];
            TryDelete(entry.JpegPath);
            TryDelete(entry.MetadataPath);
            totalBytes -= entry.SizeBytes;
        }
    }

    private static ushort[] Crop(ushort[] pixels, int sourceWidth,
            int x, int y, int width, int height) {
        var output = new ushort[checked(width * height)];
        for (var row = 0; row < height; row++) {
            Array.Copy(pixels, checked((y + row) * sourceWidth + x),
                output, row * width, width);
        }
        return output;
    }

    private static BayerPattern ShiftPattern(BayerPattern pattern, int x, int y) {
        var source = pattern.ToString();
        Span<char> shifted = stackalloc char[4];
        for (var row = 0; row < 2; row++) {
            for (var column = 0; column < 2; column++) {
                shifted[row * 2 + column] = source[((row + y) & 1) * 2 + ((column + x) & 1)];
            }
        }
        return Enum.Parse<BayerPattern>(shifted);
    }

    private static ushort[] Luminance(ushort[] red, ushort[] green, ushort[] blue) {
        var output = new ushort[red.Length];
        for (var index = 0; index < output.Length; index++) {
            output[index] = (ushort)((red[index] + 2 * green[index] + blue[index] + 2) / 4);
        }
        return output;
    }

    private static byte[] Interleave(byte[] red, byte[] green, byte[] blue) {
        var output = new byte[checked(red.Length * 3)];
        for (var index = 0; index < red.Length; index++) {
            var offset = index * 3;
            output[offset] = red[index];
            output[offset + 1] = green[index];
            output[offset + 2] = blue[index];
        }
        return output;
    }

    private static void ApplySaturation(byte[] rgb, double saturation) {
        if (Math.Abs(saturation - 1) < double.Epsilon) return;
        for (var offset = 0; offset < rgb.Length; offset += 3) {
            var luminance = (rgb[offset] + 2 * rgb[offset + 1] + rgb[offset + 2]) / 4.0;
            rgb[offset] = ToByte(luminance + (rgb[offset] - luminance) * saturation);
            rgb[offset + 1] = ToByte(luminance + (rgb[offset + 1] - luminance) * saturation);
            rgb[offset + 2] = ToByte(luminance + (rgb[offset + 2] - luminance) * saturation);
        }
    }

    private static void Invert(byte[] pixels) {
        for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)(255 - pixels[index]);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(
        (int)Math.Round(value, MidpointRounding.AwayFromZero), byte.MinValue, byte.MaxValue);

    private static (int Width, int Height) ScaleToFit(int width, int height, int maxDimension) {
        if (width <= maxDimension && height <= maxDimension) return (width, height);
        var scale = (double)maxDimension / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string ColorToWire(StarAnnotationOptions options) =>
        $"#{options.Red:x2}{options.Green:x2}{options.Blue:x2}";

    private static string AlgorithmToWire(StretchAlgorithm algorithm) => algorithm switch {
        StretchAlgorithm.AutoStf => "auto_stf",
        StretchAlgorithm.Linear => "linear",
        StretchAlgorithm.Log => "log",
        StretchAlgorithm.Asinh => "asinh",
        StretchAlgorithm.Sqrt => "sqrt",
        StretchAlgorithm.Equalized => "equalized",
        StretchAlgorithm.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
    };

    private static void TryDelete(string path) {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDirectory(string path) {
        try { Directory.Delete(path, recursive: false); } catch (DirectoryNotFoundException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() {
        _evictionGate.Dispose();
        foreach (var keyLock in _keyLocks.Values) keyLock.Dispose();
        _keyLocks.Clear();
    }

    private sealed record FileIdentity(long Length, long LastWriteUtcTicks);

    private sealed record SourceFingerprint(long Length, long LastWriteUtcTicks, string ChecksumSha256);

    private sealed record CacheEntry(string JpegPath, string MetadataPath, long SizeBytes, DateTime LastUsedUtc);

    private sealed record RenderedPreview(byte[] Bytes, int Width, int Height,
        StretchParams AppliedParameters, string DebayerMode, string ChannelMode,
        int AnnotationCount, int RejectedAnnotationCount);

    private readonly record struct AnnotationEncodingResult(
        byte[] Bytes, int AnnotationCount, int RejectedCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Preview cache operation failed for {Path}")]
    private partial void LogCacheFailure(Exception exception, string path);
}