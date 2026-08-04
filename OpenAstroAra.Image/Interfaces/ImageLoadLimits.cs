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
/// Allocation limits applied before source-image decoding. Defaults admit large
/// modern astronomy sensors while bounding malformed files and decompression bombs.
/// </summary>
public sealed record ImageLoadLimits(
    long MaxFileBytes = 2L * 1024 * 1024 * 1024,
    int MaxDimension = 100_000,
    long MaxPixelCount = 250_000_000,
    int MaxHeaderBytes = 16 * 1024 * 1024,
    long MaxDecodedBytes = 2L * 1024 * 1024 * 1024) {

    public static ImageLoadLimits Default { get; } = new();

    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPixelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDecodedBytes);
    }
}