#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace OpenAstroAra.Image.FileFormat.Raster;

/// <summary>Exact, bounded 16-bit PNG sample decoder, including Adam7 images.</summary>
internal static class Png16Decoder {
    private const int MaximumChunkCount = 65_536;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly int[] Adam7StartX = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Adam7StartY = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Adam7StepX = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Adam7StepY = [8, 8, 8, 4, 4, 2, 2];

    internal static Png16Pixels Decode(ReadOnlyMemory<byte> source, int width, int height,
            byte colorType, byte interlaceMethod, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var channels = colorType switch {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException(
                $"16-bit PNG color type {colorType} is unsupported."),
        };
        var idat = ParseChunks(source, colorType, cancellationToken);
        var pixelCount = checked(width * height);
        var red = colorType is 2 or 6 ? new ushort[pixelCount] : null;
        var green = red is null ? null : new ushort[pixelCount];
        var blue = red is null ? null : new ushort[pixelCount];
        ushort[]? luminance = red is null ? new ushort[pixelCount] : null;

        try {
            using var compressed = new SegmentedReadStream(idat);
            using var decoded = new ZLibStream(compressed, CompressionMode.Decompress);
            if (interlaceMethod == 0) {
                DecodePass(decoded, width, height, 0, 0, 1, 1, channels,
                    luminance, red, green, blue, cancellationToken);
            } else {
                for (var pass = 0; pass < Adam7StartX.Length; pass++) {
                    DecodePass(decoded, width, height, Adam7StartX[pass], Adam7StartY[pass],
                        Adam7StepX[pass], Adam7StepY[pass], channels,
                        luminance, red, green, blue, cancellationToken);
                }
            }
            if (decoded.ReadByte() != -1) {
                throw new RasterImageDecodeException(
                    "PNG decompressed pixel data exceeds the dimensions declared by IHDR.");
            }
        } catch (InvalidDataException ex) {
            throw new RasterImageDecodeException(
                "PNG compressed pixel data is malformed or truncated.", ex);
        } catch (EndOfStreamException ex) {
            throw new RasterImageDecodeException("PNG pixel data is truncated.", ex);
        }

        if (red is not null) {
            luminance = ColorPlaneMath.CreateLuminance(red, green!, blue!, cancellationToken);
        }
        return new Png16Pixels(luminance!, red, green, blue);
    }

