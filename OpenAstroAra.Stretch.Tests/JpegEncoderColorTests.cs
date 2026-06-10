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

public class JpegEncoderColorTests {

    private static byte[] SolidRgb(int width, int height, byte r, byte g, byte b) {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++) {
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
        return rgb;
    }

    // JFIF/JPEG files begin with the SOI marker 0xFFD8 and end with EOI 0xFFD9.
    private static void AssertIsJpeg(byte[] bytes) {
        Assert.True(bytes.Length > 4, "encoded JPEG is implausibly small");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
    }

    [Fact]
    public void EncodeColor_produces_a_valid_jpeg() {
        var rgb = SolidRgb(16, 12, 200, 100, 50);
        var jpeg = JpegEncoder.EncodeColor(rgb, 16, 12);
        AssertIsJpeg(jpeg);
    }

    [Fact]
    public void EncodeColorThumbnail_downscales_and_produces_a_valid_jpeg() {
        var rgb = SolidRgb(640, 480, 10, 220, 30);
        var jpeg = JpegEncoder.EncodeColorThumbnail(rgb, 640, 480, maxDim: 320);
        AssertIsJpeg(jpeg);
    }

    [Fact]
    public void EncodeColor_rejects_buffer_length_mismatch() {
        var rgb = SolidRgb(8, 8, 1, 2, 3);
        Assert.Throws<ArgumentException>(() => JpegEncoder.EncodeColor(rgb, 8, 9));
    }

    [Fact]
    public void EncodeColor_rejects_nonpositive_dimensions() {
        var rgb = SolidRgb(4, 4, 1, 2, 3);
        Assert.Throws<ArgumentException>(() => JpegEncoder.EncodeColor(rgb, 0, 4));
    }
}
