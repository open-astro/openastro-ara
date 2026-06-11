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
using OpenAstroAra.Image.ImageData;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §2105 PR1 — <see cref="BaseImageData.RenderBitmapSource"/> renders a raw 16-bit frame to an
    /// 8-bit display buffer through the §65 <see cref="Stretcher"/> (SkiaSharp-era; no OpenCvSharp4).
    /// The detection deps aren't touched by the render path, so null is fine here.
    /// </summary>
    [TestFixture]
    public class RenderBitmapSourceTest {

        private static BaseImageData NewImageData(ushort[] pixels, int w, int h) =>
            new(pixels, w, h, bitDepth: 16, isBayered: false, new ImageMetaData(), null!, null!, null!);

        [Test]
        public void RenderBitmapSource_returns_one_byte_per_pixel() {
            int w = 32, h = 4;
            var data = NewImageData(new ushort[w * h], w, h);
            var rendered = data.RenderBitmapSource();
            Assert.That(rendered.Length, Is.EqualTo(w * h));
        }

        [Test]
        public void RenderBitmapSource_matches_the_auto_stf_stretch_of_the_raw_pixels() {
            int w = 64, h = 4, n = w * h;
            var pixels = new ushort[n];
            for (int i = 0; i < n; i++) {
                pixels[i] = (ushort)((long)i * 65535 / (n - 1)); // 0..65535 gradient
            }
            var data = NewImageData(pixels, w, h);

            var rendered = data.RenderBitmapSource();

            // Assert real stretch behavior (not a circular re-call of the implementation): the gradient
            // is spread across the dynamic range — darkest ≈ 0, brightest ≈ 255 — and monotonic
            // non-decreasing. A throwing/unimplemented path fails the first line; a no-op would not
            // reach full range.
            Assert.That(rendered[0], Is.LessThanOrEqualTo(8));
            Assert.That(rendered[^1], Is.GreaterThanOrEqualTo(200));
            for (int i = 1; i < rendered.Length; i++) {
                Assert.That(rendered[i], Is.GreaterThanOrEqualTo(rendered[i - 1]));
            }
        }
    }
}
