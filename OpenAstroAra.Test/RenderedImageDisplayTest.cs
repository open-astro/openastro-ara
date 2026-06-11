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
using OpenAstroAra.Image.Interfaces;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §2105 PR2 — <see cref="RenderedImage.GetThumbnail"/> + <see cref="RenderedImage.ReRender"/>,
    /// the thin §65 display wrappers (JpegEncoder.EncodeThumbnail / re-render from raw). The detection
    /// deps aren't touched by these paths, so null is fine here.
    /// </summary>
    [TestFixture]
    public class RenderedImageDisplayTest {

        private static IRenderedImage NewRendered(int w, int h) {
            var pixels = new ushort[w * h];
            var denom = System.Math.Max(1, pixels.Length - 1);
            for (int i = 0; i < pixels.Length; i++) {
                pixels[i] = (ushort)((long)i * 65535 / denom);
            }
            var raw = new BaseImageData(pixels, w, h, bitDepth: 16, isBayered: false,
                new ImageMetaData(), null!, null!, null!);
            return raw.RenderImage();
        }

        [Test]
        public async Task GetThumbnail_returns_a_valid_jpeg() {
            var rendered = NewRendered(640, 480);
            var jpeg = await rendered.GetThumbnail();
            // JPEG: SOI 0xFFD8 … EOI 0xFFD9.
            Assert.That(jpeg.Length, Is.GreaterThan(4));
            Assert.That(jpeg[0], Is.EqualTo(0xFF));
            Assert.That(jpeg[1], Is.EqualTo(0xD8));
            Assert.That(jpeg[^2], Is.EqualTo(0xFF));
            Assert.That(jpeg[^1], Is.EqualTo(0xD9));
        }

        [Test]
        public void ReRender_returns_a_fresh_image_of_the_same_dimensions() {
            int w = 32, h = 8;
            var rendered = NewRendered(w, h);
            var reRendered = rendered.ReRender();
            Assert.That(reRendered, Is.Not.Null);
            // RenderBitmapSource is one byte/pixel, so the rendered buffer length == pixel count.
            Assert.That(reRendered.OriginalImage.Length, Is.EqualTo(w * h));
        }
    }
}
