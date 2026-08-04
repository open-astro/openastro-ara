#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.Interfaces;

public enum RasterImageFormat {
    Tiff,
    Jpeg,
    Png,
}

/// <summary>Bounded raster decode normalized to an unsigned 16-bit luminance or luma plane.</summary>
public sealed class DecodedRasterImage {
    public DecodedRasterImage(RasterImageFormat format, int width, int height,
            int sourceBitDepth, ushort[] luminance, DecodedColorImage? colorData,
            string decoderVersion, IReadOnlyDictionary<string, string>? metadata = null) {
        ArgumentNullException.ThrowIfNull(luminance);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceBitDepth <= 0 || sourceBitDepth > 64) {
            throw new ArgumentOutOfRangeException(nameof(sourceBitDepth));
        }
        var expected = checked((long)width * height);
        if (expected > int.MaxValue || luminance.LongLength != expected) {
            throw new ArgumentException("Decoded luminance length must match width times height.",
                nameof(luminance));
        }
        if (colorData is not null
            && (colorData.Width != width || colorData.Height != height)) {
            throw new ArgumentException("Decoded color dimensions must match luminance dimensions.",
                nameof(colorData));
        }

        Format = format;
        Width = width;
        Height = height;
        SourceBitDepth = sourceBitDepth;
        _luminance = luminance;
        ColorData = colorData;
        DecoderVersion = decoderVersion;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public RasterImageFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int SourceBitDepth { get; }
    public DecodedColorImage? ColorData { get; }
    public string DecoderVersion { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public bool IsPreviewOnly => Format is RasterImageFormat.Jpeg or RasterImageFormat.Png;

    private readonly ushort[] _luminance;

    public ushort[] BorrowLuminancePlane() => _luminance;
}

public interface IRasterImageDecoder {
    string Version { get; }

    Task<DecodedRasterImage> DecodeFileAsync(
        string path,
        RasterImageFormat format,
        ImageLoadLimits limits,
        CancellationToken cancellationToken);

    Task<DecodedRasterImage> DecodeBufferAsync(
        ReadOnlyMemory<byte> source,
        RasterImageFormat? expectedFormat,
        ImageLoadLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>A raster source was malformed, truncated, unsupported, or failed decode.</summary>
public sealed class RasterImageDecodeException : IOException {
    public RasterImageDecodeException() { }

    public RasterImageDecodeException(string message) : base(message) { }

    public RasterImageDecodeException(string message, Exception innerException)
        : base(message, innerException) { }
}