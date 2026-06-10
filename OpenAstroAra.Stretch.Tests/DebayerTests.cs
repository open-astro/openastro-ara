#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Stretch;
using Xunit;

namespace OpenAstroAra.Stretch.Tests;

public class DebayerTests {

    [Theory]
    [InlineData("RGGB", BayerPattern.RGGB)]
    [InlineData("BGGR", BayerPattern.BGGR)]
    [InlineData("GRBG", BayerPattern.GRBG)]
    [InlineData("GBRG", BayerPattern.GBRG)]
    [InlineData("rggb", BayerPattern.RGGB)]            // case-insensitive
    [InlineData("'RGGB    '", BayerPattern.RGGB)]      // FITS quoted/space-padded form
    [InlineData("  GBRG  ", BayerPattern.GBRG)]        // whitespace-padded
    public void TryParse_accepts_valid_patterns(string input, BayerPattern expected) {
        Assert.True(Debayer.TryParse(input, out var pattern));
        Assert.Equal(expected, pattern);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CMYG")]
    [InlineData("MONO")]
    [InlineData("XYZW")]
    public void TryParse_rejects_mono_and_unknown(string? input) {
        Assert.False(Debayer.TryParse(input, out _));
    }

    /// <summary>
    /// A 2×2 RGGB tile maps deterministically: R=top-left, B=bottom-right, G=mean of the
    /// two greens. One tile in, one RGB super-pixel out.
    /// </summary>
    [Fact]
    public void SuperPixel_RGGB_single_tile_picks_correct_cells() {
        //  R=10  G=20
        //  G=40  B=80
        ushort[] mosaic = { 10, 20, 40, 80 };
        var (r, g, b, w, h) = Debayer.SuperPixel(mosaic, 2, 2, BayerPattern.RGGB);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(10, r[0]);
        Assert.Equal((20 + 40 + 1) / 2, g[0]); // round-half-up mean of the greens
        Assert.Equal(80, b[0]);
    }

    [Fact]
    public void SuperPixel_BGGR_single_tile_swaps_red_and_blue() {
        //  B=10  G=20
        //  G=40  R=80
        ushort[] mosaic = { 10, 20, 40, 80 };
        var (r, g, b, _, _) = Debayer.SuperPixel(mosaic, 2, 2, BayerPattern.BGGR);
        Assert.Equal(80, r[0]);
        Assert.Equal((20 + 40 + 1) / 2, g[0]);
        Assert.Equal(10, b[0]);
    }

    [Fact]
    public void SuperPixel_GRBG_single_tile_picks_correct_cells() {
        //  G=10  R=20
        //  B=40  G=80
        ushort[] mosaic = { 10, 20, 40, 80 };
        var (r, g, b, _, _) = Debayer.SuperPixel(mosaic, 2, 2, BayerPattern.GRBG);
        Assert.Equal(20, r[0]);
        Assert.Equal((10 + 80 + 1) / 2, g[0]);
        Assert.Equal(40, b[0]);
    }

    [Fact]
    public void SuperPixel_GBRG_single_tile_picks_correct_cells() {
        //  G=10  B=20
        //  R=40  G=80
        ushort[] mosaic = { 10, 20, 40, 80 };
        var (r, g, b, _, _) = Debayer.SuperPixel(mosaic, 2, 2, BayerPattern.GBRG);
        Assert.Equal(40, r[0]);
        Assert.Equal((10 + 80 + 1) / 2, g[0]);
        Assert.Equal(20, b[0]);
    }

    /// <summary>Output is exactly half-resolution; each output pixel maps to its own 2×2 tile.</summary>
    [Fact]
    public void SuperPixel_halves_resolution_and_maps_each_tile() {
        // 4×2 RGGB → 2×1 output. Two side-by-side tiles.
        // tile0: R=1 G=2 / G=3 B=4    tile1: R=5 G=6 / G=7 B=8
        ushort[] mosaic = {
            1, 2, 5, 6,
            3, 4, 7, 8,
        };
        var (r, g, b, w, h) = Debayer.SuperPixel(mosaic, 4, 2, BayerPattern.RGGB);
        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(new ushort[] { 1, 5 }, r);
        Assert.Equal(new ushort[] { 4, 8 }, b);
        Assert.Equal(new ushort[] { (ushort)((2 + 3 + 1) / 2), (ushort)((6 + 7 + 1) / 2) }, g);
    }

    /// <summary>A trailing odd row/column is dropped (preview-only contract).</summary>
    [Fact]
    public void SuperPixel_drops_odd_trailing_row_and_column() {
        // 3×3 → 1×1 (the last row & column are ignored).
        ushort[] mosaic = {
            10, 20, 99,
            40, 80, 99,
            99, 99, 99,
        };
        var (r, g, b, w, h) = Debayer.SuperPixel(mosaic, 3, 3, BayerPattern.RGGB);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(10, r[0]);
        Assert.Equal(80, b[0]);
    }

    [Fact]
    public void SuperPixel_rejects_mismatched_length() {
        ushort[] mosaic = { 1, 2, 3 }; // not 2×2
        Assert.Throws<ArgumentException>(() => Debayer.SuperPixel(mosaic, 2, 2, BayerPattern.RGGB));
    }

    [Fact]
    public void SuperPixel_rejects_sub_2x2() {
        ushort[] mosaic = { 1, 2 };
        Assert.Throws<ArgumentException>(() => Debayer.SuperPixel(mosaic, 2, 1, BayerPattern.RGGB));
    }
}
