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

        // Immutable session snapshot swapped atomically at the END of each gated
        // transition, so a live-polled Status() never observes a torn mid-mutation
        // combination (review #911 r3). Mutators serialize on the gate and call
        // PublishState() as their last step; Status() reads only the snapshot.
        private sealed record SessionState(bool InPlanetaryMode, int? CameraId, int? UsbfsMb, string? OutputPath, VideoRecorder? Recorder);

        private volatile SessionState session = new(false, null, null, null, null);

        private bool inPlanetaryMode;
        private int? cameraId;
        private int? usbfsMb;
        private IVideoCapture? capture;
        private VideoRecorder? recorder;
        private string? outputPath;
        private bool disposed;

        private void PublishState() =>
            session = new SessionState(inPlanetaryMode, cameraId, usbfsMb, outputPath, recorder);

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

                try {
                    usbfsMb = await tuner.AutoTuneAsync(request.UsbfsOverrideMb, ct).ConfigureAwait(false)
                              ?? UsbfsTuner.ReadCurrentMb();
                } catch {
                    // Don't strand a silently-detached camera (r2/r4): whatever
                    // escapes here — cancellation or anything AutoTuneAsync's internal
                    // catch list misses — restore the Alpaca connection best-effort
                    // before propagating. inPlanetaryMode is still false, so the
                    // invariant "detached implies planetary mode or reconnected" holds.
                    await TryReconnectAsync().ConfigureAwait(false);
                    throw;
                }

                // TOCTOU re-check (r6/r8): a run that registered any time between the
                // gate check and this point — during the Alpaca disconnect OR the usbfs
                // tune — must win. This is the last instant before the mode flips, so
                // the whole detached-but-not-yet-planetary window is covered.
                if (runs.HasAny) {
                    await TryReconnectAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "a sequence run started while entering planetary mode — camera returned to it (§77.2)");
                }

                inPlanetaryMode = true;
                cameraId = request.CameraId;
                PublishState();
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
                // Clear the finished session's stats/path so a Status() poll after the
                // next Enter can't show camera A's recording against camera B (r2).
                recorder?.Dispose();
                recorder = null;
                outputPath = null;
                inPlanetaryMode = false;
                var leftCamera = cameraId;
                cameraId = null;
                PublishState();

                var device = lastDeviceProvider();
                // CancellationToken.None deliberately (r6): a cancelled HTTP request
                // must not abandon the reconnect after state already flipped to idle —
                // same rule as the Enter failure path. TryReconnectAsync is best-effort
                // and never throws; leaving planetary mode always succeeds.
                if (device is not null) {
                    await TryReconnectAsync().ConfigureAwait(false);
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
                if (request.ExposureMs <= 0 || request.ExposureMs > 60_000) {
                    throw new ArgumentException("exposure_ms must be in [1, 60000]", nameof(request));
                }
                if (request.Bin <= 0) {
                    throw new ArgumentException("bin must be >= 1", nameof(request));
                }
                if (request.Gain < 0) {
                    throw new ArgumentException("gain must be >= 0", nameof(request));
                }
                if (!TryParseFormat(request.Format, out var format)) {
                    throw new ArgumentException($"unknown pixel format '{request.Format}'", nameof(request));
                }
                var videoRequest = new VideoRequest {
                    StartX = request.StartX,
                    StartY = request.StartY,
                    Width = request.Width,
                    Height = request.Height,
                    Bin = request.Bin,
                    Format = format,
                    Gain = request.Gain,
                    ExposureMs = request.ExposureMs,
                };
                var frameBytes = VideoFormats.FrameBytes(videoRequest);
                if (frameBytes <= 0) {
                    throw new ArgumentException("invalid frame geometry", nameof(request));
                }
                // DoS guard (review #911 r5): a crafted geometry could otherwise demand
                // a multi-GB pinned ring arena over the network surface. 128 MB/frame
                // comfortably covers any real planetary sensor (a 61 MP full-frame at
                // 16-bit is ~122 MB; planetary ROIs are a fraction of that).
                if (frameBytes > 128L * 1024 * 1024) {
                    throw new ArgumentException("frame geometry exceeds the 128 MB/frame limit", nameof(request));
                }

                // Client-supplied names are confined to the planetary output directory
                // (r2): a bare filename only — no separators, no traversal, no absolute
                // paths — so the endpoint can never write outside the save tree.
                string path;
                if (string.IsNullOrWhiteSpace(request.OutputPath)) {
                    path = DefaultOutputPath();
                } else {
                    var name = request.OutputPath!;
                    if (Path.GetFileName(name) != name || name.Contains("..", StringComparison.Ordinal)) {
                        throw new ArgumentException(
                            "output_path must be a bare .ser filename; it is placed in the planetary output directory",
                            nameof(request));
                    }
                    path = Path.Combine(PlanetaryOutputDir(), name);
                }
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
                PublishState();
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
                PublishState();
                return Accepted("planetary.record_stop", idempotencyKey);
            } finally {
                gate.Release();
            }
        }

        public PlanetaryStatusDto Status() {
            var snapshot = session;   // one volatile read: internally consistent view
            var stats = snapshot.Recorder?.Stats();
            long? diskFree = null;
            if (snapshot.OutputPath is not null) {
                diskFree = DiskSpaceMonitor.TryGetFreeBytes(
                    Path.GetDirectoryName(snapshot.OutputPath) ?? snapshot.OutputPath);
            }
            return new PlanetaryStatusDto(
                Mode: snapshot.InPlanetaryMode ? "planetary" : "idle",
                CameraId: snapshot.CameraId,
                OutputPath: snapshot.OutputPath,
                DiskFreeBytes: diskFree,
                UsbfsMemoryMb: snapshot.UsbfsMb ?? UsbfsTuner.ReadCurrentMb(),
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

        private string PlanetaryOutputDir() {
            var configured = profileStore?.GetStorageSettings().SaveDirectory;
            return !string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(configured, "planetary")
                : Path.Combine(AppContext.BaseDirectory, "frames", "planetary");
        }

        private string DefaultOutputPath() {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            return Path.Combine(PlanetaryOutputDir(), $"planetary_{stamp}.ser");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort rollback/restore path: both call sites (Enter rollback rethrowing the " +
                            "original failure, Leave's documented always-succeeds contract) require that a reconnect " +
                            "failure never replaces or adds to the primary outcome. Log-and-recover boundary.")]
        private async Task TryReconnectAsync() {
            var device = lastDeviceProvider();
            if (device is null) {
                return;
            }
            try {
                await camera.ConnectAsync(new ConnectRequestDto(device), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogReconnectFailed(logger, ex);
            }
        }

        private static bool TryParseFormat(string token, out VideoPixelFormat format) {
            switch (token) {
                case "mono8": format = VideoPixelFormat.Mono8; return true;
                case "mono16": format = VideoPixelFormat.Mono16; return true;
                case "bayer_rggb8": format = VideoPixelFormat.BayerRggb8; return true;
                case "bayer_grbg8": format = VideoPixelFormat.BayerGrbg8; return true;
                case "bayer_gbrg8": format = VideoPixelFormat.BayerGbrg8; return true;
                case "bayer_bggr8": format = VideoPixelFormat.BayerBggr8; return true;
                case "bayer_rggb16": format = VideoPixelFormat.BayerRggb16; return true;
                case "bayer_grbg16": format = VideoPixelFormat.BayerGrbg16; return true;
                case "bayer_gbrg16": format = VideoPixelFormat.BayerGbrg16; return true;
                case "bayer_bggr16": format = VideoPixelFormat.BayerBggr16; return true;
                case "rgb24": format = VideoPixelFormat.Rgb24; return true;
                default: format = VideoPixelFormat.Mono8; return false;
            }
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
                inPlanetaryMode = false;
                cameraId = null;
                outputPath = null;
                PublishState();
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
