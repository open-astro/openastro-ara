#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Stretch;

/// <summary>
/// The four Bayer color-filter-array layouts, named by the 2×2 tile at the image (0,0)
/// origin (column-major within the tile, i.e. <c>RGGB</c> = R at (0,0), G at (1,0), G at (0,1),
/// B at (1,1)). The capture path resolves the sensor's native pattern + ASCOM BayerOffsetX/Y
/// into one of these and stamps it as the FITS <c>BAYERPAT</c> header.
/// </summary>
public enum BayerPattern { RGGB, BGGR, GRBG, GBRG }

/// <summary>
/// Bayer-mosaic → RGB debayering for the §65 preview/thumbnail path ONLY. The stored FITS keeps
/// its raw, undebayered mosaic (with a <c>BAYERPAT</c> header) so downstream stackers debayer it
/// themselves; this just renders a color image to look at.
///
/// Uses <b>super-pixel</b> debayering: each 2×2 Bayer tile collapses to one RGB output pixel
/// (R from the red cell, B from the blue cell, G = mean of the two green cells). That halves the
/// resolution — exactly what a downscaled preview wants — and is artifact-free (no interpolation
/// across edges), simple, and fast. Full-resolution bilinear/VNG debayering is overkill for a
/// preview and is left to the real stacking tools operating on the raw FITS.
/// </summary>
public static class Debayer {

    /// <summary>Parse a FITS <c>BAYERPAT</c> header value (case-insensitive) into a pattern.</summary>
    public static bool TryParse(string? bayerPat, out BayerPattern pattern) {
        // FITS string headers come back quoted/space-padded (e.g. "'RGGB    '").
        switch (bayerPat?.Trim().Trim('\'').Trim().ToUpperInvariant()) {
            case "RGGB": pattern = BayerPattern.RGGB; return true;
            case "BGGR": pattern = BayerPattern.BGGR; return true;
            case "GRBG": pattern = BayerPattern.GRBG; return true;
            case "GBRG": pattern = BayerPattern.GBRG; return true;
            default: pattern = BayerPattern.RGGB; return false;
        }
    }

    /// <summary>
    /// Super-pixel debayer a raw mosaic into three half-resolution color planes.
    /// Any trailing odd row/column is dropped (preview-only). Output dimensions are
    /// <c>width/2 × height/2</c>.
    /// </summary>
    public static (ushort[] R, ushort[] G, ushort[] B, int Width, int Height) SuperPixel(
        ReadOnlySpan<ushort> mosaic, int width, int height, BayerPattern pattern) {
        if (width <= 1 || height <= 1) {
            throw new ArgumentException("Bayer mosaic must be at least 2×2.");
        }
        if (mosaic.Length != width * height) {
            throw new ArgumentException(
                $"mosaic length ({mosaic.Length}) doesn't match {width}×{height} = {width * height}");
        }

        // Per-pattern cell offsets within the 2×2 tile: which (dx,dy) is red, blue, and the two greens.
        var (rx, ry, bx, by, g1x, g1y, g2x, g2y) = CellOffsets(pattern);

        int ow = width / 2, oh = height / 2;
        var r = new ushort[ow * oh];
        var g = new ushort[ow * oh];
        var b = new ushort[ow * oh];

        for (int j = 0; j < oh; j++) {
            int sy = j * 2;
            for (int i = 0; i < ow; i++) {
                int sx = i * 2;
                int o = j * ow + i;
                r[o] = mosaic[(sy + ry) * width + (sx + rx)];
                b[o] = mosaic[(sy + by) * width + (sx + bx)];
                // Average the two green cells (round-half-up).
                int gsum = mosaic[(sy + g1y) * width + (sx + g1x)] + mosaic[(sy + g2y) * width + (sx + g2x)];
                g[o] = (ushort)((gsum + 1) / 2);
            }
        }
        return (r, g, b, ow, oh);
    }

    // (rx,ry, bx,by, g1x,g1y, g2x,g2y) for each pattern's 2×2 tile.
    private static (int, int, int, int, int, int, int, int) CellOffsets(BayerPattern pattern) => pattern switch {
        BayerPattern.RGGB => (0, 0, 1, 1, 1, 0, 0, 1), // R=(0,0) B=(1,1) G=(1,0),(0,1)
        BayerPattern.BGGR => (1, 1, 0, 0, 1, 0, 0, 1), // R=(1,1) B=(0,0) G=(1,0),(0,1)
        BayerPattern.GRBG => (1, 0, 0, 1, 0, 0, 1, 1), // R=(1,0) B=(0,1) G=(0,0),(1,1)
        BayerPattern.GBRG => (0, 1, 1, 0, 0, 0, 1, 1), // R=(0,1) B=(1,0) G=(0,0),(1,1)
        _ => (0, 0, 1, 1, 1, 0, 0, 1),
    };
}
