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

namespace OpenAstroAra.Image.Interfaces;

/// <summary>
/// Immutable unsigned 16-bit RGB planes used by headless preview and analysis paths.
/// <see cref="ProcessingMethod"/> identifies sample processing and transfer semantics.
/// Plane methods return borrowed buffers; callers must treat them as immutable.
/// </summary>
public class DecodedColorImage {
    private readonly ushort[] _red;
    private readonly ushort[] _green;
    private readonly ushort[] _blue;

    public DecodedColorImage(int width, int height, int sourceBitDepth,
            ushort[] red, ushort[] green, ushort[] blue,
            string decoderVersion, string processingMethod) {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        ArgumentException.ThrowIfNullOrWhiteSpace(decoderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingMethod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (sourceBitDepth <= 0 || sourceBitDepth > 64) {
            throw new ArgumentOutOfRangeException(nameof(sourceBitDepth));
        }
        var expected = checked((long)width * height);
        if (expected > int.MaxValue || red.LongLength != expected
            || green.LongLength != expected || blue.LongLength != expected) {
            throw new ArgumentException("Decoded color-plane lengths must match width times height.");
        }

        Width = width;
        Height = height;
        SourceBitDepth = sourceBitDepth;
        _red = red;
        _green = green;
        _blue = blue;
        DecoderVersion = decoderVersion;
        ProcessingMethod = processingMethod;
    }

    public int Width { get; }
    public int Height { get; }
    public int SourceBitDepth { get; }
    public string DecoderVersion { get; }
    public string ProcessingMethod { get; }

    public ushort[] BorrowRedPlane() => _red;
    public ushort[] BorrowGreenPlane() => _green;
    public ushort[] BorrowBluePlane() => _blue;
}