#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Stretch;
using System;

namespace OpenAstroAra.Test;

/// <summary>Mono library thumbnails: decimated at the preview's stride before the
/// stretch (a full-resolution stretch made a cold tile slower than the full preview).</summary>
public class ThumbnailRenderTest {

    private static ushort[] Frame(int width, int height) {
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++) {
            // A sky background near 500 ADU (a deterministic scramble) with a few bright "stars".
            pixels[i] = (ushort)(480 + (int)((uint)(i * 7919) % 41) + (i % 1999 == 0 ? 20000 : 0));
        }
        return pixels;
    }

    /// <summary>Width and height from the JPEG's baseline SOF0 header.</summary>
    private static (int Width, int Height) JpegSize(byte[] jpeg) {
        for (var i = 2; i + 8 < jpeg.Length; i++) {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0) {
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            }
        }
        throw new InvalidOperationException("no SOF0 marker");
    }

    [Test]
    public void A_frame_bigger_than_the_preview_is_shrunk_first_and_renders_a_320_px_tile() {
        // 6248 wide, the ASI2600-class sensor from the field: stride 2 at the 4096 preview cap.
        var jpeg = SqliteFrameRepository.RenderMonoThumbnail(Frame(6248, 400), 6248, 400, StretchAlgorithm.AutoStf);
        var (w, h) = JpegSize(jpeg);
        Assert.That(w, Is.EqualTo(320));
        Assert.That(h, Is.EqualTo(20).Within(1), "aspect kept through the decimation");
    }

    [Test]
    public void A_frame_within_the_preview_size_renders_from_its_own_pixels() {
        var pixels = Frame(1600, 1200);
        var jpeg = SqliteFrameRepository.RenderMonoThumbnail(pixels, 1600, 1200, StretchAlgorithm.AutoStf);
        var expected = JpegEncoder.EncodeThumbnail(Stretcher.Apply(StretchAlgorithm.AutoStf, pixels), 1600, 1200);
        Assert.That(jpeg, Is.EqualTo(expected), "no decimation below the preview cap");
        Assert.That(JpegSize(jpeg), Is.EqualTo((320, 240)));
    }
}
