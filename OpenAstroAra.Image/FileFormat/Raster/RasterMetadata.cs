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
using System;

namespace OpenAstroAra.Image.FileFormat.Raster;

public static class RasterMetadata {
    /// <summary>
    /// Reconciles decoded color planes with embedded sensor metadata and returns a usable CFA pattern.
    /// </summary>
    public static string? ApplyColorModel(ImageMetaData metadata, bool hasColorPlanes,
            bool assumeBayered = false) {
        ArgumentNullException.ThrowIfNull(metadata);
        if (hasColorPlanes) {
            metadata.Camera.SensorType = SensorType.Color;
            return null;
        }

        var cfaPattern = metadata.Camera.SensorType switch {
            SensorType.RGGB or SensorType.BGGR or SensorType.GBRG or SensorType.GRBG =>
                metadata.Camera.SensorType.ToString().ToUpperInvariant(),
            _ => null,
        };
        if (cfaPattern is not null) return cfaPattern;
        if (assumeBayered) {
            metadata.Camera.SensorType = SensorType.RGGB;
            return SensorType.RGGB.ToString().ToUpperInvariant();
        }

        metadata.Camera.SensorType = SensorType.Monochrome;
        return null;
    }
}