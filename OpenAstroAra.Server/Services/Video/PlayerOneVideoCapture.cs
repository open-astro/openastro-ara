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
    /// §77.1 Player One glue behind the <see cref="IVideoCapture"/> seam
    /// (POAOpenCamera → POAStartExposure(continuous) → POAGetImageData →
    /// POAStopExposure). Same §77.2 hand-off contract and retry-open shape as the
    /// ZWO glue.
    /// </summary>
    public sealed class PlayerOneVideoCapture : IVideoCapture {
        private static readonly TimeSpan OpenRetryWindow = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OpenRetryInterval = TimeSpan.FromMilliseconds(250);

        private readonly int cameraId;
        private bool opened;
        private bool started;

        public PlayerOneVideoCapture(int cameraId) {
            this.cameraId = cameraId;
        }

        public ulong DroppedFrames {
            get {
                if (!started) {
                    return 0;
                }
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.GetDroppedImagesCount(cameraId, out var dropped), "POAGetDroppedImagesCount");
                return dropped > 0 ? (ulong)dropped : 0;
            }
        }

        public void Start(in VideoRequest request) {
            if (started) {
                throw new VideoCaptureException("PlayerOneVideoCapture: already started");
            }
            if (VideoFormats.FrameBytes(request) <= 0) {
                throw new VideoCaptureException("PlayerOneVideoCapture: invalid frame geometry");
            }

            OpenWithRetry();
            try {
                PlayerOneNative.ThrowOnError(PlayerOneNative.InitCamera(cameraId), "POAInitCamera");
                PlayerOneNative.ThrowOnError(PlayerOneNative.SetImageBin(cameraId, request.Bin), "POASetImageBin");
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.SetImageSize(cameraId, request.Width, request.Height), "POASetImageSize");
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.SetImageStartPos(cameraId, request.StartX, request.StartY), "POASetImageStartPos");
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.SetImageFormat(cameraId, ToPoaFormat(request.Format)), "POASetImageFormat");
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.SetConfig(cameraId, PlayerOneNative.PoaConfig.Gain,
                        new PlayerOneNative.PoaConfigValue { IntValue = request.Gain }, 0),
                    "POASetConfig(Gain)");
                PlayerOneNative.ThrowOnError(
                    PlayerOneNative.SetConfig(cameraId, PlayerOneNative.PoaConfig.Exposure,
                        new PlayerOneNative.PoaConfigValue { IntValue = request.ExposureMs * 1000L }, 0),
                    "POASetConfig(Exposure)");
                // Video-rate throttles, same rationale as the ZWO glue: max the USB
                // bandwidth and ensure no frame-rate limit. Best-effort.
                _ = PlayerOneNative.SetConfig(cameraId, PlayerOneNative.PoaConfig.UsbBandwidthLimit,
                    new PlayerOneNative.PoaConfigValue { IntValue = 100 }, 0);
                _ = PlayerOneNative.SetConfig(cameraId, PlayerOneNative.PoaConfig.FrameLimit,
                    new PlayerOneNative.PoaConfigValue { IntValue = 0 }, 0);
                // singleFrame = 0 → continuous video mode.
                PlayerOneNative.ThrowOnError(PlayerOneNative.StartExposure(cameraId, 0), "POAStartExposure");
                started = true;
            } catch {
                _ = PlayerOneNative.CloseCamera(cameraId);
                opened = false;
                throw;
            }
        }

        private void OpenWithRetry() {
            var deadline = DateTime.UtcNow + OpenRetryWindow;
            while (true) {
                // Same SDK contract as ZWO: ids are only valid after an enumeration
                // pass in this process.
                var count = PlayerOneNative.GetCameraCount();
                var code = count <= 0
                    ? PlayerOneNative.PoaErrorCode.DeviceNotFound
                    : PlayerOneNative.OpenCamera(cameraId);
                if (code == PlayerOneNative.PoaErrorCode.Ok) {
                    opened = true;
                    return;
                }
                var retryable = code is PlayerOneNative.PoaErrorCode.DeviceNotFound
                    or PlayerOneNative.PoaErrorCode.Timeout
                    or PlayerOneNative.PoaErrorCode.Exposing
                    or PlayerOneNative.PoaErrorCode.ExposureFailed;
                if (!retryable) {
                    throw new VideoCaptureException(
                        $"Player One camera {cameraId} failed to open for video mode: POA_ERROR_{code}. " +
                        $"Enumerated ids: [{EnumerateIds(count)}]");
                }
                if (DateTime.UtcNow >= deadline) {
                    throw new VideoCaptureException(
                        $"Player One camera {cameraId} could not be opened for video mode after " +
                        $"{OpenRetryWindow.TotalSeconds:0}s — it is likely still held by AlpacaBridge. " +
                        $"Disconnect it from the Alpaca surface first (§77.2). Last SDK error: POA_ERROR_{code}");
                }
                Thread.Sleep(OpenRetryInterval);
            }
        }

        public bool GetFrame(Span<byte> buffer, int timeoutMs) {
            if (!started) {
                throw new VideoCaptureException("PlayerOneVideoCapture: not started");
            }
            var code = PlayerOneNative.GetImageData(
                cameraId, ref MemoryMarshal.GetReference(buffer), buffer.Length, timeoutMs);
            if (code == PlayerOneNative.PoaErrorCode.Timeout) {
                return false;
            }
            PlayerOneNative.ThrowOnError(code, "POAGetImageData");
            return true;
        }

        public void StopCapture() {
            if (started) {
                _ = PlayerOneNative.StopExposure(cameraId);
                started = false;
            }
            if (opened) {
                _ = PlayerOneNative.CloseCamera(cameraId);
                opened = false;
            }
        }

        public void Dispose() => StopCapture();

        private static string EnumerateIds(int count) {
            var ids = new System.Collections.Generic.List<string>();
            for (var i = 0; i < count; i++) {
                var id = PlayerOneNative.GetCameraIdAtIndex(i);
                if (id is not null) {
                    ids.Add(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return string.Join(", ", ids);
        }

        private static PlayerOneNative.PoaImgFormat ToPoaFormat(VideoPixelFormat format) => format switch {
            VideoPixelFormat.Rgb24 => PlayerOneNative.PoaImgFormat.Rgb24,
            VideoPixelFormat.Mono16 or VideoPixelFormat.BayerRggb16 or VideoPixelFormat.BayerGrbg16
                or VideoPixelFormat.BayerGbrg16 or VideoPixelFormat.BayerBggr16 => PlayerOneNative.PoaImgFormat.Raw16,
            _ => PlayerOneNative.PoaImgFormat.Raw8
        };
    }
}
