#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text;

namespace OpenAstroAra.Test.TestData;

/// <summary>Writes a minimal little-endian, uncompressed DNG with an RGGB CFA.</summary>
internal static class SyntheticDng {
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeSignedRational = 10;

    internal static byte[] Create(int width = 64, int height = 48) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        var pixelBytes = new byte[checked(width * height * 2)];
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var value = (y & 1, x & 1) switch {
                    (0, 0) => 50_000,
                    (1, 1) => 10_000,
                    _ => 30_000,
                };
                value += (x + y) % 1000;
                var offset = (y * width + x) * 2;
                pixelBytes[offset] = (byte)value;
                pixelBytes[offset + 1] = (byte)(value >> 8);
            }
        }

        var entries = new List<Entry> {
            Long(256, (uint)width),
            Long(257, (uint)height),
            Short(258, 16),
            Short(259, 1),
            Short(262, 32803),
            Ascii(271, "OpenAstro"),
            Ascii(272, "Synthetic RGGB"),
            Long(273, 0), // strip offset patched after auxiliary blocks are placed
            Short(274, 1),
            Short(277, 1),
            Long(278, (uint)height),
            Long(279, (uint)pixelBytes.Length),
            Short(284, 1),
            Shorts(33421, 2, 2),
            Bytes(33422, 0, 1, 1, 2),
            Bytes(50706, 1, 4, 0, 0),
            Bytes(50707, 1, 1, 0, 0),
            Ascii(50708, "OpenAstro Synthetic RGGB"),
            Bytes(50710, 0, 1, 2),
            Short(50711, 1),
            Shorts(50713, 1, 1),
            Short(50714, 0),
            Long(50717, ushort.MaxValue),
            Longs(50719, 0, 0),
            Longs(50720, (uint)width, (uint)height),
            SignedRationals(50721, 1, 0, 0, 0, 1, 0, 0, 0, 1),
            Rationals(50728, 1, 1, 1),
            Short(50778, 21),
            Longs(50829, 0, 0, (uint)height, (uint)width),
        };
        entries.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));

        const int tiffHeaderBytes = 8;
        var ifdBytes = checked(2 + entries.Count * 12 + 4);
        var nextOffset = tiffHeaderBytes + ifdBytes;
        foreach (var entry in entries.Where(static entry => entry.Data.Length > 4)) {
            entry.Offset = checked((uint)nextOffset);
            nextOffset += entry.Data.Length;
            if ((nextOffset & 1) != 0) nextOffset++;
        }
        var stripOffset = checked((uint)nextOffset);
        entries.Single(static entry => entry.Tag == 273).SetInlineUInt32(stripOffset);

        using var output = new MemoryStream(checked(nextOffset + pixelBytes.Length));
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)tiffHeaderBytes);
        writer.Write((ushort)entries.Count);
        foreach (var entry in entries) {
            writer.Write(entry.Tag);
            writer.Write(entry.Type);
            writer.Write(entry.Count);
            if (entry.Data.Length <= 4) {
                writer.Write(entry.Data);
                for (var index = entry.Data.Length; index < 4; index++) writer.Write((byte)0);
            } else {
                writer.Write(entry.Offset);
            }
        }
        writer.Write(0u);
        foreach (var entry in entries.Where(static entry => entry.Data.Length > 4)) {
            while (output.Position < entry.Offset) writer.Write((byte)0);
            writer.Write(entry.Data);
        }
        while (output.Position < stripOffset) writer.Write((byte)0);
        writer.Write(pixelBytes);
        return output.ToArray();
    }

    private static Entry Bytes(ushort tag, params byte[] values) =>
        new(tag, TypeByte, (uint)values.Length, values);

    private static Entry Ascii(ushort tag, string value) {
        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        return new Entry(tag, TypeAscii, (uint)bytes.Length, bytes);
    }

    private static Entry Short(ushort tag, ushort value) =>
        new(tag, TypeShort, 1, LittleEndian(value));

    private static Entry Shorts(ushort tag, params ushort[] values) =>
        new(tag, TypeShort, (uint)values.Length, [.. values.SelectMany(LittleEndian)]);

    private static Entry Long(ushort tag, uint value) =>
        new(tag, TypeLong, 1, LittleEndian(value));

    private static Entry Longs(ushort tag, params uint[] values) =>
        new(tag, TypeLong, (uint)values.Length, [.. values.SelectMany(LittleEndian)]);

    private static Entry Rationals(ushort tag, params uint[] numerators) {
        var data = numerators.SelectMany(value => LittleEndian(value).Concat(LittleEndian(1u))).ToArray();
        return new Entry(tag, TypeRational, (uint)numerators.Length, data);
    }

    private static Entry SignedRationals(ushort tag, params int[] numerators) {
        var data = numerators.SelectMany(value => LittleEndian(value).Concat(LittleEndian(1))).ToArray();
        return new Entry(tag, TypeSignedRational, (uint)numerators.Length, data);
    }

    private static byte[] LittleEndian(ushort value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] LittleEndian(uint value) => [
        (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
    ];

    private static byte[] LittleEndian(int value) => LittleEndian(unchecked((uint)value));

    private sealed class Entry(ushort tag, ushort type, uint count, byte[] data) {
        internal ushort Tag { get; } = tag;
        internal ushort Type { get; } = type;
        internal uint Count { get; } = count;
        internal byte[] Data { get; private set; } = data;
        internal uint Offset { get; set; }

        internal void SetInlineUInt32(uint value) => Data = LittleEndian(value);
    }
}