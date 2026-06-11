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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using System;
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
        public async Task Stretch_with_a_brighter_factor_produces_a_brighter_median() {
            // Background-dominated frame so the median is well-defined.
            int w = 100, h = 100;
            var pixels = new ushort[w * h];
            for (int i = 0; i < pixels.Length - 50; i++) pixels[i] = (ushort)(i % 2 == 0 ? 8000 : 12000);
            for (int i = pixels.Length - 50; i < pixels.Length; i++) pixels[i] = 60000;
            var raw = new BaseImageData(pixels, w, h, 16, false, new ImageMetaData(), null!, null!, null!);
            var rendered = raw.RenderImage();

            var dim = await rendered.Stretch(factor: 0.1, blackClipping: -2.8, unlinked: false);
            var bright = await rendered.Stretch(factor: 0.5, blackClipping: -2.8, unlinked: false);

            // A 12000 (median) pixel is brighter under the higher target background.
            Assert.That(bright.Image[1], Is.GreaterThan(dim.Image[1]));
        }

        [Test]
        public void Debayer_produces_full_resolution_lrgb_planes() {
            int w = 8, h = 8;
            var raw = new BaseImageData(new ushort[w * h], w, h, 16, isBayered: true,
                new ImageMetaData(), null!, null!, null!);
            var rendered = raw.RenderImage();

            var debayered = rendered.Debayer(saveColorChannels: true, saveLumChannel: true, SensorType.RGGB);

            Assert.That(debayered.BayerPattern, Is.EqualTo(SensorType.RGGB));
            Assert.That(debayered.SaveColorChannels, Is.True);
            Assert.That(debayered.DebayeredData.Red.Length, Is.EqualTo(w * h));
            Assert.That(debayered.DebayeredData.Green.Length, Is.EqualTo(w * h));
            Assert.That(debayered.DebayeredData.Blue.Length, Is.EqualTo(w * h));
            Assert.That(debayered.DebayeredData.Lum.Length, Is.EqualTo(w * h));
        }

        [Test]
        public void Debayer_throws_for_an_unsupported_cfa() {
            int w = 4, h = 4;
            var raw = new BaseImageData(new ushort[w * h], w, h, 16, isBayered: true,
                new ImageMetaData(), null!, null!, null!);
            var rendered = raw.RenderImage();
            Assert.Throws<NotSupportedException>(() => rendered.Debayer(bayerPattern: SensorType.CMYG));
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
