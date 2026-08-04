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
using OpenAstroAra.Core.Enums;
using System;
using System.IO;
using System.Threading;

namespace OpenAstroAra.Image.FileFormat.Raster;

public static class TiffImageWriter {
    public static void WriteGrayscale16(string path, ushort[] pixels, int width, int height,
            TIFFCompressionType compressionType, string? imageDescription,
            CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.LongLength != checked((long)width * height)) {
            throw new ArgumentException("TIFF pixel length must match width times height.",
                nameof(pixels));
        }
        cancellationToken.ThrowIfCancellationRequested();
        try {
            using var image = Tiff.Open(path, "w")
                ?? throw new IOException("TIFF output could not be opened.");
            SetRequired(image, TiffTag.IMAGEWIDTH, width);
            SetRequired(image, TiffTag.IMAGELENGTH, height);
            SetRequired(image, TiffTag.SAMPLESPERPIXEL, 1);
            SetRequired(image, TiffTag.BITSPERSAMPLE, 16);
            SetRequired(image, TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
            SetRequired(image, TiffTag.ORIENTATION, Orientation.TOPLEFT);
            SetRequired(image, TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            SetRequired(image, TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            var compression = compressionType switch {
                TIFFCompressionType.NONE => Compression.NONE,
                TIFFCompressionType.LZW => Compression.LZW,
                TIFFCompressionType.ZIP => Compression.ADOBE_DEFLATE,
                _ => throw new ArgumentOutOfRangeException(nameof(compressionType)),
            };
            SetRequired(image, TiffTag.COMPRESSION, compression);
            if (compression != Compression.NONE) {
                SetRequired(image, TiffTag.PREDICTOR, Predictor.HORIZONTAL);
            }
            var rowBytes = checked(width * sizeof(ushort));
            var rowsPerStrip = Math.Min(height, Math.Max(1, (64 * 1024) / rowBytes));
            SetRequired(image, TiffTag.ROWSPERSTRIP, rowsPerStrip);
            SetRequired(image, TiffTag.SOFTWARE, "OpenAstro Ara");
            if (!string.IsNullOrWhiteSpace(imageDescription)) {
                SetRequired(image, TiffTag.IMAGEDESCRIPTION, imageDescription);
            }

            var row = new byte[rowBytes];
            for (var y = 0; y < height; y++) {
                cancellationToken.ThrowIfCancellationRequested();
                Buffer.BlockCopy(pixels, y * rowBytes, row, 0, rowBytes);
                if (!image.WriteScanline(row, y, 0)) {
                    throw new IOException($"TIFF scanline {y} could not be written.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!image.WriteDirectory()) throw new IOException("TIFF image directory could not be finalized.");
        } catch {
            TryDeletePartial(path);
            throw;
        }
    }

    private static void SetRequired(Tiff image, TiffTag tag, params object[] values) {
        if (!image.SetField(tag, values)) {
            throw new IOException($"TIFF tag {tag} could not be written.");
        }
    }

    private static void TryDeletePartial(string path) {
        try {
            File.Delete(path);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}