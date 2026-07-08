#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using SkiaSharp;
using System.Collections.Generic;

namespace OpenAstroAra.Stretch;

/// <summary>A star marker to overlay on a preview: a circle centred at (<see cref="X"/>, <see cref="Y"/>)
/// with the given <see cref="Radius"/>, all in the pixel-buffer's coordinate space (the same space as the
/// pixels handed to the encoder — the caller is responsible for scaling detection coordinates into it).</summary>
public readonly record struct StarMarker(float X, float Y, float Radius);

/// <summary>
/// JPEG encoder for stretched grayscale pixels (§65). Takes the
/// 0–255 byte[] output of <see cref="Stretcher.Apply"/> + image
/// dimensions; returns a JPEG byte[].
///
/// SkiaSharp under the hood — cross-platform with native bins per
/// target RID. AOT-compatible from SkiaSharp 3.x.
/// </summary>
public static class JpegEncoder {
    /// <summary>
    /// Encode 8-bit grayscale pixels as a JPEG.
    /// </summary>
    /// <param name="pixels">Row-major 0–255 grayscale buffer; length must equal <paramref name="width"/> × <paramref name="height"/>.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="quality">JPEG quality 1–100; default 85.</param>
    /// <param name="maxDim">When &gt; 0 and the image exceeds it on either axis, the output is
    /// downscaled to fit (aspect-preserving) before encoding — caps the preview payload for very
    /// large sensors. 0 (default) keeps the full resolution, so existing callers are unaffected.</param>
    public static byte[] EncodeGray(ReadOnlySpan<byte> pixels, int width, int height, int quality = 85, int maxDim = 0) {
        if (width <= 0 || height <= 0) throw new ArgumentException("Dimensions must be positive");
        if (pixels.Length != width * height) {
            throw new ArgumentException(
                $"pixel buffer length ({pixels.Length}) doesn't match dimensions ({width}×{height} = {width * height})",
                nameof(pixels));
        }
        // Downscale-and-encode reuses the tested thumbnail resize path.
        if (maxDim > 0 && (width > maxDim || height > maxDim)) {
            return EncodeThumbnail(pixels, width, height, maxDim, quality);
        }

        // Skia's Gray8 format encodes single-channel byte buffers directly
        // without an RGB copy step.
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        var dst = bitmap.GetPixels();
        if (dst == IntPtr.Zero) throw new InvalidOperationException("Skia could not allocate the Gray8 bitmap backing buffer.");
        unsafe {
            fixed (byte* p = pixels) {
                // CopyPixels signature wants a pointer + length; we hand it
                // the unmanaged span directly.
                System.Buffer.MemoryCopy(p, (void*)dst, pixels.Length, pixels.Length);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    // §64/§59 star-marker overlay colour + stroke. Green reads clearly over the near-monochrome sky of a
    // stretched preview; a hollow (stroke-only) circle leaves the star itself visible inside the ring.
    private static readonly SKColor MarkerColor = new(0, 255, 0);
    private const float MarkerStrokeWidth = 2f;

    /// <summary>
    /// Encode 8-bit grayscale pixels as a JPEG with star-marker circles drawn over them (§64 Live View /
    /// §59 focus overlay). The grayscale is expanded to an RGB surface so the markers can be drawn in colour;
    /// markers are drawn at full resolution and, when <paramref name="maxDim"/> caps the output, the whole
    /// annotated image is downscaled together so the circles stay aligned with the stars.
    /// </summary>
    /// <param name="pixels">Row-major 0–255 grayscale buffer; length must equal <paramref name="width"/> × <paramref name="height"/>.</param>
    /// <param name="markers">Star markers in the same pixel space as <paramref name="pixels"/>; a zero/negative radius is skipped.</param>
    /// <param name="maxDim">When &gt; 0 and the image exceeds it on either axis, the annotated output is downscaled to fit (aspect-preserving).</param>
    public static byte[] EncodeGrayAnnotated(ReadOnlySpan<byte> pixels, int width, int height,
            IReadOnlyList<StarMarker> markers, int quality = 85, int maxDim = 0) {
        if (width <= 0 || height <= 0) throw new ArgumentException("Dimensions must be positive");
        if (pixels.Length != width * height) {
            throw new ArgumentException(
                $"pixel buffer length ({pixels.Length}) doesn't match dimensions ({width}×{height} = {width * height})",
                nameof(pixels));
        }
        ArgumentNullException.ThrowIfNull(markers);

        using var bitmap = GrayToRgbaBitmap(pixels, width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint {
            Color = MarkerColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MarkerStrokeWidth,
            IsAntialias = true,
        }) {
            foreach (var m in markers) {
                if (m.Radius > 0) {
                    canvas.DrawCircle(m.X, m.Y, m.Radius, paint);
                }
            }
        }

        if (maxDim > 0 && (width > maxDim || height > maxDim)) {
            var (dstW, dstH) = ScaleToFit(width, height, maxDim);
            var dstInfo = new SKImageInfo(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var resized = bitmap.Resize(dstInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new InvalidOperationException($"Skia failed to resize {width}×{height} → {dstW}×{dstH} annotated preview");
            using var scaledImage = SKImage.FromBitmap(resized);
            using var scaledData = scaledImage.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return scaledData.ToArray();
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    // Expand a single-channel grayscale buffer into an opaque Rgba8888 SKBitmap (r=g=b=gray, a=255) so a
    // colour overlay can be drawn onto it.
    private static SKBitmap GrayToRgbaBitmap(ReadOnlySpan<byte> pixels, int width, int height) {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var pixelPtr = bitmap.GetPixels();
        if (pixelPtr == IntPtr.Zero) {
            bitmap.Dispose();
            throw new InvalidOperationException("Skia could not allocate the Rgba8888 bitmap backing buffer.");
        }
        unsafe {
            byte* dst = (byte*)pixelPtr;
            int n = width * height;
            for (int i = 0; i < n; i++) {
                byte g = pixels[i];
                int d = i * 4;
                dst[d] = g;
                dst[d + 1] = g;
                dst[d + 2] = g;
                dst[d + 3] = 255;
            }
        }
        return bitmap;
    }

    /// <summary>
    /// Resize 8-bit grayscale pixels to a target maximum dimension, preserving
    /// aspect ratio, then encode as JPEG. Used for thumbnails per §40.5.
    /// </summary>
    public static byte[] EncodeThumbnail(ReadOnlySpan<byte> pixels, int srcWidth, int srcHeight, int maxDim = 320, int quality = 75) {
        if (srcWidth <= 0 || srcHeight <= 0) throw new ArgumentException("Dimensions must be positive");
        if (pixels.Length != srcWidth * srcHeight) {
            throw new ArgumentException(
                $"pixel buffer length ({pixels.Length}) doesn't match dimensions ({srcWidth}×{srcHeight} = {srcWidth * srcHeight})",
                nameof(pixels));
        }

        var (dstW, dstH) = ScaleToFit(srcWidth, srcHeight, maxDim);

        var srcInfo = new SKImageInfo(srcWidth, srcHeight, SKColorType.Gray8, SKAlphaType.Opaque);
        using var srcBitmap = new SKBitmap(srcInfo);
        var src = srcBitmap.GetPixels();
        if (src == IntPtr.Zero) throw new InvalidOperationException("Skia could not allocate the Gray8 bitmap backing buffer.");
        unsafe {
            fixed (byte* p = pixels) {
                System.Buffer.MemoryCopy(p, (void*)src, pixels.Length, pixels.Length);
            }
        }

        var dstInfo = new SKImageInfo(dstW, dstH, SKColorType.Gray8, SKAlphaType.Opaque);
        using var dstBitmap = srcBitmap.Resize(dstInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException($"Skia failed to resize {srcWidth}×{srcHeight} → {dstW}×{dstH} thumbnail");
        using var image = SKImage.FromBitmap(dstBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    /// <summary>
    /// Encode interleaved 8-bit RGB pixels (3 bytes/pixel, R,G,B order) as a JPEG. Used for the
    /// debayered §65 color preview of OSC frames.
    /// </summary>
    /// <param name="rgb">Row-major interleaved R,G,B bytes; length must equal width × height × 3.</param>
    /// <param name="maxDim">When &gt; 0 and the image exceeds it on either axis, the output is
    /// downscaled to fit (aspect-preserving) before encoding. 0 (default) keeps full resolution.</param>
    public static byte[] EncodeColor(ReadOnlySpan<byte> rgb, int width, int height, int quality = 85, int maxDim = 0) {
        if (width <= 0 || height <= 0) throw new ArgumentException("Dimensions must be positive");
        if (rgb.Length != width * height * 3) {
            throw new ArgumentException(
                $"RGB buffer length ({rgb.Length}) doesn't match {width}×{height}×3 = {width * height * 3}",
                nameof(rgb));
        }
        if (maxDim > 0 && (width > maxDim || height > maxDim)) {
            return EncodeColorThumbnail(rgb, width, height, maxDim, quality);
        }
        using var bitmap = RgbToBitmap(rgb, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    /// <summary>Resize interleaved RGB to a max dimension (aspect-preserving) and encode as JPEG.</summary>
    public static byte[] EncodeColorThumbnail(ReadOnlySpan<byte> rgb, int srcWidth, int srcHeight, int maxDim = 320, int quality = 75) {
        if (srcWidth <= 0 || srcHeight <= 0) throw new ArgumentException("Dimensions must be positive");
        if (rgb.Length != srcWidth * srcHeight * 3) {
            throw new ArgumentException(
                $"RGB buffer length ({rgb.Length}) doesn't match {srcWidth}×{srcHeight}×3 = {srcWidth * srcHeight * 3}",
                nameof(rgb));
        }
        var (dstW, dstH) = ScaleToFit(srcWidth, srcHeight, maxDim);
        using var srcBitmap = RgbToBitmap(rgb, srcWidth, srcHeight);
        var dstInfo = new SKImageInfo(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var dstBitmap = srcBitmap.Resize(dstInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException($"Skia failed to resize {srcWidth}×{srcHeight} → {dstW}×{dstH} thumbnail");
        using var image = SKImage.FromBitmap(dstBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    // Expand interleaved RGB (3 B/px) into an opaque Rgba8888 SKBitmap (4 B/px, A=255).
    private static SKBitmap RgbToBitmap(ReadOnlySpan<byte> rgb, int width, int height) {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var pixelPtr = bitmap.GetPixels();
        if (pixelPtr == IntPtr.Zero) {
            bitmap.Dispose();
            throw new InvalidOperationException("Skia could not allocate the Rgba8888 bitmap backing buffer.");
        }
        unsafe {
            byte* dst = (byte*)pixelPtr;
            int n = width * height;
            for (int i = 0, s = 0; i < n; i++, s += 3) {
                int d = i * 4;
                dst[d] = rgb[s];
                dst[d + 1] = rgb[s + 1];
                dst[d + 2] = rgb[s + 2];
                dst[d + 3] = 255;
            }
        }
        return bitmap;
    }

    private static (int W, int H) ScaleToFit(int srcW, int srcH, int maxDim) {
        if (srcW <= maxDim && srcH <= maxDim) return (srcW, srcH);
        double scale = (double)maxDim / Math.Max(srcW, srcH);
        return (Math.Max(1, (int)Math.Round(srcW * scale)),
                Math.Max(1, (int)Math.Round(srcH * scale)));
    }
}