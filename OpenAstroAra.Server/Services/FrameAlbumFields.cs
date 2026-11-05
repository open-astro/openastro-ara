#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>Sky Album (#1517): what a frame contributes to the album, read from its header the same
/// way by the drive rescan and the startup backfill.</summary>
internal readonly record struct FrameAlbumFields(double? RaDeg, double? DecDeg, double? PixelScaleArcsec, bool CapturedByAra) {
    /// <summary>The SWCREATE value Ara writes; the XISF writer appends a version, so match the prefix.</summary>
    internal const string AraSoftware = "OpenAstro Ara";

    /// <summary>Arcsec per stored pixel from a pixel size that already includes binning.</summary>
    internal static double? ArcsecPerPixel(double pixelSizeUm, double focalLengthMm) =>
        pixelSizeUm > 0 && focalLengthMm > 0 ? 206.265 * pixelSizeUm / focalLengthMm : null;

    /// <summary>OBJCTRA/OBJCTDEC only when EQUINOX is 2000.0; pixel scale from FOCALLEN and XPIXSZ
    /// (already binned, as the header writer computes it); Ara when SWCREATE starts with Ara's name.</summary>
    internal static FrameAlbumFields FromHeaders(IReadOnlyDictionary<string, string> headers) {
        double? Num(string key) =>
            headers.TryGetValue(key, out var v)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var pointing = Num("EQUINOX") == 2000.0
            ? SqliteFrameRepository.ParseTargetCoordinates(
                headers.TryGetValue("OBJCTRA", out var ra) ? ra : null,
                headers.TryGetValue("OBJCTDEC", out var dec) ? dec : null)
            : null;
        var scale = Num("XPIXSZ") is double px && Num("FOCALLEN") is double focal ? ArcsecPerPixel(px, focal) : null;
        var ara = headers.TryGetValue("SWCREATE", out var sw) && sw.StartsWith(AraSoftware, StringComparison.Ordinal);
        return new FrameAlbumFields(pointing?.RaDegrees, pointing?.DecDegrees, scale, ara);
    }
}
