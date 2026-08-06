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
/// Pre-stretch box-average downscaling for the §65 preview path. Stretching a
/// 26-megapixel mono frame only to have the JPEG encoder throw most of it away
/// is where a Pi spends its preview time — averaging s×s blocks FIRST shrinks
/// the stretch+encode workload by s², and the box average doubles as noise
/// reduction (binning), which a screen preview only benefits from. The stored
/// FITS is untouched; this exists purely to render something to look at, fast.
/// </summary>
public static class Decimator {

    /// <summary>
    /// The block size that brings <paramref name="width"/>×<paramref name="height"/>
    /// down to at most ~<paramref name="maxDim"/> on the long side; 1 = no work needed.
    /// </summary>
    public static int StrideFor(int width, int height, int maxDim) {
        if (maxDim <= 0) {
            return 1;
        }
        var longSide = Math.Max(width, height);
        return Math.Max(1, (int)Math.Ceiling(longSide / (double)maxDim));
    }

    /// <summary>
    /// Box-average <paramref name="stride"/>×<paramref name="stride"/> blocks into one
    /// output pixel each. Trailing rows/columns that don't fill a block are dropped
    /// (preview-only). Stride 1 returns a copy-free view of the input dimensions.
    /// </summary>
    public static (ushort[] Pixels, int Width, int Height) Decimate(
        ReadOnlySpan<ushort> pixels, int width, int height, int stride) {
        if (pixels.Length != width * height) {
            throw new ArgumentException("Pixel buffer does not match dimensions.");
        }
        if (stride <= 1) {
            return (pixels.ToArray(), width, height);
        }
        var ow = width / stride;
        var oh = height / stride;
        if (ow == 0 || oh == 0) {
            throw new ArgumentException("Stride exceeds image dimensions.");
        }
        var output = new ushort[ow * oh];
        var area = stride * stride;
        for (var oy = 0; oy < oh; oy++) {
            var syBase = oy * stride;
            for (var ox = 0; ox < ow; ox++) {
                var sxBase = ox * stride;
                long sum = 0;
                for (var dy = 0; dy < stride; dy++) {
                    var row = (syBase + dy) * width + sxBase;
                    for (var dx = 0; dx < stride; dx++) {
                        sum += pixels[row + dx];
                    }
                }
                output[oy * ow + ox] = (ushort)(sum / area);
            }
        }
        return (output, ow, oh);
    }
}
