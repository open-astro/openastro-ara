#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.FileFormat.FITS;
using OpenAstroAra.Image.ImageData;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenAstroAra.Image.FileFormat.Raster;

public static class TiffMetadataCodec {
    private const int FitsCardLength = 80;
    private const int MaximumDescriptionLength = 16 * 1024 * 1024;

    public static string Encode(ImageMetaData metadata, int width, int height) {
        ArgumentNullException.ThrowIfNull(metadata);
        var fitsHeader = new FITSHeader(width, height);
        fitsHeader.PopulateFromMetaData(metadata);
        var description = new StringBuilder();
        foreach (var card in fitsHeader.HeaderCards) {
            var encoded = card.GetHeaderString();
            if (description.Length + encoded.Length + Environment.NewLine.Length
                > MaximumDescriptionLength - (FitsCardLength + Environment.NewLine.Length)) break;
            description.AppendLine(encoded);
        }
        description.AppendLine("END");
        return description.ToString();
    }

    public static bool TryDecode(string? description, int width, int height,
            out ImageMetaData metadata) {
        metadata = new ImageMetaData();
        if (string.IsNullOrWhiteSpace(description)
            || description.Length > MaximumDescriptionLength
            || !description.StartsWith("SIMPLE", StringComparison.Ordinal)) {
            return false;
        }

        try {
            var fitsHeader = new FITSHeader(width, height);
            using var reader = new StringReader(description);
            while (reader.ReadLine() is { } sourceLine) {
                if (sourceLine == "END") break;
                if (sourceLine.Length < 10) continue;
                var line = sourceLine.Length >= FitsCardLength
                    ? sourceLine[..FitsCardLength]
                    : sourceLine.PadRight(FitsCardLength);
                var keyword = line[..8].Trim();
                if (keyword.Length == 0 || line[8] != '=') continue;
                var raw = line[10..];
                var commentIndex = FindComment(raw);
                var rawValue = (commentIndex >= 0 ? raw[..commentIndex] : raw).Trim();
                var comment = commentIndex >= 0 ? raw[(commentIndex + 1)..].Trim() : string.Empty;
                if (rawValue.Length == 0) continue;

                if (rawValue.Length >= 2 && rawValue[0] == '\'' && rawValue[^1] == '\'') {
                    fitsHeader.Add(keyword,
                        rawValue[1..^1].Replace("''", "'", StringComparison.Ordinal).TrimEnd(), comment);
                } else if (rawValue is "T" or "F") {
                    fitsHeader.Add(keyword, rawValue == "T", comment);
                } else if (int.TryParse(rawValue, NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out var integer)) {
                    fitsHeader.Add(keyword, integer, comment);
                } else if (double.TryParse(rawValue, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var number)) {
                    fitsHeader.Add(keyword, number, comment);
                } else {
                    fitsHeader.Add(keyword, rawValue, comment);
                }
            }
            metadata = fitsHeader.ExtractMetaData();
            return true;
        } catch (Exception ex) when (ex is FormatException or OverflowException
                                     or ArgumentException or IndexOutOfRangeException) {
            metadata = new ImageMetaData();
            return false;
        }
    }

    private static int FindComment(string value) {
        var quoted = false;
        for (var index = 0; index < value.Length; index++) {
            if (value[index] == '\'') {
                if (quoted && index + 1 < value.Length && value[index + 1] == '\'') {
                    index++;
                    continue;
                }
                quoted = !quoted;
            } else if (value[index] == '/' && !quoted) {
                return index;
            }
        }
        return -1;
    }
}