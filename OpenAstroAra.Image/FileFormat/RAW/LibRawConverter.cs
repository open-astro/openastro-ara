#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.FileFormat.RAW;

/// <summary>Adapts LibRaw to the inherited camera-exposure conversion contract.</summary>
public sealed class LibRawConverter : IRawConverter {
    private readonly IRawImageDecoder _decoder;
    private readonly IImageDataFactory _imageDataFactory;
    private readonly ImageLoadLimits _limits;

    public LibRawConverter(IImageDataFactory imageDataFactory,
            IRawImageDecoder? decoder = null, ImageLoadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(imageDataFactory);
        _imageDataFactory = imageDataFactory;
        _decoder = decoder ?? new LibRawDecoder();
        _limits = limits ?? ImageLoadLimits.Default;
        _limits.Validate();
    }

    public async Task<IImageData> Convert(MemoryStream s, int bitDepth, string rawType,
            ImageMetaData metaData, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(metaData);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawType);
        token.ThrowIfCancellationRequested();
        var source = await ReadBoundedAsync(s, token).ConfigureAwait(false);
        var decoded = await _decoder.DecodeBufferAsync(source, _limits, token).ConfigureAwait(false);
        var luminance = CreateLuminance(decoded.BorrowRedPlane(), decoded.BorrowGreenPlane(),
            decoded.BorrowBluePlane(), token);
        metaData.Camera.SensorType = OpenAstroAra.Core.Enums.SensorType.Color;
        metaData.GenericHeaders =
        [
            .. metaData.GenericHeaders,
            .. new IGenericMetaDataHeader[] {
                new StringMetaDataHeader("RAWDECODER", $"LibRaw {decoded.DecoderVersion}"),
                new StringMetaDataHeader("DEBAYER", decoded.DebayerMethod),
                new IntMetaDataHeader("RAWSOURCEBITDEPTH", decoded.SourceBitDepth),
            },
            .. RawMetadataHeaders(decoded),
        ];
        var originalBytes = MemoryMarshal.TryGetArray(source, out var segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array
                : source.ToArray();
        return _imageDataFactory.CreateBaseImageData(
            new ImageArray(luminance, originalBytes, NormalizeRawType(rawType)),
            decoded.Width, decoded.Height, bitDepth: 16,
            isBayered: false, metaData);
    }

    private async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream source,
            CancellationToken cancellationToken) {
        var maxManagedBytes = Math.Min(_limits.MaxFileBytes, int.MaxValue);
        if (source.CanSeek) {
            var remaining = source.Length - source.Position;
            if (remaining <= 0 || remaining > maxManagedBytes) {
                throw new InvalidDataException(
                    $"RAW source size {remaining} is outside the supported managed-buffer range 1-{maxManagedBytes} bytes.");
            }
            if (source is MemoryStream memory && memory.TryGetBuffer(out var segment)) {
                return segment.AsMemory(checked((int)source.Position), checked((int)remaining));
            }
        }

        using var destination = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true) {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maxManagedBytes) {
                throw new InvalidDataException(
                    $"RAW source exceeds the supported managed-buffer limit {maxManagedBytes} bytes.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (destination.Length == 0) throw new InvalidDataException("RAW source is empty.");
        return destination.ToArray();
    }

    public static ushort[] CreateLuminance(ushort[] red, ushort[] green, ushort[] blue,
            CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (red.Length != green.Length || red.Length != blue.Length) {
            throw new InvalidDataException("RAW color planes have mismatched lengths.");
        }
        var luminance = new ushort[red.Length];
        for (var index = 0; index < luminance.Length; index++) {
            if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            luminance[index] = (ushort)(((ulong)red[index] * 2126
                + (ulong)green[index] * 7152 + (ulong)blue[index] * 722 + 5000) / 10000);
        }
        return luminance;
    }

    public static IReadOnlyList<IGenericMetaDataHeader> RawMetadataHeaders(DecodedRawImage decoded) {
        ArgumentNullException.ThrowIfNull(decoded);
        var headers = new List<IGenericMetaDataHeader>();
        if (!string.IsNullOrWhiteSpace(decoded.CameraMake)) {
            headers.Add(new StringMetaDataHeader("CAMERAMAKE", decoded.CameraMake));
        }
        if (!string.IsNullOrWhiteSpace(decoded.CameraModel)) {
            headers.Add(new StringMetaDataHeader("CAMERAMODEL", decoded.CameraModel));
        }
        if (!string.IsNullOrWhiteSpace(decoded.OriginalCfaPattern)) {
            headers.Add(new StringMetaDataHeader("BAYERPAT", decoded.OriginalCfaPattern));
        }
        return headers;
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "RAWType is a lowercase file-extension token, not natural-language text or an identifier.")]
    private static string NormalizeRawType(string rawType) => rawType.Trim().TrimStart('.').ToLowerInvariant();
}