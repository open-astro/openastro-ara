#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Direct Alpaca ImageBytes download (Alpaca API v1, metadata v1) for the
/// capture path. The ASCOM client library's <c>ImageArray</c> receives this
/// same binary payload but inflates it into a 104 MB <c>int[,]</c> that the
/// capture path then immediately converts back to <c>ushort[]</c> — double
/// work and double allocation on a Pi. This fetches the wire bytes and
/// transposes them straight into the raster buffer. Callers fall back to the
/// library path on any failure (older bridges, JSON-only devices).
/// </summary>
internal static class AlpacaImageBytes {

    // Alpaca ImageBytes type codes (spec: 1=Int16, 2=Int32, 6=Byte, 8=UInt16).
    private const int TypeInt16 = 1;
    private const int TypeInt32 = 2;
    private const int TypeByte = 6;
    private const int TypeUInt16 = 8;

    /// <summary>
    /// GET <c>/api/v1/camera/{device}/imagearray</c> with
    /// <c>Accept: application/imagebytes</c> and decode a rank-2 payload into a
    /// row-major <c>ushort[]</c>. Returns null when the device answered with
    /// JSON (no ImageBytes support) — the caller then uses the library path.
    /// Throws on transport errors and malformed payloads.
    /// </summary>
    public static async Task<(ushort[] Pixels, int Width, int Height)?> DownloadAsync(
            HttpClient http, Uri baseAddress, int deviceNumber, CancellationToken ct) {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(baseAddress, $"api/v1/camera/{deviceNumber}/imagearray"));
        request.Headers.Accept.ParseAdd("application/imagebytes");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/imagebytes", StringComparison.OrdinalIgnoreCase)) {
            return null; // device ignored the Accept header — no ImageBytes support
        }
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Decode(body);
    }

    /// <summary>Decode an ImageBytes body. Internal for tests.</summary>
    internal static (ushort[] Pixels, int Width, int Height) Decode(ReadOnlySpan<byte> body) {
        if (body.Length < 44) {
            throw new InvalidOperationException($"ImageBytes metadata truncated ({body.Length} bytes).");
        }
        var metadataVersion = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (metadataVersion != 1) {
            throw new InvalidOperationException($"unsupported ImageBytes metadata version {metadataVersion}");
        }
        var errorNumber = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (errorNumber != 0) {
            throw new InvalidOperationException($"device returned Alpaca error {errorNumber} in ImageBytes response");
        }
        var dataStart = BinaryPrimitives.ReadInt32LittleEndian(body[16..]);
        var transmissionType = BinaryPrimitives.ReadInt32LittleEndian(body[24..]);
        var rank = BinaryPrimitives.ReadInt32LittleEndian(body[28..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(body[32..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(body[36..]);
        if (rank != 2) {
            throw new InvalidOperationException($"rank-{rank} ImageBytes payload is not supported (color v1 captures are rank 2)");
        }
        if (width <= 0 || height <= 0 || dataStart < 44 || dataStart > body.Length) {
            throw new InvalidOperationException($"implausible ImageBytes geometry {width}x{height} dataStart={dataStart}");
        }
        var payload = body[dataStart..];
        var count = (long)width * height;
        var pixels = new ushort[count];

        // The payload is the flattened Alpaca [x,y] array — x-major, y
        // contiguous — so this is the same tiled transpose the int[,] path
        // uses, minus the 104 MB intermediate.
        switch (transmissionType) {
            case TypeUInt16: {
                ExpectLength(payload.Length, count * 2, "UInt16");
                Transpose(MemoryMarshal.Cast<byte, ushort>(payload), pixels, width, height, static v => v);
                break;
            }
            case TypeInt16: {
                ExpectLength(payload.Length, count * 2, "Int16");
                Transpose(MemoryMarshal.Cast<byte, short>(payload), pixels, width, height,
                    static v => (ushort)Math.Clamp((int)v, ushort.MinValue, ushort.MaxValue));
                break;
            }
            case TypeInt32: {
                ExpectLength(payload.Length, count * 4, "Int32");
                Transpose(MemoryMarshal.Cast<byte, int>(payload), pixels, width, height,
                    static v => (ushort)Math.Clamp(v, ushort.MinValue, ushort.MaxValue));
                break;
            }
            case TypeByte: {
                ExpectLength(payload.Length, count, "Byte");
                Transpose(payload, pixels, width, height, static v => (ushort)(v << 8));
                break;
            }
            default:
                throw new InvalidOperationException($"unsupported ImageBytes transmission type {transmissionType}");
        }
        return (pixels, width, height);
    }

    private static void ExpectLength(int actual, long expected, string type) {
        if (actual < expected) {
            throw new InvalidOperationException($"ImageBytes {type} payload truncated: {actual} < {expected} bytes");
        }
    }

    private static void Transpose<T>(ReadOnlySpan<T> source, ushort[] dest, int width, int height, Func<T, ushort> convert)
            where T : struct {
        // Span can't cross a Parallel.For lambda; the single-threaded tiled
        // walk is already memory-bound-fast (~150ms for 26 MP on a Pi) and
        // runs while no other capture work is in flight.
        const int tile = 64;
        for (var y0 = 0; y0 < height; y0 += tile) {
            var y1 = Math.Min(y0 + tile, height);
            for (var x0 = 0; x0 < width; x0 += tile) {
                var x1 = Math.Min(x0 + tile, width);
                for (var x = x0; x < x1; x++) {
                    var col = x * height;
                    for (var y = y0; y < y1; y++) {
                        dest[y * width + x] = convert(source[col + y]);
                    }
                }
            }
        }
    }
}
