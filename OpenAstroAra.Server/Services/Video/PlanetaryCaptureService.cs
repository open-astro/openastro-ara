#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// §77.2/§77.4 planetary-mode orchestrator. A camera cannot be in Alpaca
    /// still-imaging and SDK video mode at once, and the gate needs no bridge API:
    /// entering planetary mode PUTs Connected=false to AlpacaBridge (via the camera
    /// service) and opens the camera natively; leaving closes the SDK handle and
    /// reconnects the Alpaca device. Entering is refused while a sequence run is
    /// active. usbfs is auto-tuned on entry (§77.1, best-effort). One camera at a
    /// time — planetary sessions are operator-attended and exclusive by design.
    /// </summary>
    public sealed partial class PlanetaryCaptureService : IDisposable {
        private readonly ILogger logger;
        private readonly ILogger<VideoRecorder> recorderLogger;
        private readonly ICameraService camera;
        private readonly ActiveRunSessionRegistry runs;
        private readonly IWsBroadcaster? ws;
        private readonly IProfileStore? profileStore;
        private readonly UsbfsTuner tuner;
        private readonly Func<int, IVideoCapture> captureFactory;
        private readonly Func<DiscoveredDeviceDto?> lastDeviceProvider;
        private readonly SemaphoreSlim gate = new(1, 1);

        private bool inPlanetaryMode;
        private int? cameraId;
        private int? usbfsMb;
        private IVideoCapture? capture;
        private VideoRecorder? recorder;
        private string? outputPath;
        private bool disposed;

        public PlanetaryCaptureService(
            ILogger<PlanetaryCaptureService> logger,
            ILogger<VideoRecorder> recorderLogger,
            ICameraService camera,
            ActiveRunSessionRegistry runs,
            IWsBroadcaster? ws,
            IProfileStore? profileStore,
            UsbfsTuner tuner,
            Func<int, IVideoCapture>? captureFactory = null,
            Func<DiscoveredDeviceDto?>? lastDeviceProvider = null) {
            this.logger = logger;
            this.recorderLogger = recorderLogger;
            this.camera = camera;
            this.runs = runs;
            this.ws = ws;
            this.profileStore = profileStore;
            this.tuner = tuner;
            this.captureFactory = captureFactory ?? (id => new ZwoVideoCapture(id));
            this.lastDeviceProvider = lastDeviceProvider ?? (() => (camera as CameraService)?.LastKnownDevice);
        }

        /// <summary>
        /// §77.2 enter: refuse while a sequence run is active (the sequence holds the
        /// camera); otherwise detach the camera from the Alpaca surface and auto-tune
        /// usbfs. Idempotent while already in planetary mode for the same camera.
        /// </summary>
        public async Task<OperationAcceptedDto> EnterAsync(PlanetaryEnterRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (inPlanetaryMode) {
                    if (cameraId == request.CameraId) {
                        return Accepted("planetary.enter", idempotencyKey);
                    }
                    throw new InvalidOperationException(
                        $"already in planetary mode with camera {cameraId}; leave first");
                }
                if (runs.HasAny) {
                    throw new InvalidOperationException(
                        "a sequence run is active — it holds the camera (§77.2); stop it before entering planetary mode");
                }

                // Detach from the Alpaca surface (bridge closes the SDK handle in-call
                // per its driver contract; ZwoVideoCapture's retry-open absorbs a slow
                // close). 202 fire-and-forget is fine — the retry-open is the sync point.
                await camera.DisconnectAsync(idempotencyKey: null, ct).ConfigureAwait(false);

                usbfsMb = await tuner.AutoTuneAsync(request.UsbfsOverrideMb, ct).ConfigureAwait(false)
                          ?? UsbfsTuner.ReadCurrentMb();

                inPlanetaryMode = true;
                cameraId = request.CameraId;
                await PublishAsync(WsEventCatalog.PlanetaryModeEntered, new JsonObject {
                    ["camera_id"] = request.CameraId,
                    ["usbfs_memory_mb"] = usbfsMb,
                }).ConfigureAwait(false);
                LogEntered(logger, request.CameraId, usbfsMb ?? 0);
                return Accepted("planetary.enter", idempotencyKey);
            } finally {
                gate.Release();
            }
        }

        /// <summary>
        /// §77.2 leave: stop any recording, close the SDK handle, and reconnect the
        /// Alpaca device (best-effort — the last-connected device is re-used).
        /// Idempotent when not in planetary mode.
        /// </summary>
        public async Task<OperationAcceptedDto> LeaveAsync(string? idempotencyKey, CancellationToken ct) {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!inPlanetaryMode) {
                    return Accepted("planetary.leave", idempotencyKey);
                }
                StopRecordingLocked(publish: true);
                // The capture instance is constructor-bound to this camera id — a later
                // Enter with a different camera must get a fresh one (review #911 r1).
                capture?.Dispose();
                capture = null;
                inPlanetaryMode = false;
                var leftCamera = cameraId;
                cameraId = null;

                var device = lastDeviceProvider();
                if (device is not null) {
                    try {
                        await camera.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, ct).ConfigureAwait(false);
                    } catch (InvalidOperationException ex) {
                        // Best-effort restore: the user can reconnect via the normal
                        // equipment flow; leaving planetary mode must still succeed.
                        LogReconnectFailed(logger, ex);
                    }
                }
                await PublishAsync(WsEventCatalog.PlanetaryModeLeft, new JsonObject {
                    ["camera_id"] = leftCamera,
                    ["alpaca_reconnect_attempted"] = device is not null,
                }).ConfigureAwait(false);
                LogLeft(logger, leftCamera ?? -1);
                return Accepted("planetary.leave", idempotencyKey);
            } finally {
                gate.Release();
            }
        }

        /// <summary>§77.4 start a SER recording (requires planetary mode).</summary>
        public async Task<OperationAcceptedDto> StartRecordingAsync(PlanetaryRecordRequestDto request, string? idempotencyKey, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!inPlanetaryMode || cameraId is null) {
                    throw new InvalidOperationException("not in planetary mode — enter first (§77.2)");
                }
                if (recorder is not null && recorder.Stats().Running) {
                    throw new InvalidOperationException("a recording is already in progress");
                }
                if (!TryParseFormat(request.Format, out var format)) {
                    throw new ArgumentException($"unknown pixel format '{request.Format}'", nameof(request));
                }
                var videoRequest = new VideoRequest {
                    StartX = request.StartX,
                    StartY = request.StartY,
                    Width = request.Width,
                    Height = request.Height,
                    Bin = request.Bin <= 0 ? 1 : request.Bin,
                    Format = format,
                    Gain = request.Gain,
                    ExposureMs = request.ExposureMs,
                };
                if (VideoFormats.FrameBytes(videoRequest) <= 0) {
                    throw new ArgumentException("invalid frame geometry", nameof(request));
                }

                var path = string.IsNullOrWhiteSpace(request.OutputPath)
                    ? DefaultOutputPath()
                    : request.OutputPath!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                recorder?.Dispose();
                capture ??= captureFactory(cameraId.Value);
                recorder = new VideoRecorder(capture, recorderLogger);
                recorder.Start(videoRequest, new VideoRecorderOptions {
                    Ser = new SerWriterOptions {
                        Path = path,
                        Observer = "OpenAstro Ara",
                        Instrument = $"ZWO camera {cameraId.Value}",
                    },
                });
                outputPath = path;
                await PublishAsync(WsEventCatalog.PlanetaryRecordingStarted, new JsonObject {
                    ["camera_id"] = cameraId.Value,
                    ["output_path"] = path,
                    ["width"] = request.Width,
                    ["height"] = request.Height,
                    ["format"] = request.Format,
                }).ConfigureAwait(false);
                LogRecordingStarted(logger, path);
                return Accepted("planetary.record", idempotencyKey);
            } finally {
                gate.Release();
            }
        }

        /// <summary>§77.4 stop the current recording; publishes final honest stats.</summary>
        public async Task<OperationAcceptedDto> StopRecordingAsync(string? idempotencyKey, CancellationToken ct) {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                StopRecordingLocked(publish: true);
                return Accepted("planetary.record_stop", idempotencyKey);
            } finally {
                gate.Release();
            }
        }

        public PlanetaryStatusDto Status() {
            var stats = recorder?.Stats();
            long? diskFree = null;
            if (outputPath is not null) {
                diskFree = DiskSpaceMonitor.TryGetFreeBytes(Path.GetDirectoryName(outputPath) ?? outputPath);
            }
            return new PlanetaryStatusDto(
                Mode: inPlanetaryMode ? "planetary" : "idle",
                CameraId: cameraId,
                OutputPath: outputPath,
                DiskFreeBytes: diskFree,
                UsbfsMemoryMb: usbfsMb ?? UsbfsTuner.ReadCurrentMb(),
                UsesDirectIo: stats?.UsesDirectIo ?? false,
                Recording: stats is null ? null : ToDto(stats));
        }

        private void StopRecordingLocked(bool publish) {
            if (recorder is null) {
                return;
            }
            var wasRunning = recorder.Stats().Running;
            recorder.Stop();
            var stats = recorder.Stats();
            if (publish && wasRunning) {
                _ = PublishAsync(WsEventCatalog.PlanetaryRecordingStopped, new JsonObject {
                    ["output_path"] = outputPath,
                    ["frames_captured"] = stats.FramesCaptured,
                    ["frames_written"] = stats.FramesWritten,
                    ["ring_dropped_frames"] = stats.RingDroppedFrames,
                    ["abandoned_frames"] = stats.AbandonedFrames,
                    ["sdk_dropped_frames"] = stats.SdkDroppedFrames,
                    ["achieved_fps"] = Math.Round(stats.AchievedFps, 2),
                    ["error"] = stats.Error,
                });
                LogRecordingStopped(logger, stats.FramesWritten, stats.RingDroppedFrames);
            }
        }

        private string DefaultOutputPath() {
            var configured = profileStore?.GetStorageSettings().SaveDirectory;
            var baseDir = !string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(configured, "planetary")
                : Path.Combine(AppContext.BaseDirectory, "frames", "planetary");
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            return Path.Combine(baseDir, $"planetary_{stamp}.ser");
        }

        private static bool TryParseFormat(string token, out VideoPixelFormat format) {
            format = token switch {
                "mono8" => VideoPixelFormat.Mono8,
                "mono16" => VideoPixelFormat.Mono16,
                "bayer_rggb8" => VideoPixelFormat.BayerRggb8,
                "bayer_grbg8" => VideoPixelFormat.BayerGrbg8,
                "bayer_gbrg8" => VideoPixelFormat.BayerGbrg8,
                "bayer_bggr8" => VideoPixelFormat.BayerBggr8,
                "bayer_rggb16" => VideoPixelFormat.BayerRggb16,
                "bayer_grbg16" => VideoPixelFormat.BayerGrbg16,
                "bayer_gbrg16" => VideoPixelFormat.BayerGbrg16,
                "bayer_bggr16" => VideoPixelFormat.BayerBggr16,
                "rgb24" => VideoPixelFormat.Rgb24,
                _ => VideoPixelFormat.Mono8,
            };
            return token is "mono8" or "mono16" or "rgb24"
                or "bayer_rggb8" or "bayer_grbg8" or "bayer_gbrg8" or "bayer_bggr8"
                or "bayer_rggb16" or "bayer_grbg16" or "bayer_gbrg16" or "bayer_bggr16";
        }

        private static PlanetaryRecordingStatsDto ToDto(RecorderStats stats) => new(
            Recording: stats.Running,
            FramesCaptured: stats.FramesCaptured,
            FramesWritten: stats.FramesWritten,
            RingDroppedFrames: stats.RingDroppedFrames,
            AbandonedFrames: stats.AbandonedFrames,
            SdkDroppedFrames: stats.SdkDroppedFrames,
            BytesWritten: stats.BytesWritten,
            AchievedFps: Math.Round(stats.AchievedFps, 2),
            Error: stats.Error);

        private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
            new(Guid.NewGuid(), operationType, DateTimeOffset.UtcNow, idempotencyKey);

        private async Task PublishAsync(string eventType, JsonObject payload) {
            if (ws is null) {
                return;
            }
            try {
                using var doc = System.Text.Json.JsonDocument.Parse(payload.ToJsonString());
                await ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) {
                LogWsPublishFailed(logger, ex);
            }
        }

        public void Dispose() {
            // Serialize with in-flight mutators so a shutdown-time Dispose can't yank
            // fields out from under an active Enter/Start (review #911 r1 minor).
            gate.Wait();
            try {
                if (disposed) {
                    return;
                }
                disposed = true;
                recorder?.Dispose();
                recorder = null;
                capture?.Dispose();
                capture = null;
            } finally {
                gate.Release();
            }
            gate.Dispose();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary mode entered: SDK camera {CameraId}, usbfs {UsbfsMb} MB.")]
        private static partial void LogEntered(ILogger logger, int cameraId, int usbfsMb);

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary mode left: SDK camera {CameraId}.")]
        private static partial void LogLeft(ILogger logger, int cameraId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Alpaca camera reconnect after leaving planetary mode failed; reconnect manually.")]
        private static partial void LogReconnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary SER recording started: {Path}.")]
        private static partial void LogRecordingStarted(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Information, Message = "Planetary SER recording stopped: written {Written}, ring-dropped {RingDropped}.")]
        private static partial void LogRecordingStopped(ILogger logger, ulong written, ulong ringDropped);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Planetary WS publish failed.")]
        private static partial void LogWsPublishFailed(ILogger logger, Exception ex);
    }
}
