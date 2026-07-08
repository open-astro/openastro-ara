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
using OpenAstroAra.Stretch;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test;

/// <summary>
/// §64/§59 — the star-marker overlay encoder (`JpegEncoder.EncodeGrayAnnotated`): draws green circles at
/// given pixel positions over a stretched grayscale preview. Decodes the JPEG back and inspects the pixels
/// to confirm a coloured marker is drawn where asked, a marker-less call stays monochrome, and the maxDim
/// cap downscales the annotated frame.
/// </summary>
[TestFixture]
public class JpegEncoderAnnotatedTest {

    private static byte[] FlatGray(int w, int h, byte level) {
        var b = new byte[w * h];
        Array.Fill(b, level);
        return b;
    }

    // A pixel where the green channel clearly dominates red and blue — i.e. part of a drawn marker, not the
    // gray background (which decodes to r≈g≈b). The margin absorbs JPEG chroma noise.
    private static bool IsGreenish(SKColor c) => c.Green > c.Red + 30 && c.Green > c.Blue + 30;

    [Test]
    public void EncodeGrayAnnotated_draws_a_green_marker_where_asked() {
        int w = 100, h = 100;
        var pixels = FlatGray(w, h, 30);
        var markers = new List<StarMarker> { new(50, 50, 12) };

        var jpeg = JpegEncoder.EncodeGrayAnnotated(pixels, w, h, markers);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded.Width, Is.EqualTo(w));
        Assert.That(decoded.Height, Is.EqualTo(h));

        int greenCount = 0;
        long sumX = 0, sumY = 0;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                if (IsGreenish(decoded.GetPixel(x, y))) {
                    greenCount++;
                    sumX += x;
                    sumY += y;
                }
            }
        }
        Assert.That(greenCount, Is.GreaterThan(0), "the green marker circle should be visible in the decoded JPEG");
        // The ring is centred on the marker, so the mean of its pixels lands near the requested centre.
        Assert.That(sumX / (double)greenCount, Is.EqualTo(50).Within(8), "marker X should be where it was drawn");
        Assert.That(sumY / (double)greenCount, Is.EqualTo(50).Within(8), "marker Y should be where it was drawn");
    }

    [Test]
    public void EncodeGrayAnnotated_with_no_markers_stays_monochrome() {
        int w = 80, h = 80;
        var pixels = FlatGray(w, h, 40);

        var jpeg = JpegEncoder.EncodeGrayAnnotated(pixels, w, h, new List<StarMarker>());
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.That(decoded, Is.Not.Null);
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                Assert.That(IsGreenish(decoded.GetPixel(x, y)), Is.False,
                    "a marker-less annotated encode must not introduce coloured pixels");
            }
        }
    }

    [Test]
    public void EncodeGrayAnnotated_maxDim_downscales_the_annotated_frame() {
        int w = 1000, h = 800;
        var pixels = FlatGray(w, h, 30);
        var markers = new List<StarMarker> { new(500, 400, 20) };

        var jpeg = JpegEncoder.EncodeGrayAnnotated(pixels, w, h, markers, maxDim: 400);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(Math.Max(decoded.Width, decoded.Height), Is.EqualTo(400), "the longest axis should be capped to maxDim");
    }

    [Test]
    public void EncodeGrayAnnotated_rejects_a_dimension_mismatch() {
        Assert.Throws<ArgumentException>(() =>
            JpegEncoder.EncodeGrayAnnotated(new byte[10], 4, 4, new List<StarMarker>()));
    }

    [Test]
    public void EncodeGrayAnnotated_rejects_null_markers() {
        Assert.Throws<ArgumentNullException>(() =>
            JpegEncoder.EncodeGrayAnnotated(new byte[16], 4, 4, null!));
    }
}
