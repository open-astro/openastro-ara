#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"
using System;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>
    /// #1147 — the headless <see cref="IStarDetection"/>: the dependency-free <see cref="StarDetector"/> over
    /// the rendered image's RAW 16-bit frame. Stateless; use <see cref="Instance"/>.
    /// </summary>
    public sealed class HeadlessStarDetection : IStarDetection {
        public static readonly HeadlessStarDetection Instance = new();

        public Task<StarDetectionResult> Detect(IRenderedImage image, string format,
                StarDetectionParams parameters, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var raw = image.RawImageData;
            var pixels = raw.Data.FlatArray;
            var width = raw.Properties.Width;
            var height = raw.Properties.Height;
            return Task.Run(() => StarDetector.Detect(pixels, width, height, parameters, token), token);
        }
    }

    /// <summary>
    /// #1147 — the headless <see cref="IStarAnnotator"/>: draws each detected star as a white circle (radius
    /// ≈ 2 × its HFR, at least <see cref="MinRadiusPx"/>) onto a COPY of the 8-bit display buffer, so the
    /// un-annotated render survives. Pure byte maths, no bitmap library. Stateless; use <see cref="Instance"/>.
    /// </summary>
    public sealed class CircleStarAnnotator : IStarAnnotator {
        public static readonly CircleStarAnnotator Instance = new();
        public const int MinRadiusPx = 3;
        public const byte Ink = 255;

        public Task<byte[]> GetAnnotatedImage(StarDetectionParams parameters, StarDetectionResult result,
                byte[] source, int width, int height) =>
            Task.FromResult(Annotate(result, source, width, height));

        /// <summary>The synchronous core, exposed for tests.</summary>
        public static byte[] Annotate(StarDetectionResult result, byte[] source, int width, int height) {
            if (source.Length != width * height) {
                throw new ArgumentException($"Display buffer has {source.Length} bytes for {width}×{height}.", nameof(source));
            }
            var annotated = (byte[])source.Clone();
            foreach (var star in result.StarList) {
                var (cx, cy) = star.Unpack(width);
                var radius = Math.Max(MinRadiusPx, (int)Math.Round(2 * star.HFR));
                DrawCircle(annotated, width, height, cx, cy, radius);
            }
            return annotated;
        }

        // Midpoint circle: eight-way symmetric points, clipped at the frame edge.
        private static void DrawCircle(byte[] buffer, int width, int height, int cx, int cy, int radius) {
            int x = radius, y = 0, err = 1 - radius;
            while (x >= y) {
                Plot(buffer, width, height, cx + x, cy + y); Plot(buffer, width, height, cx - x, cy + y);
                Plot(buffer, width, height, cx + x, cy - y); Plot(buffer, width, height, cx - x, cy - y);
                Plot(buffer, width, height, cx + y, cy + x); Plot(buffer, width, height, cx - y, cy + x);
                Plot(buffer, width, height, cx + y, cy - x); Plot(buffer, width, height, cx - y, cy - x);
                y++;
                if (err < 0) {
                    err += 2 * y + 1;
                } else {
                    x--;
                    err += 2 * (y - x) + 1;
                }
            }
        }

        private static void Plot(byte[] buffer, int width, int height, int x, int y) {
            if (x >= 0 && y >= 0 && x < width && y < height) {
                buffer[y * width + x] = Ink;
            }
        }
    }
}
