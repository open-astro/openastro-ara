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
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// §77.1 ZWO glue behind the vendor-neutral <see cref="IVideoCapture"/> seam
    /// (ASIOpenCamera → ASIStartVideoCapture → ASIGetVideoData → ASIStopVideoCapture).
    ///
    /// §77.2 hand-off contract: the caller has already PUT `Connected=false` to
    /// AlpacaBridge for this camera. The bridge's driver contract closes the SDK handle
    /// inside that call, but a slow close can defer the release briefly — so
    /// <see cref="Start"/> retries the SDK open for up to <see cref="OpenRetryWindow"/>
    /// and reports a clear "camera still held by the bridge" error rather than a raw
    /// vendor code.
    /// </summary>
    public sealed class ZwoVideoCapture : IVideoCapture {
        private static readonly TimeSpan OpenRetryWindow = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OpenRetryInterval = TimeSpan.FromMilliseconds(250);

        private readonly int cameraId;
        private bool opened;
        private bool started;

        public ZwoVideoCapture(int cameraId) {
            this.cameraId = cameraId;
        }

        public ulong DroppedFrames {
            get {
                if (!started) {
                    return 0;
                }
                ZwoNative.ThrowOnError(ZwoNative.GetDroppedFrames(cameraId, out var dropped), "ASIGetDroppedFrames");
                return dropped > 0 ? (ulong)dropped : 0;
            }
        }

        public void Start(in VideoRequest request) {
            if (started) {
                throw new VideoCaptureException("ZwoVideoCapture: already started");
            }
            if (VideoFormats.FrameBytes(request) <= 0) {
                throw new VideoCaptureException("ZwoVideoCapture: invalid frame geometry");
            }

            OpenWithRetry();
            try {
                ZwoNative.ThrowOnError(ZwoNative.InitCamera(cameraId), "ASIInitCamera");
                // ZWO ROI divisors (width % 8, height % 2 after binning) are the caller's
                // contract — this glue passes SDK-ready geometry straight through.
                ZwoNative.ThrowOnError(
                    ZwoNative.SetRoiFormat(cameraId, request.Width, request.Height, request.Bin, ToAsiImgType(request.Format)),
                    "ASISetROIFormat");
                ZwoNative.ThrowOnError(ZwoNative.SetStartPos(cameraId, request.StartX, request.StartY), "ASISetStartPos");
                ZwoNative.ThrowOnError(
                    ZwoNative.SetControlValue(cameraId, ZwoNative.AsiControlType.Gain, request.Gain, 0),
                    "ASISetControlValue(Gain)");
                ZwoNative.ThrowOnError(
                    ZwoNative.SetControlValue(cameraId, ZwoNative.AsiControlType.Exposure, request.ExposureMs * 1000L, 0),
                    "ASISetControlValue(Exposure)");
                ZwoNative.ThrowOnError(ZwoNative.StartVideoCapture(cameraId), "ASIStartVideoCapture");
                started = true;
            } catch {
                // Any post-open failure must not leak the exclusive USB handle.
                _ = ZwoNative.CloseCamera(cameraId);
                opened = false;
                throw;
            }
        }

        private void OpenWithRetry() {
            var deadline = DateTime.UtcNow + OpenRetryWindow;
            while (true) {
                // The ASI SDK only accepts ASIOpenCamera for ids surfaced by a prior
                // enumeration pass in THIS process — an un-enumerated id returns
                // InvalidId even for a present camera (found on-hardware, rc91 spike).
                var count = ZwoNative.GetNumOfConnectedCameras();
                if (count <= 0) {
                    ThrowOrRetry(ZwoNative.AsiErrorCode.CameraRemoved, deadline,
                        "no ZWO cameras enumerated — is the camera connected and free?");
                    continue;
                }
                var code = ZwoNative.OpenCamera(cameraId);
                if (code == ZwoNative.AsiErrorCode.Success) {
                    opened = true;
                    return;
                }
                // Only the codes a bridge-still-holding-the-camera or a settling USB
                // stack can produce are worth waiting out; a bad camera id or a
                // physically absent camera fails fast with the real SDK error.
                var retryable = code is ZwoNative.AsiErrorCode.CameraClosed
                    or ZwoNative.AsiErrorCode.CameraRemoved
                    or ZwoNative.AsiErrorCode.GeneralError
                    or ZwoNative.AsiErrorCode.Timeout;
                if (!retryable) {
                    throw new VideoCaptureException(
                        $"ZWO camera {cameraId} failed to open for video mode: ASI_ERROR_{code}. " +
                        $"Enumerated ids: [{EnumerateIds(count)}]");
                }
                ThrowOrRetry(code, deadline,
                    $"it is likely still held by AlpacaBridge — disconnect it from the Alpaca surface first (§77.2)");
            }
        }

        private void ThrowOrRetry(ZwoNative.AsiErrorCode code, DateTime deadline, string hint) {
            if (DateTime.UtcNow >= deadline) {
                throw new VideoCaptureException(
                    $"ZWO camera {cameraId} could not be opened for video mode after " +
                    $"{OpenRetryWindow.TotalSeconds:0}s — {hint}. Last SDK error: ASI_ERROR_{code}");
            }
            Thread.Sleep(OpenRetryInterval);
        }

        private static string EnumerateIds(int count) {
            var ids = new System.Collections.Generic.List<string>();
            for (var i = 0; i < count; i++) {
                var id = ZwoNative.GetCameraIdAtIndex(i);
                if (id is not null) {
                    ids.Add(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return string.Join(", ", ids);
        }

        public bool GetFrame(Span<byte> buffer, int timeoutMs) {
            if (!started) {
                throw new VideoCaptureException("ZwoVideoCapture: not started");
            }
            var code = ZwoNative.GetVideoData(cameraId, ref MemoryMarshal.GetReference(buffer), buffer.Length, timeoutMs);
            if (code == ZwoNative.AsiErrorCode.Timeout) {
                return false;
            }
            ZwoNative.ThrowOnError(code, "ASIGetVideoData");
            return true;
        }

        public void StopCapture() {
            if (started) {
                _ = ZwoNative.StopVideoCapture(cameraId);
                started = false;
            }
            if (opened) {
                _ = ZwoNative.CloseCamera(cameraId);
                opened = false;
            }
        }

        public void Dispose() => StopCapture();

        private static ZwoNative.AsiImgType ToAsiImgType(VideoPixelFormat format) => format switch {
            VideoPixelFormat.Rgb24 => ZwoNative.AsiImgType.Rgb24,
            VideoPixelFormat.Mono16 or VideoPixelFormat.BayerRggb16 or VideoPixelFormat.BayerGrbg16
                or VideoPixelFormat.BayerGbrg16 or VideoPixelFormat.BayerBggr16 => ZwoNative.AsiImgType.Raw16,
            _ => ZwoNative.AsiImgType.Raw8
        };
    }
}