    private static List<ReadOnlyMemory<byte>> ParseChunks(ReadOnlyMemory<byte> source,
            byte colorType, CancellationToken cancellationToken) {
        if (source.Length < Signature.Length || !source.Span[..Signature.Length].SequenceEqual(Signature)) {
            throw new RasterImageDecodeException("PNG signature is invalid.");
        }
        var chunks = new List<ReadOnlyMemory<byte>>();
        var offset = Signature.Length;
        var sawHeader = false;
        var sawData = false;
        var dataEnded = false;
        var sawEnd = false;
        var sawPalette = false;
        var chunkCount = 0;
        while (offset < source.Length) {
            cancellationToken.ThrowIfCancellationRequested();
            if (++chunkCount > MaximumChunkCount) {
                throw new RasterImageDecodeException(
                    $"PNG contains more than {MaximumChunkCount} chunks.");
            }
            if (source.Length - offset < 12) {
                throw new RasterImageDecodeException("PNG chunk header is truncated.");
            }
            var span = source.Span;
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
            if (lengthValue > int.MaxValue) {
                throw new RasterImageDecodeException("PNG chunk exceeds the managed integer range.");
            }
            var length = (int)lengthValue;
            var chunkEnd = checked((long)offset + 12 + length);
            if (chunkEnd > source.Length) {
                throw new RasterImageDecodeException("PNG chunk data is truncated.");
            }
            var type = span.Slice(offset + 4, 4);
            if (!IsChunkType(type)) {
                throw new RasterImageDecodeException("PNG chunk type is invalid.");
            }
            var data = source.Slice(offset + 8, length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                span.Slice(offset + 8 + length, 4));
            if (ComputeCrc(type, data.Span, cancellationToken) != expectedCrc) {
                throw new RasterImageDecodeException(
                    $"PNG {System.Text.Encoding.ASCII.GetString(type)} chunk CRC is invalid.");
            }

            if (!sawHeader) {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) {
                    throw new RasterImageDecodeException("PNG IHDR must be the first chunk.");
                }
                sawHeader = true;
            } else if (type.SequenceEqual("IHDR"u8)) {
                throw new RasterImageDecodeException("PNG contains more than one IHDR chunk.");
            } else if (type.SequenceEqual("IDAT"u8)) {
                if (dataEnded) {
                    throw new RasterImageDecodeException("PNG IDAT chunks must be consecutive.");
                }
                sawData = true;
                chunks.Add(data);
            } else if (type.SequenceEqual("PLTE"u8)) {
                if (sawPalette || sawData || colorType is 0 or 4
                    || length is 0 or > 768 || length % 3 != 0) {
                    throw new RasterImageDecodeException("PNG PLTE chunk is invalid.");
                }
                sawPalette = true;
            } else if (type.SequenceEqual("IEND"u8)) {
                if (length != 0 || !sawData) {
                    throw new RasterImageDecodeException("PNG IEND chunk is invalid.");
                }
                sawEnd = true;
                offset = (int)chunkEnd;
                break;
            } else {
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8)
                    || type.SequenceEqual("fdAT"u8)) {
                    throw new NotSupportedException(
                        "Animated PNG imports are unsupported; use one still frame.");
                }
                if (sawData) dataEnded = true;
                if ((type[0] & 0x20) == 0) {
                    throw new NotSupportedException(
                        $"PNG critical chunk {System.Text.Encoding.ASCII.GetString(type)} is unsupported.");
                }
            }
            offset = (int)chunkEnd;
        }
        if (!sawEnd || offset != source.Length) {
            throw new RasterImageDecodeException("PNG IEND is missing or trailing data is present.");
        }
        return chunks;
    }

    private static void DecodePass(Stream decoded, int imageWidth, int imageHeight,
            int startX, int startY, int stepX, int stepY, int channels,
            ushort[]? luminance, ushort[]? red, ushort[]? green, ushort[]? blue,
            CancellationToken cancellationToken) {
        var width = PassLength(imageWidth, startX, stepX);
        var height = PassLength(imageHeight, startY, stepY);
        if (width == 0 || height == 0) return;
        var bytesPerPixel = checked(channels * sizeof(ushort));
        var rowBytes = checked(width * bytesPerPixel);
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var passY = 0; passY < height; passY++) {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = decoded.ReadByte();
            if (filter < 0) throw new EndOfStreamException();
            decoded.ReadExactly(current);
            Unfilter(current, previous, bytesPerPixel, filter);
            var y = startY + passY * stepY;
            for (var passX = 0; passX < width; passX++) {
                var x = startX + passX * stepX;
                var destination = checked(y * imageWidth + x);
                var sample = passX * bytesPerPixel;
                if (red is null) {
                    luminance![destination] = BinaryPrimitives.ReadUInt16BigEndian(
                        current.AsSpan(sample, sizeof(ushort)));
                } else {
                    red[destination] = BinaryPrimitives.ReadUInt16BigEndian(
                        current.AsSpan(sample, sizeof(ushort)));
                    green![destination] = BinaryPrimitives.ReadUInt16BigEndian(
                        current.AsSpan(sample + 2, sizeof(ushort)));
                    blue![destination] = BinaryPrimitives.ReadUInt16BigEndian(
                        current.AsSpan(sample + 4, sizeof(ushort)));
                }
            }
            (previous, current) = (current, previous);
        }
    }

    private static void Unfilter(Span<byte> current, ReadOnlySpan<byte> previous,
            int bytesPerPixel, int filter) {
        switch (filter) {
            case 0:
                return;
            case 1:
                for (var index = bytesPerPixel; index < current.Length; index++) {
                    current[index] = unchecked((byte)(current[index] + current[index - bytesPerPixel]));
                }
                return;
            case 2:
                for (var index = 0; index < current.Length; index++) {
                    current[index] = unchecked((byte)(current[index] + previous[index]));
                }
                return;
            case 3:
                for (var index = 0; index < current.Length; index++) {
                    var left = index < bytesPerPixel ? 0 : current[index - bytesPerPixel];
                    current[index] = unchecked((byte)(current[index]
                        + ((left + previous[index]) >> 1)));
                }
                return;
            case 4:
                for (var index = 0; index < current.Length; index++) {
                    var left = index < bytesPerPixel ? 0 : current[index - bytesPerPixel];
                    var upperLeft = index < bytesPerPixel ? 0 : previous[index - bytesPerPixel];
                    current[index] = unchecked((byte)(current[index]
                        + Paeth(left, previous[index], upperLeft)));
                }
                return;
            default:
                throw new RasterImageDecodeException($"PNG row filter {filter} is invalid.");
        }
    }

    private static int Paeth(int left, int above, int upperLeft) {
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

    private static bool IsChunkType(ReadOnlySpan<byte> type) {
        foreach (var value in type) {
            if (value is not (>= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z')) return false;
        }
        return true;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data,
            CancellationToken cancellationToken) {
        var crc = uint.MaxValue;
        UpdateCrc(ref crc, type, cancellationToken);
        UpdateCrc(ref crc, data, cancellationToken);
        return ~crc;
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> bytes,
            CancellationToken cancellationToken) {
        for (var index = 0; index < bytes.Length; index++) {
            if ((index & 0xfffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = bytes[index];
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
        }
    }

    private sealed class SegmentedReadStream(List<ReadOnlyMemory<byte>> segments)
            : Stream {
        private int _segment;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var written = 0;
            while (!buffer.IsEmpty && _segment < segments.Count) {
                var remaining = segments[_segment].Span[_offset..];
                if (remaining.IsEmpty) {
                    _segment++;
                    _offset = 0;
                    continue;
                }
                var count = Math.Min(buffer.Length, remaining.Length);
                remaining[..count].CopyTo(buffer);
                buffer = buffer[count..];
                written += count;
                _offset += count;
            }
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

internal sealed record Png16Pixels(ushort[] Luminance, ushort[]? Red,
    ushort[]? Green, ushort[]? Blue);