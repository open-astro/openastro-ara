#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;

namespace OpenAstroAra.Image.Tests;

/// <summary>
/// The file readers hand decoded pixels to an <see cref="IImageDataFactory"/>; this one builds a
/// bare <see cref="BaseImageData"/> with no profile or star-detection services, which is all a
/// format round-trip needs to inspect.
/// </summary>
internal sealed class FakeImageDataFactory : IImageDataFactory {

    public BaseImageData CreateBaseImageData(ushort[] input, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData) =>
        new(input, width, height, bitDepth, isBayered, metaData, null!, null!, null!);

    public BaseImageData CreateBaseImageData(IImageArray imageArray, int width, int height, int bitDepth, bool isBayered, ImageMetaData metaData) =>
        new(imageArray, width, height, bitDepth, isBayered, metaData, null!, null!, null!);

    public Task<IImageData> CreateFromFile(string path, int bitDepth, bool isBayered, RawConverter rawConverter, CancellationToken ct = default) =>
        BaseImageData.FromFile(path, bitDepth, isBayered, null, this, ct);
}
