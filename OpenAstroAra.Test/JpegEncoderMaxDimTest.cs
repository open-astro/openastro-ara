#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Stretch;

namespace OpenAstroAra.Test;

/// <summary>
/// §349 follow-up — the full-preview encoders gained an opt-in <c>maxDim</c> cap so a huge OSC
/// frame isn't shipped as a multi-MP preview JPEG. Verifies the cap fires (smaller payload) and is
/// a true no-op at the default / when the image already fits.
/// </summary>
[TestFixture]
public class JpegEncoderMaxDimTest {
    private const int W = 1000;
    private const int H = 800;

    private static byte[] Gradient(int count) {
        var b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = (byte)((i * 7) % 256); // non-flat → real JPEG size
        return b;
    }

    [Test]
    public void EncodeGray_maxDim_downscales_so_the_payload_shrinks() {
        var pixels = Gradient(W * H);
        var full = JpegEncoder.EncodeGray(pixels, W, H);                 // maxDim default 0 = no cap
        var capped = JpegEncoder.EncodeGray(pixels, W, H, maxDim: 400);
        capped.Length.Should().BeLessThan(full.Length);
    }

    [Test]
    public void EncodeGray_maxDim_at_or_above_size_is_a_noop() {
        var pixels = Gradient(W * H);
        var full = JpegEncoder.EncodeGray(pixels, W, H);
        var uncapped = JpegEncoder.EncodeGray(pixels, W, H, maxDim: 5000); // larger than the image
        uncapped.Should().Equal(full);
    }

    [Test]
    public void EncodeColor_maxDim_downscales_so_the_payload_shrinks() {
        var rgb = Gradient(W * H * 3);
        var full = JpegEncoder.EncodeColor(rgb, W, H);
        var capped = JpegEncoder.EncodeColor(rgb, W, H, maxDim: 400);
        capped.Length.Should().BeLessThan(full.Length);
    }

    [Test]
    public void EncodeColor_maxDim_at_or_above_size_is_a_noop() {
        var rgb = Gradient(W * H * 3);
        var full = JpegEncoder.EncodeColor(rgb, W, H);
        var uncapped = JpegEncoder.EncodeColor(rgb, W, H, maxDim: 5000); // larger than the image
        uncapped.Should().Equal(full);
    }
}
