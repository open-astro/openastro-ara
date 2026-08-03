#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// Pixel layouts supported by the SER container (§77.1). SER encodes layout as a
    /// ColorID plus a separate per-plane bit depth; <see cref="VideoFormats"/> derives
    /// both from one of these tokens.
    /// </summary>
    public enum VideoPixelFormat {
        Mono8,
        Mono16,
        BayerRggb8,
        BayerGrbg8,
        BayerGbrg8,
        BayerBggr8,
        BayerRggb16,
        BayerGrbg16,
        BayerGbrg16,
        BayerBggr16,
        Rgb24
    }

    public static class VideoFormats {

        public static int BitsPerPlane(VideoPixelFormat format) => format switch {
            VideoPixelFormat.Mono16 or VideoPixelFormat.BayerRggb16 or VideoPixelFormat.BayerGrbg16
                or VideoPixelFormat.BayerGbrg16 or VideoPixelFormat.BayerBggr16 => 16,
            _ => 8
        };

        public static int PlaneCount(VideoPixelFormat format) =>
            format == VideoPixelFormat.Rgb24 ? 3 : 1;

        /// <summary>SER ColorID token (MONO=0, BAYER_RGGB..BGGR=8..11, RGB=100).</summary>
        public static int SerColorId(VideoPixelFormat format) => format switch {
            VideoPixelFormat.BayerRggb8 or VideoPixelFormat.BayerRggb16 => 8,
            VideoPixelFormat.BayerGrbg8 or VideoPixelFormat.BayerGrbg16 => 9,
            VideoPixelFormat.BayerGbrg8 or VideoPixelFormat.BayerGbrg16 => 10,
            VideoPixelFormat.BayerBggr8 or VideoPixelFormat.BayerBggr16 => 11,
            VideoPixelFormat.Rgb24 => 100,
            _ => 0
        };

        /// <summary>Bytes of one frame (width × height × planes × bytes/plane); 0 when the geometry is invalid.</summary>
        public static long FrameBytes(in VideoRequest request) {
            if (request.Width <= 0 || request.Height <= 0) {
                return 0;
            }
            return (long)request.Width * request.Height *
                   PlaneCount(request.Format) * (BitsPerPlane(request.Format) / 8);
        }
    }

    /// <summary>Geometry + exposure settings for one video-mode session (§77.1).</summary>
    public readonly record struct VideoRequest(
        int StartX,
        int StartY,
        int Width,
        int Height,
        int Bin,
        VideoPixelFormat Format,
        long Gain,
        int ExposureMs) {

        public VideoRequest() : this(0, 0, 0, 0, 1, VideoPixelFormat.Mono8, 0, 10) {
        }
    }

    /// <summary>
    /// The §77.1 per-vendor video seam (~30 lines): one P/Invoke implementation per camera
    /// SDK; the ring buffer / SER writer / preview tap sit on top, vendor-agnostic.
    ///
    /// Pull model: the recorder's capture thread calls <see cref="GetFrame"/> in a tight
    /// loop. Pull matches the ZWO / SVBONY / Player One SDKs directly; callback-push SDKs
    /// (ToupTek) adapt with an internal single-frame handoff inside their glue.
    /// </summary>
    public interface IVideoCapture : IDisposable {

        /// <summary>
        /// Configure the device and enter video mode. Throws <see cref="VideoCaptureException"/>
        /// on vendor failure. Calling while started is an error.
        /// </summary>
        void Start(in VideoRequest request);

        /// <summary>
        /// Block up to <paramref name="timeoutMs"/> for the next frame and copy it into
        /// <paramref name="buffer"/> (length must be ≥ FrameBytes of the started request).
        /// True when a frame was delivered; false on timeout. Throws on vendor failure.
        /// </summary>
        bool GetFrame(Span<byte> buffer, int timeoutMs);

        /// <summary>Leave video mode. Safe to call when already stopped.</summary>
        void StopCapture();

        /// <summary>
        /// Frames the vendor SDK itself dropped since Start (device/USB side — distinct
        /// from ring drops, which the recorder counts). Vendors without a counter return 0.
        /// </summary>
        ulong DroppedFrames { get; }
    }

    /// <summary>Vendor/IO failure inside the §77 video path.</summary>
    public class VideoCaptureException : Exception {

        public VideoCaptureException() {
        }

        public VideoCaptureException(string message) : base(message) {
        }

        public VideoCaptureException(string message, Exception innerException) : base(message, innerException) {
        }
    }
}
