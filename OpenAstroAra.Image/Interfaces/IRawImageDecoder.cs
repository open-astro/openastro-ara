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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.Interfaces;

/// <summary>
/// Linear, demosaiced 16-bit camera-RAW planes. Plane methods return borrowed buffers;
/// callers must treat them as immutable. Copies would multiply RAW decode peak memory.
/// </summary>
public sealed class DecodedRawImage {
    private readonly ushort[] _red;
    private readonly ushort[] _green;
    private readonly ushort[] _blue;

    public DecodedRawImage(int width, int height, int sourceBitDepth,
            ushort[] red, ushort[] green, ushort[] blue,
            string decoderVersion, string debayerMethod,
            string? cameraMake = null, string? cameraModel = null,
            string? originalCfaPattern = null) {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(debayerMethod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceBitDepth <= 0 || sourceBitDepth > 64) {
            throw new ArgumentOutOfRangeException(nameof(sourceBitDepth));
        }
        var expected = checked((long)width * height);
        if (expected > int.MaxValue || red.LongLength != expected
            || green.LongLength != expected || blue.LongLength != expected) {
            throw new ArgumentException("Decoded RAW color-plane lengths must match width times height.");
        }
        Width = width;
        Height = height;
        SourceBitDepth = sourceBitDepth;
        _red = red;
        _green = green;
        _blue = blue;
        DecoderVersion = decoderVersion;
        DebayerMethod = debayerMethod;
        CameraMake = cameraMake;
        CameraModel = cameraModel;
        OriginalCfaPattern = originalCfaPattern;
    }

    public int Width { get; }
    public int Height { get; }
    public int SourceBitDepth { get; }
    public string DecoderVersion { get; }
    public string DebayerMethod { get; }
    public string? CameraMake { get; }
    public string? CameraModel { get; }
    public string? OriginalCfaPattern { get; }

    public ushort[] BorrowRedPlane() => _red;
    public ushort[] BorrowGreenPlane() => _green;
    public ushort[] BorrowBluePlane() => _blue;
}

/// <summary>Headless camera-RAW decoder used by capture and library paths.</summary>
public interface IRawImageDecoder {
    bool IsAvailable { get; }

    string? Version { get; }

    Task<DecodedRawImage> DecodeFileAsync(
        string path,
        ImageLoadLimits limits,
        CancellationToken cancellationToken);

    Task<DecodedRawImage> DecodeBufferAsync(
        ReadOnlyMemory<byte> source,
        ImageLoadLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>LibRaw is absent or outside the supported ABI range.</summary>
public sealed class RawDecoderUnavailableException : NotSupportedException {
    public RawDecoderUnavailableException() { }

    public RawDecoderUnavailableException(string message) : base(message) { }

    public RawDecoderUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>A camera-RAW source was malformed, unsupported, or failed native decode.</summary>
public sealed class RawImageDecodeException : IOException {
    public RawImageDecodeException() { }

    public RawImageDecodeException(string message) : base(message) { }

    public RawImageDecodeException(string message, Exception innerException)
        : base(message, innerException) { }

    public RawImageDecodeException(string message, int nativeErrorCode) : base(message) {
        NativeErrorCode = nativeErrorCode;
    }

    public int? NativeErrorCode { get; }
}