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

namespace OpenAstroAra.Stretch;

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