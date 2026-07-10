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
/// origin in (x,y) = (column,row) order, i.e. <c>RGGB</c> = R at (0,0), G at (1,0), G at (0,1),
/// B at (1,1). The capture path resolves the sensor's native pattern + ASCOM BayerOffsetX/Y
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

    /// <summary>
    /// Super-pixel debayer + per-channel stretch + RGB interleave — the whole
    /// "raw OSC mosaic → displayable half-res RGB" preview recipe in one call, shared by the §65
    /// capture preview and the §64 Live View render so the two paths can't drift. Per-channel
    /// auto-stretch incidentally auto-white-balances the preview; the stored/live raw data stays
    /// the undebayered mosaic.
    /// <para>WB caveat: with a caller-supplied <paramref name="stretchParams"/> (manual black/white
    /// points), applying the same params per channel can shift white balance — those points were
    /// chosen against the mosaic's combined luminance, not per-channel. Acceptable for a preview;
    /// revisit if manual OSC stretch looks off.</para>
    /// </summary>
    public static (byte[] Rgb, int Width, int Height) SuperPixelStretched(
        ReadOnlySpan<ushort> mosaic, int width, int height, BayerPattern pattern,
        StretchAlgorithm algorithm, StretchParams? stretchParams = null) {
        var (r, g, b, ow, oh) = SuperPixel(mosaic, width, height, pattern);
        return (StretchAndInterleave(r, g, b, algorithm, stretchParams), ow, oh);
    }

    /// <summary>
    /// <see cref="SuperPixelStretched"/> plus a raw 16-bit LUMINANCE plane on the same half-res
    /// grid — one debayer pass serves both the displayable colour and a detection-grade mono
    /// plane (§64 OSC live annotation: the star detector must see real, unstretched dynamics,
    /// and detecting on the same grid as the colour output means markers land on their stars
    /// without any coordinate scaling). Luminance keeps the CFA's own 1:2:1 channel weighting
    /// ((R + 2G + B) / 4, round-half-up).
    /// </summary>
    public static (byte[] Rgb, ushort[] Luminance, int Width, int Height) SuperPixelStretchedWithLuminance(
        ReadOnlySpan<ushort> mosaic, int width, int height, BayerPattern pattern,
        StretchAlgorithm algorithm, StretchParams? stretchParams = null) {
        var (r, g, b, ow, oh) = SuperPixel(mosaic, width, height, pattern);
        var luminance = new ushort[r.Length];
        for (int i = 0; i < r.Length; i++) {
            luminance[i] = (ushort)((r[i] + 2 * g[i] + b[i] + 2) / 4);
        }
        return (StretchAndInterleave(r, g, b, algorithm, stretchParams), luminance, ow, oh);
    }

    private static byte[] StretchAndInterleave(
        ushort[] r, ushort[] g, ushort[] b, StretchAlgorithm algorithm, StretchParams? stretchParams) {
        var rs = Stretcher.Apply(algorithm, r, stretchParams);
        var gs = Stretcher.Apply(algorithm, g, stretchParams);
        var bs = Stretcher.Apply(algorithm, b, stretchParams);
        var rgb = new byte[rs.Length * 3];
        for (int i = 0, d = 0; i < rs.Length; i++, d += 3) {
            rgb[d] = rs[i];
            rgb[d + 1] = gs[i];
            rgb[d + 2] = bs[i];
        }
        return rgb;
    }

    /// <summary>
    /// Full-resolution bilinear debayer of a raw mosaic into three R/G/B planes, each
    /// <c>width × height</c>. At every pixel the native CFA channel is kept and the two missing
    /// channels are bilinearly interpolated from the appropriate orthogonal / diagonal / row /
    /// column neighbours (edge-clamped). Higher quality than <see cref="SuperPixel"/> (full
    /// resolution, no blockiness) at ~4× the cost — for the §2105 in-memory render, not the §65
    /// half-res preview. CPU-bound; offload from a UI/event thread.
    /// </summary>
    public static (ushort[] R, ushort[] G, ushort[] B) Bilinear(
        ReadOnlySpan<ushort> mosaic, int width, int height, BayerPattern pattern) {
        if (width <= 1 || height <= 1) {
            throw new ArgumentException("Bayer mosaic must be at least 2×2.");
        }
        if (mosaic.Length != (long)width * height) {
            throw new ArgumentException(
                $"mosaic length ({mosaic.Length}) doesn't match {width}×{height} = {(long)width * height}");
        }
        var (rx, ry, bx, by, _, _, _, _) = CellOffsets(pattern);
        var r = new ushort[mosaic.Length];
        var g = new ushort[mosaic.Length];
        var b = new ushort[mosaic.Length];

        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int idx = y * width + x;
                ushort v = mosaic[idx];
                int px = x & 1, py = y & 1;
                if (px == rx && py == ry) {            // red site: G orthogonal, B diagonal
                    r[idx] = v;
                    g[idx] = MeanOrtho(mosaic, x, y, width, height);
                    b[idx] = MeanDiag(mosaic, x, y, width, height);
                } else if (px == bx && py == by) {     // blue site: G orthogonal, R diagonal
                    b[idx] = v;
                    g[idx] = MeanOrtho(mosaic, x, y, width, height);
                    r[idx] = MeanDiag(mosaic, x, y, width, height);
                } else {                                // green site
                    g[idx] = v;
                    // If this green shares red's tile-row, red is the left/right neighbour (blue up/down);
                    // otherwise it shares red's column, so red is up/down (blue left/right).
                    if (py == ry) {
                        r[idx] = MeanH(mosaic, x, y, width, height);
                        b[idx] = MeanV(mosaic, x, y, width, height);
                    } else {
                        r[idx] = MeanV(mosaic, x, y, width, height);
                        b[idx] = MeanH(mosaic, x, y, width, height);
                    }
                }
            }
        }
        return (r, g, b);
    }

    // Mirror-reflected pixel read + round-half-up neighbour means for the bilinear kernel. Reflection
    // (not clamping) at the borders is essential: a ±1 neighbour that falls off the edge reflects to
    // the pixel 1 inside, shifting the index by 2 so it lands on the SAME CFA colour as the wanted
    // (off-edge) neighbour — clamping would instead return the edge pixel itself, a wrong-colour
    // sample, producing a coloured fringe along all four borders.
    private static int Reflect(int i, int n) {
        if (n == 1) return 0;
        if (i < 0) return -i;               // -1 -> 1  (parity preserved)
        if (i >= n) return 2 * (n - 1) - i; //  n -> n-2 (parity preserved)
        return i;
    }

    private static ushort Px(ReadOnlySpan<ushort> m, int x, int y, int w, int h) =>
        m[Reflect(y, h) * w + Reflect(x, w)];

    private static ushort MeanOrtho(ReadOnlySpan<ushort> m, int x, int y, int w, int h) =>
        (ushort)((Px(m, x - 1, y, w, h) + Px(m, x + 1, y, w, h) + Px(m, x, y - 1, w, h) + Px(m, x, y + 1, w, h) + 2) / 4);

    private static ushort MeanDiag(ReadOnlySpan<ushort> m, int x, int y, int w, int h) =>
        (ushort)((Px(m, x - 1, y - 1, w, h) + Px(m, x + 1, y - 1, w, h) + Px(m, x - 1, y + 1, w, h) + Px(m, x + 1, y + 1, w, h) + 2) / 4);

    private static ushort MeanH(ReadOnlySpan<ushort> m, int x, int y, int w, int h) =>
        (ushort)((Px(m, x - 1, y, w, h) + Px(m, x + 1, y, w, h) + 1) / 2);

    private static ushort MeanV(ReadOnlySpan<ushort> m, int x, int y, int w, int h) =>
        (ushort)((Px(m, x, y - 1, w, h) + Px(m, x, y + 1, w, h) + 1) / 2);

    // (rx,ry, bx,by, g1x,g1y, g2x,g2y) for each pattern's 2×2 tile.
    private static (int, int, int, int, int, int, int, int) CellOffsets(BayerPattern pattern) => pattern switch {
        BayerPattern.RGGB => (0, 0, 1, 1, 1, 0, 0, 1), // R=(0,0) B=(1,1) G=(1,0),(0,1)
        BayerPattern.BGGR => (1, 1, 0, 0, 1, 0, 0, 1), // R=(1,1) B=(0,0) G=(1,0),(0,1)
        BayerPattern.GRBG => (1, 0, 0, 1, 0, 0, 1, 1), // R=(1,0) B=(0,1) G=(0,0),(1,1)
        BayerPattern.GBRG => (0, 1, 1, 0, 0, 0, 1, 1), // R=(0,1) B=(1,0) G=(0,0),(1,1)
        // No silent RGGB fallback: a new pattern added without updating this map should fail loudly,
        // not render wrong colors.
        _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unhandled Bayer pattern"),
    };
}
