#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Fits;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — tenth real Alpaca-backed device service and the head of the capture path. Replaces
/// <c>PlaceholderCameraService</c>: connects to a discovered Alpaca Camera, serves capabilities +
/// §32.4-cached runtime (state/temperature/cooler/progress), drives the cooler and exposure aborts,
/// and — the substantive part — <see cref="StartExposureAsync"/> runs a REAL capture:
/// pre-allocates the frame id and returns immediately (§60.5 async semantics: the preview URL 404s
/// until the capture lands), while a background pipeline exposes, polls <c>ImageReady</c>, downloads
/// <c>ImageArray</c>, writes a §72 <see cref="FitsImage"/> (atomic per §28.7) into the configured
/// save directory and registers the frame in the §28 catalog — at which point the existing
/// preview/thumbnail/download endpoints serve it like any other frame.
///
/// REST-only: the <c>ICameraMediator</c>/<c>IImagingMediator</c> unification (the
/// <c>TakeExposure</c> instruction) is the follow-up increment.
/// </summary>
public sealed partial class CameraService : ICameraService, IDisposable {

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ImageReadyPollInterval = TimeSpan.FromMilliseconds(250);
    // Margin on top of the requested exposure for the device to report ImageReady (shutter/readout
    // latency) before the capture is reported failed. Download time is bounded separately by the
    // blocking ImageArray call's own HTTP timeout.
    private static readonly TimeSpan ImageReadyMargin = TimeSpan.FromSeconds(60);
    private static readonly CameraStateDto IdleRuntime = new("idle", null, null, false, null);

    private readonly ILogger<CameraService> _logger;
    private readonly EquipmentEventPublisher? _events;
    private readonly object _gate = new();
    private readonly Timer _refreshTimer;
    private readonly IFrameRepository? _frames;
    private readonly IProfileStore? _profileStore;
    private readonly string? _fallbackFramesDir;
    private AlpacaCamera? _client;
    private DiscoveredDeviceDto? _device;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private CameraCapabilitiesDto? _capabilities;
    private CameraStateDto _runtime = IdleRuntime;
    private int _refreshing;
    private int _refreshPending;
    private long _connectGeneration;
    private int _captureInFlight;
    // Set around the blocking ImageArray transfer: serializing Alpaca bridges queue every request
    // behind the download, so timer refreshes would stall for the whole window — skip them instead
    // (exposure progress still updates: the ImageReady poll loop refreshes every tick).
    private int _downloading;
    private bool _disposed;

    public CameraService(
        ILogger<CameraService>? logger = null,
        IFrameRepository? frames = null,
        IProfileStore? profileStore = null,
        string? fallbackFramesDir = null,
        IFocuserMediator? focuser = null,
        EquipmentEventPublisher? events = null,
        ImageHistoryService? imageHistory = null) {
        _logger = logger ?? NullLogger<CameraService>.Instance;
        _events = events;
        _frames = frames;
        _profileStore = profileStore;
        _fallbackFramesDir = fallbackFramesDir;
        _focuser = focuser;
        _imageHistory = imageHistory;
        _refreshTimer = new Timer(RefreshTick, state: null, dueTime: RefreshInterval, period: RefreshInterval);
    }

    private readonly ImageHistoryService? _imageHistory;

    private readonly IFocuserMediator? _focuser;

    // Snapshotted just after pixel readout (the focuser is stationary during an
    // exposure, so post-readout == shutter-open for this metadata). A focuser
    // fault must never abort a capture whose pixels are already in hand, so a
    // throwing GetInfo() degrades to "no position recorded".
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Recording focuser position is best-effort metadata; a mediator/transport fault must not fail a capture whose image is already downloaded. Log-and-recover boundary.")]
    private int? ReadFocuserPosition() {
        try {
            return FocuserPositionFrom(_focuser?.GetInfo());
        } catch (Exception ex) {
            LogFocuserSnapshotFailed(ex);
            return null;
        }
    }

    // §38: the connected focuser's step position at capture, for the §50.4
    // focus-vs-temperature view. Null when no focuser is connected (or absent),
    // so FOCUSPOS is simply omitted and the catalog column stays null.
    internal static int? FocuserPositionFrom(FocuserInfo? info) =>
        info is { Connected: true } ? info.Position : null;

    public Task<CameraDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) {
                return Task.FromResult<CameraDto?>(null);
            }
            var state = _state;
            var connected = state == EquipmentConnectionState.Connected;
            var runtime = connected ? _runtime : IdleRuntime;
            var caps = connected ? _capabilities : null;
            return Task.FromResult<CameraDto?>(new CameraDto(_device.UniqueId, _device.Name, state, caps, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        var device = request.Device;
        long generation;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((_state == EquipmentConnectionState.Connecting || _state == EquipmentConnectionState.Connected)
                && _device?.UniqueId == device.UniqueId) {
                return Task.FromResult(Accepted("camera.connect", idempotencyKey));
            }
            DisposeClientLocked();
            _device = device;
            generation = ++_connectGeneration;
            SetState(EquipmentConnectionState.Connecting);
        }
        _ = Task.Run(() => ConnectInBackground(device, generation), CancellationToken.None);
        return Task.FromResult(Accepted("camera.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        AlpacaCamera? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectGeneration++;
            client = _client;
            _client = null;
            if (_device is not null) {
                SetState(EquipmentConnectionState.Disconnected);
            }
        }
        if (client is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(client), CancellationToken.None);
        }
        return Task.FromResult(Accepted("camera.disconnect", idempotencyKey));
    }

    /// <summary>
    /// §60.5 async capture: validates, pre-allocates the frame id, kicks the capture pipeline off
    /// in the background and returns immediately. The returned <c>PreviewUrl</c> 404s until the
    /// frame lands in the catalog (WILMA polls it the way it polls any operation result).
    /// </summary>
    public Task<ExposureResponseDto> StartExposureAsync(ExposureRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        AlpacaCamera? client;
        CameraCapabilitiesDto? caps;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
            caps = _capabilities;
        }
        // Dispose → argument range → connected, aligned with the other device services. Range
        // checks use the read-once caps when present; a zero max means that capability read failed
        // ("unknown") and validation defers to the device rather than rejecting everything.
        if (request.ExposureSec <= 0 || double.IsNaN(request.ExposureSec) || double.IsInfinity(request.ExposureSec)
            || (caps is not null && caps.MaxExposureSec > 0
                && (request.ExposureSec < caps.MinExposureSec || request.ExposureSec > caps.MaxExposureSec))) {
            throw new ArgumentOutOfRangeException(nameof(request), request.ExposureSec,
                "ExposureSec is outside the camera's supported exposure range.");
        }
        // ASCOM types BinX/BinY/Gain as short on the device; validate the short range explicitly
        // (services-wide validate-before-narrowing-cast convention) so an extreme value fails
        // loudly with a 4xx instead of silently truncating on the wire.
        if (request.BinX < 1 || request.BinY < 1
            || request.BinX > short.MaxValue || request.BinY > short.MaxValue
            || (caps is not null && caps.MaxBinX > 0 && caps.MaxBinY > 0
                && (request.BinX > caps.MaxBinX || request.BinY > caps.MaxBinY))) {
            throw new ArgumentOutOfRangeException(nameof(request), $"{request.BinX}x{request.BinY}",
                "Binning is outside the camera's supported range.");
        }
        if (request.Gain is int gain
            && (gain < 0 || gain > short.MaxValue
                || (caps is not null && caps.MaxGain > 0 && (gain < caps.MinGain || gain > caps.MaxGain)))) {
            throw new ArgumentOutOfRangeException(nameof(request), gain,
                "Gain is outside the camera's supported range.");
        }
        // Validate offset the same way as Gain: a 0 MaxOffset means the bounds read failed (or the
        // camera is in offset-index mode), so defer to the device. Without this, an out-of-range
        // offset would silently fall through TrySet to the device default (the read-back keeps the
        // FITS header honest, but the request should fail fast rather than be quietly ignored).
        if (request.CameraOffset is int cameraOffset
            && (cameraOffset < 0
                || (caps is not null && caps.MaxOffset > 0 && (cameraOffset < caps.MinOffset || cameraOffset > caps.MaxOffset)))) {
            throw new ArgumentOutOfRangeException(nameof(request), cameraOffset,
                "Offset is outside the camera's supported range.");
        }
        if (client is null) {
            throw new InvalidOperationException("camera is not connected");
        }
        if (_frames is null) {
            throw new InvalidOperationException("frame catalog is not configured; captures cannot be stored");
        }
        // One capture at a time: two concurrent StartExposure calls against the same ASCOM device
        // are undefined behavior for most drivers (and would corrupt the second download). The flag
        // releases in the pipeline's finally.
        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
            throw new InvalidOperationException("an exposure is already in progress");
        }

        var frameId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;
        try {
            _ = Task.Run(() => CaptureInBackgroundAsync(client, frameId, request), CancellationToken.None);
        } catch {
            // Task.Run can only realistically throw under extreme conditions (OOM); the in-flight
            // flag must not leak a permanently-blocked camera.
            Interlocked.Exchange(ref _captureInFlight, 0);
            throw;
        }
        return Task.FromResult(new ExposureResponseDto(
            FrameId: frameId.ToString(),
            PreviewUrl: new Uri($"/api/v1/frames/{frameId}/preview", UriKind.Relative),
            ExposureSec: request.ExposureSec,
            CapturedAt: capturedAt.ToString("O")));
    }

    public async Task AbortExposureAsync(CancellationToken ct) {
        var client = RequireConnectedClient();
        // CancellationToken.None (not ct): Task.Run(lambda, ct) never schedules the lambda if ct is
        // already cancelled, which would silently skip the abort — same hazard as the mount's
        // AbortSlew panic stop.
        await Task.Run(() => client.AbortExposure(), CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    public async Task SetCoolerAsync(bool enabled, double? targetTemperatureC, CancellationToken ct) {
        var client = RequireConnectedClient();
        await Task.Run(() => {
            if (targetTemperatureC is double target) {
                client.SetCCDTemperature = target;
            }
            client.CoolerOn = enabled;
        }, CancellationToken.None).ConfigureAwait(false);
        RefreshCacheOnce();
    }

    // §25.5.5 — select a readout mode by driver index. The list is re-read from the device inside
    // the write (not from the cached caps) so the validation matches what the driver will accept
    // even if it changed the list since connect. Per-mode electronics (full-well, e-/ADU) differ by
    // mode, so a successful set invalidates the cached caps — the next refresh re-reads them along
    // with the new current-mode name.
    public async Task SetReadoutModeAsync(int modeIndex, CancellationToken ct) {
        // Argument validation precedes the connected check (the Slew/Sync precedence): a
        // structurally-bad request is a 400 regardless of connection state.
        if (modeIndex < 0) {
            throw new ArgumentOutOfRangeException(nameof(modeIndex), modeIndex, "readout mode index must be >= 0");
        }
        var client = RequireConnectedClient();
        await Task.Run(() => {
            var modes = client.ReadoutModes;
            if (modes is null || modes.Count == 0) {
                throw new InvalidOperationException("this camera reports no readout modes");
            }
            if (modeIndex >= modes.Count) {
                throw new ArgumentOutOfRangeException(nameof(modeIndex), modeIndex,
                    $"readout mode index must be below the driver's {modes.Count}-entry list");
            }
            client.ReadoutMode = (short)modeIndex; // ASCOM ReadoutMode is a short; index already validated against the list
        }, CancellationToken.None).ConfigureAwait(false);
        lock (_gate) {
            if (ReferenceEquals(_client, client)) {
                _capabilities = null; // force a caps re-read: electronics are per-readout-mode
            }
        }
        RefreshCacheOnce();
    }

    // ── Capture pipeline ─────────────────────────────────────────────────────────────────────────

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background capture boundary: every stage (ASCOM writes, the blocking download, FITS IO, the catalog insert) can throw arbitrary driver/HTTP/disk exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-capture; any escape must be contained and logged as a failed capture (the pre-announced frame simply never appears), never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private async Task CaptureInBackgroundAsync(AlpacaCamera client, Guid frameId, ExposureRequestDto request) {
        try {
            // LIGHT is deliberate, not an oversight: the §60.5 manual-capture endpoint takes light/
            // test snapshots; calibration frames are sequencer-driven. See PORT_TODO.md "REST manual
            // capture is LIGHT-only by design" for the optional ImageType-field follow-up.
            // The bool result is intentionally ignored (fire-and-forget): a false/failed capture is
            // already logged inside CaptureCoreAsync, and the pre-announced frame simply never lands
            // — unlike the sequencer path, there's no caller to surface a throw to.
            _ = await CaptureCoreAsync(client, frameId, request, "LIGHT", FrameType.Light, "Manual capture", CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogCaptureFailed(ex, frameId);
        } finally {
            Interlocked.Exchange(ref _captureInFlight, 0);
            RefreshCacheOnce();
        }
    }

    /// <summary>
    /// The device half of a capture — settings → expose → poll ImageReady → download → pixel
    /// conversion — extracted from <see cref="CaptureCoreAsync"/> so the §59 autofocus sweep can
    /// capture analysis frames through the IDENTICAL device path without the persistence half
    /// (no §72 FITS write, no §28 catalog row — AF probe frames are throwaway measurements, not
    /// data). Returns null on the abandoned (disconnect/supersede) and not-ready paths, which are
    /// logged here exactly as before; cancellation aborts the exposure and propagates.
    /// </summary>
    private async Task<(ushort[] Pixels, int Width, int Height, DateTimeOffset CapturedAt)?> ExposeAndDownloadAsync(
            AlpacaCamera client, Guid frameId, ExposureRequestDto request, CancellationToken ct) {
        // Entry checkpoint: ApplyExposureSettings is up to 7 synchronous Alpaca round-trips with no
        // ct hook, so honor a cancel that arrived before any device work begins. Inert for REST.
        ct.ThrowIfCancellationRequested();
        ApplyExposureSettings(client, request);
        // Stamp NOW, after the settings round-trips (up to 7 Alpaca calls — seconds on a slow
        // bridge): DATE-OBS feeds plate-solving, so the FITS header must carry the actual
        // exposure start, not the request-accepted time the §60.5 response reported.
        var capturedAt = DateTimeOffset.UtcNow;
        // A sequencer cancel during the up-to-7 ApplyExposureSettings round-trips above shouldn't
        // still kick off an exposure we'd only abort on the first ImageReady poll. Inert for REST
        // (CancellationToken.None).
        ct.ThrowIfCancellationRequested();
        client.StartExposure(request.ExposureSec, true);
        RefreshCacheOnce();

        bool? ready;
        try {
            ready = await WaitForImageReadyAsync(client, request.ExposureSec, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            TryAbortQuietly(client); // sequencer cancel mid-exposure: stop the camera, then propagate
            throw;
        }
        if (ready is null) {
            LogCaptureAbandonedDisconnect(frameId); // disconnect/supersede, NOT a device timeout
            return null;
        }
        if (ready == false) {
            LogCaptureFailedNotReady(frameId, request.ExposureSec);
            return null;
        }

        // The exposure is complete (ImageReady), but the download below is a synchronous 10-30s
        // transfer on a large sensor / slow bridge with no ct hook — honor a sequencer cancel here
        // so the run doesn't block on a frame it's discarding. No abort: the camera already holds a
        // finished image, nothing to stop. REST passes CancellationToken.None, so this is inert there.
        ct.ThrowIfCancellationRequested();

        // The blocking download (single large JSON/imagebytes transfer); runs on this worker.
        object? imageArray;
        Interlocked.Exchange(ref _downloading, 1);
        try {
            imageArray = client.ImageArray;
        } finally {
            Interlocked.Exchange(ref _downloading, 0);
        }
        var (pixels, width, height) = ConvertImageArray(imageArray);
        RefreshCacheOnce();
        return (pixels, width, height, capturedAt);
    }

    /// <summary>
    /// The capture pipeline shared by the REST background path and the §14e PRb sequencer path
    /// (<c>IImagingMediator.CaptureImage</c> on the mediator partial). Caller owns the in-flight
    /// gate and the exception boundary; genuine cancellation (the sequencer token) aborts the
    /// exposure best-effort and propagates. Returns true only when the frame landed in the catalog.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Catalog-registration boundary: any DB failure after a successful FITS write degrades to an Error log naming the recoverable file (the §28.8 startup scan re-registers it) rather than faulting the capture worker. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> CaptureCoreAsync(AlpacaCamera client, Guid frameId, ExposureRequestDto request, string imageType, FrameType frameType, string targetName, CancellationToken ct) {
        // §29 — per-frame pre-capture free-space check: the disk monitor polls every 60 s, and a
        // burst of large frames can fill the volume between ticks. Blocks BEFORE the shutter
        // opens (an exposure that can't be saved is wasted sky time) — but only when the volume
        // is critically low AND the profile's OnDiskSpaceCritical policy is "abort"; the "warn"
        // default never blocks (the monitor's own notification path owns telling the user).
        if (PreCaptureDiskBlocked(out var freeBytes)) {
            LogPreCaptureDiskBlocked(frameId, freeBytes);
            return false;
        }
        var exposed = await ExposeAndDownloadAsync(client, frameId, request, ct).ConfigureAwait(false);
        if (exposed is null) {
            return false; // abandoned (disconnect/supersede) or not-ready — already logged
        }
        var (pixels, width, height, capturedAt) = exposed.Value;

        // Read back what the driver ACTUALLY applied for the header write: a driver can
        // silently coerce a setting TrySet appeared to accept (e.g. symmetric-binning
        // enforcement), and XBINNING/GAIN feed plate-solving + calibration matching.
        var applied = ReadAppliedSettings(client, request);
        var focuserPos = ReadFocuserPosition();
        var filePath = WriteFits(frameId, pixels, width, height, applied, imageType, capturedAt, focuserPos);
        try {
            await RegisterFrameAsync(frameId, request, frameType, targetName, capturedAt, filePath, width, height, focuserPos).ConfigureAwait(false);
        } catch (Exception ex) {
            // The FITS is on disk but invisible to the catalog; name the path so an operator can
            // recover it — and the §28.8 startup orphan scan re-registers it on the next boot.
            LogCatalogRegistrationFailed(ex, frameId, filePath);
            return false;
        }
        LogCaptureComplete(frameId, width, height, filePath);
        if (frameType == FrameType.Light) {
            // §59.5 — off the capture path: HFR/star analysis of a full frame takes real CPU
            // time, and delaying frame.complete (or the next exposure) for a statistic is the
            // wrong trade. The pixels are already in hand; the write-back lands as
            // frame.analyzed when done.
            QueueFrameAnalysis(frameId, pixels, width, height,
                string.IsNullOrWhiteSpace(request.FilterName) ? null : request.FilterName);
        }
        return true;
    }

    // Only a measurement with enough stars to be stable feeds the catalog + the §59.5
    // HFR-drift trigger — a 2-star HFR is noise that would swing the trend line. Deliberately
    // lower than the §59.6 autofocus bar (30): a statistic can tolerate more scatter than a
    // decision to refocus, and the trigger's least-squares smoothing absorbs the rest.
    internal const int MinStarsForAnalysis = 10;

    // Test seam: frame analysis without synthesizing a detectable star field.
    internal Func<ReadOnlyMemory<ushort>, int, int, (double Hfr, int Stars)>? AnalysisMetricOverride { get; set; }

    private sealed record AnalysisJob(Guid FrameId, ushort[] Pixels, int Width, int Height, string? FilterName);

    // Single-reader worker, NOT Task.Run-per-frame (#747 review): the HFR-drift trigger's
    // least-squares trend trusts ImageHistoryService's list order as capture chronology, and
    // per-frame tasks complete in ANALYSIS order — two fast exposures outpacing the detector
    // would shuffle the trend. One reader also caps StarDetector at one full-frame pass at a
    // time (the RPi target runs guiding/WS work beside this) and the small bound keeps a
    // burst of short subs from pinning whole pixel buffers in RAM: when the backlog is full
    // the NEW frame's analysis is skipped honestly (logged; its HFR simply stays unrecorded,
    // exactly like a star-starved frame).
    internal const int AnalysisQueueCapacity = 2;
    // FullMode stays the default (Wait) — this code only ever uses the non-blocking TryWrite,
    // which returns false synchronously on a full Wait-mode channel. DropWrite would make
    // TryWrite return TRUE while silently discarding, so the honest-skip log below could
    // never fire (round-2 review catch).
    private readonly Channel<AnalysisJob> _analysisQueue = Channel.CreateBounded<AnalysisJob>(
        new BoundedChannelOptions(AnalysisQueueCapacity) {
            SingleReader = true,
        });
    private Task? _analysisWorker;

    /// <summary>True when the frame's analysis was queued; false when the backlog was full
    /// and this frame's analysis is skipped (logged — its HFR stays unrecorded).</summary>
    internal bool QueueFrameAnalysis(Guid frameId, ushort[] pixels, int width, int height, string? filterName) {
        lock (_gate) {
            if (_disposed) {
                return false;
            }
            _analysisWorker ??= Task.Run(RunAnalysisWorkerAsync);
        }
        if (!_analysisQueue.Writer.TryWrite(new AnalysisJob(frameId, pixels, width, height, filterName))) {
            LogFrameAnalysisBacklogged(frameId);
            return false;
        }
        return true;
    }

    private async Task RunAnalysisWorkerAsync() {
        // Per-job faults are absorbed inside AnalyzeFrameAsync; the loop ends only when
        // Dispose completes the writer, so the worker can never fault-and-strand the queue.
        await foreach (var job in _analysisQueue.Reader.ReadAllAsync().ConfigureAwait(false)) {
            await AnalyzeFrameAsync(job.FrameId, job.Pixels, job.Width, job.Height, job.FilterName).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget analysis boundary: detector/DB/WS faults must degrade to a logged skip — the frame is already captured, registered, and safe; analysis is enrichment. CA1031's log-and-recover boundary applies.")]
    internal async Task AnalyzeFrameAsync(Guid frameId, ushort[] pixels, int width, int height, string? filterName) {
        try {
            var (hfr, stars) = AnalysisMetricOverride is { } metric
                ? metric(pixels, width, height)
                : DefaultAnalysisMetric(pixels, width, height);
            if (stars < MinStarsForAnalysis || double.IsNaN(hfr) || hfr <= 0) {
                LogFrameAnalysisSkipped(frameId, stars);
                return;
            }
            if (_frames is not null) {
                await _frames.UpdateAnalysisAsync(frameId, hfr, stars, CancellationToken.None).ConfigureAwait(false);
            }
            // The §59.5 HFR-drift trigger's data feed. The filter is the REQUESTED name — the
            // same name SwitchFilter drives the wheel by, so it matches the trigger's
            // SelectedFilter comparison.
            _imageHistory?.RecordImage("LIGHT", hfr, filterName);
            LogFrameAnalyzed(frameId, hfr, stars);
        } catch (Exception ex) {
            LogFrameAnalysisFailed(ex, frameId);
        }
    }

    // §59 — the one detector posture the HFR trend and the live-view overlay must share. Both feed the same
    // instrument's picture of the frame, so a sensitivity retune here must reach both; a copy-pasted literal
    // at each call site would silently desync them. maxStars = 0 leaves the count uncapped (full-frame stats);
    // the overlay passes its draw cap. IsAutoFocus = false: full-frame statistics, not the sweep's
    // center-weighted probe posture.
    private static StarDetectionParams AnalysisDetectionParams(int maxStars = 0) =>
        new() { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = false, MaxNumberOfStars = maxStars };

    private static (double Hfr, int Stars) DefaultAnalysisMetric(ushort[] pixels, int width, int height) {
        var result = StarDetector.Detect(
            pixels, width, height, AnalysisDetectionParams(), CancellationToken.None);
        return (result.AverageHFR, result.DetectedStars);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort cancellation abort: AbortExposure on a cancelled capture can throw driver/HTTP exceptions which must not mask the OperationCanceledException being propagated. CA1031's log-and-recover boundary applies.")]
    private void TryAbortQuietly(AlpacaCamera client) {
        try {
            client.AbortExposure();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
    }

    // Per-setting guarded writes: an unsupported optional setting (e.g. Gain on a gain-less driver)
    // must not abort the capture; subframe defaults to full sensor when unspecified. Subframe
    // bounds are deliberately NOT pre-validated: an out-of-bounds subframe logs + falls back to the
    // driver's current framing (per-setting-guarded strategy) rather than failing the capture.
    private void ApplyExposureSettings(AlpacaCamera client, ExposureRequestDto request) {
        TrySet(() => client.BinX = (short)request.BinX, "BinX");
        TrySet(() => client.BinY = (short)request.BinY, "BinY");
        if (request.Gain is int gain) {
            TrySet(() => client.Gain = (short)gain, "Gain");
        }
        if (request.CameraOffset is int cameraOffset) {
            TrySet(() => client.Offset = cameraOffset, "Offset");
        }
        TrySet(() => {
            var maxX = client.CameraXSize / request.BinX;
            var maxY = client.CameraYSize / request.BinY;
            var startX = Math.Clamp(request.OffsetX ?? 0, 0, Math.Max(0, maxX - 1));
            var startY = Math.Clamp(request.OffsetY ?? 0, 0, Math.Max(0, maxY - 1));
            client.StartX = startX;
            client.StartY = startY;
            // Clamp to at least 1px so an off-sensor offset can't drive NumX/NumY negative (the
            // driver throw would be logged as "unsupported", which would be misleading).
            client.NumX = Math.Max(1, Math.Min(request.Width ?? maxX, maxX - startX));
            client.NumY = Math.Max(1, Math.Min(request.Height ?? maxY, maxY - startY));
        }, "subframe");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-setting write boundary: an optional exposure setting unsupported by the driver throws; it is logged and skipped so the capture proceeds with device defaults. CA1031's log-and-recover boundary applies.")]
    private void TrySet(Action write, string setting) {
        try {
            write();
        } catch (Exception ex) {
            LogExposureSettingSkipped(ex, setting);
        }
    }

    // Polls ImageReady until set, bounded by the exposure duration + readout margin. Distinguishes
    // the two non-ready outcomes so the capture log names the real cause: false = device timeout,
    // null = the connection was dropped/superseded mid-capture.
    private async Task<bool?> WaitForImageReadyAsync(AlpacaCamera client, double exposureSec, CancellationToken ct) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(exposureSec) + ImageReadyMargin;
        while (DateTimeOffset.UtcNow < deadline) {
            await Task.Delay(ImageReadyPollInterval, ct).ConfigureAwait(false);
            bool stillOurClient;
            lock (_gate) {
                stillOurClient = !_disposed && _state == EquipmentConnectionState.Connected
                    && ReferenceEquals(_client, client);
            }
            if (!stillOurClient) {
                return null; // disconnected / superseded — the capture can't complete
            }
            if (ReadImageReadySafe(client) == true) {
                return true;
            }
            RefreshCacheOnce();
        }
        return false;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-poll read boundary: a transient ImageReady read throws; report null ('unknown') so the wait keeps polling rather than faulting the capture. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadImageReadySafe(AlpacaCamera client) {
        try {
            return client.ImageReady;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Converts the ASCOM <c>ImageArray</c> (column-major <c>[x, y]</c> per the Alpaca spec) into
    /// the row-major 16-bit buffer FITS expects, clamping to the ushort range. Color (3-axis)
    /// arrays are rejected — the v1 capture path is mono/OSC-raw only. Extracted (internal) for
    /// direct sim-free unit testing.
    /// </summary>
    internal static (ushort[] Pixels, int Width, int Height) ConvertImageArray(object? imageArray) {
        // The Alpaca spec mandates int for grayscale, but ASCOM-bridged drivers (legacy ZWO/QHY
        // bridges) are known to return double[,] — handle both; anything else (incl. 3-axis color,
        // unsupported in v1) fails with the actual payload type in the message so the capture-failed
        // log is diagnosable.
        switch (imageArray) {
            case int[,] ints: {
                var width = ints.GetLength(0);
                var height = ints.GetLength(1);
                var pixels = new ushort[width * height];
                for (var y = 0; y < height; y++) {
                    var row = y * width;
                    for (var x = 0; x < width; x++) {
                        pixels[row + x] = (ushort)Math.Clamp(ints[x, y], ushort.MinValue, ushort.MaxValue);
                    }
                }
                return (pixels, width, height);
            }
            case double[,] doubles: {
                var width = doubles.GetLength(0);
                var height = doubles.GetLength(1);
                var pixels = new ushort[width * height];
                for (var y = 0; y < height; y++) {
                    var row = y * width;
                    for (var x = 0; x < width; x++) {
                        // Clamp in the double domain BEFORE casting: a saturated/+Inf pixel must
                        // become white (65535), not wrap through the cast to black; NaN reads as 0.
                        var v = doubles[x, y];
                        pixels[row + x] = double.IsNaN(v) ? (ushort)0
                            : v >= ushort.MaxValue ? ushort.MaxValue
                            : v <= 0 ? (ushort)0
                            : (ushort)Math.Round(v);
                    }
                }
                return (pixels, width, height);
            }
            default:
                throw new InvalidOperationException(imageArray is Array { Rank: 3 }
                    ? "color (3-axis) ImageArray is not supported by the v1 capture path"
                    : $"unsupported ImageArray payload: {imageArray?.GetType().Name ?? "null"}");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field readback boundary: a driver that throws from a settings read falls back to the requested value for that header rather than failing the capture. CA1031's log-and-recover boundary applies.")]
    private static ExposureRequestDto ReadAppliedSettings(AlpacaCamera client, ExposureRequestDto request) {
        int binX = request.BinX, binY = request.BinY;
        var gain = request.Gain;
        var cameraOffset = request.CameraOffset;
        try { binX = client.BinX; } catch (Exception) { }
        try { binY = client.BinY; } catch (Exception) { }
        if (gain is not null) {
            try { gain = client.Gain; } catch (Exception) { }
        }
        if (cameraOffset is not null) {
            try { cameraOffset = client.Offset; } catch (Exception) { }
        }
        return request with { BinX = binX, BinY = binY, Gain = gain, CameraOffset = cameraOffset };
    }

    private string WriteFits(Guid frameId, ushort[] pixels, int width, int height, ExposureRequestDto request, string imageType, DateTimeOffset capturedAt, int? focuserPosition = null) {
        var dir = ResolveFramesDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{frameId}.fits");
        using var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort);
        fits.WriteImageData(pixels);
        // FITS convention + case-sensitive calibration matchers/plate-solvers want an uppercase
        // IMAGETYP; Validate() accepts the type case-insensitively, so normalize here at the write.
        fits.SetHeader("IMAGETYP", imageType.ToUpperInvariant(), "frame type");
        fits.SetHeader("EXPTIME", request.ExposureSec, "exposure seconds");
        if (request.Gain is int gain) {
            fits.SetHeader("GAIN", gain, "camera gain");
        }
        if (request.CameraOffset is int fitsOffset) {
            fits.SetHeader("OFFSET", fitsOffset, "Sensor gain offset");
        }
        fits.SetHeader("XBINNING", request.BinX, "binning X");
        fits.SetHeader("YBINNING", request.BinY, "binning Y");
        fits.SetHeader("DATE-OBS", capturedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "capture start UTC");
        if (!string.IsNullOrWhiteSpace(request.FilterName)) {
            fits.SetHeader("FILTER", request.FilterName!, "filter name");
        }
        // §38: focuser step position (when a focuser is connected) for the §50.4
        // focus-vs-temperature view; the §28.8 scanner reads it back as FOCUSPOS.
        if (focuserPosition is int focPos) {
            fits.SetHeader("FOCUSPOS", focPos, "focuser position (steps)");
        }
        // OSC sensors: record the Bayer pattern so downstream stackers (and ARA's §65 color preview)
        // can debayer the raw mosaic. The effective pattern is at the origin, so the offsets are 0.
        // The stored data stays a raw, undebayered mosaic.
        //
        // Only at 1×1: hardware binning averages adjacent sensor cells, mixing R/G/B so the readout is
        // no longer a Bayer mosaic — stamping BAYERPAT there would make the preview debayer garbage.
        if (_capabilities?.BayerPattern is string bayerPat && request.BinX == 1 && request.BinY == 1) {
            fits.SetHeader("BAYERPAT", bayerPat, "Bayer CFA pattern at image origin");
            fits.SetHeader("XBAYROFF", 0, "Bayer X offset (baked into BAYERPAT)");
            fits.SetHeader("YBAYROFF", 0, "Bayer Y offset (baked into BAYERPAT)");
        }
        fits.Complete(); // §28.7 atomic finish
        return path;
    }

    // §29 pre-capture gate — true only when the CONFIGURED save volume is critically low and the
    // profile policy says abort. Best-effort by design: no profile store, an unprobeable volume,
    // or any probe fault means "don't block" — the §29 monitor owns reporting those conditions,
    // and a broken probe must never cost the user a frame. internal (not private) so the wiring
    // is testable without an Alpaca client (CaptureCoreAsync consults exactly this).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort pre-capture probe: a profile read or DriveInfo query can throw arbitrary IO/driver exceptions, and a probe fault must degrade to 'capture proceeds' rather than blocking or faulting the capture path. CA1031's log-and-recover boundary applies.")]
    internal bool PreCaptureDiskBlocked(out long freeBytes) {
        freeBytes = 0;
        try {
            if (_profileStore is null) {
                return false;
            }
            var storage = _profileStore.GetStorageSettings();
            if (string.IsNullOrWhiteSpace(storage.SaveDirectory)) {
                return false; // fallback-dir captures are dev-box territory — the monitor doesn't watch it either
            }
            var free = DiskSpaceMonitor.TryGetFreeBytes(storage.SaveDirectory);
            if (free is null) {
                return false;
            }
            freeBytes = free.Value;
            return DiskSpaceMonitor.PreCaptureShouldBlock(free.Value,
                storage.MinFreeDiskWarnGb, storage.MinFreeDiskCriticalGb,
                _profileStore.GetSafetyPolicies().OnDiskSpaceCritical);
        } catch (Exception ex) {
            LogPreCaptureDiskProbeFailed(ex);
            return false;
        }
    }

    // Save-directory resolution: the user's §29 storage setting when present AND creatable, else
    // the daemon-local fallback (dev boxes where /media/openastroara doesn't exist). The "manual"
    // subdir keeps REST captures apart from future sequence-run target dirs.
    internal string ResolveFramesDir() {
        var configured = _profileStore?.GetStorageSettings().SaveDirectory;
        if (!string.IsNullOrWhiteSpace(configured)) {
            var candidate = Path.Combine(configured, "manual");
            if (TryEnsureDir(candidate)) {
                return candidate;
            }
            LogSaveDirUnavailable(configured!);
        }
        return Path.Combine(_fallbackFramesDir ?? Path.Combine(AppContext.BaseDirectory, "frames"), "manual");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Probe boundary: CreateDirectory failure (missing mount, permissions) selects the fallback dir rather than faulting the capture. CA1031's log-and-recover boundary applies.")]
    private static bool TryEnsureDir(string dir) {
        try {
            Directory.CreateDirectory(dir);
            return true;
        } catch (Exception) {
            return false;
        }
    }

    private async Task RegisterFrameAsync(Guid frameId, ExposureRequestDto request, FrameType frameType, string targetName, DateTimeOffset capturedAt, string filePath, int width, int height, int? focuserPosition = null) {
        // §40/§50 — a capture that runs INSIDE a sequence run carries the run's
        // catalog session via the ambient CaptureSessionScope (AsyncLocal set by
        // the run worker); everything else — the REST snapshot path — falls back
        // to the shared manual-capture bucket as before.
        var sessionId = CaptureSessionScope.Current
            ?? await _frames!.EnsureManualCaptureSessionAsync(CancellationToken.None).ConfigureAwait(false);
        var fileSize = new FileInfo(filePath).Length;
        double? ccdTemp;
        lock (_gate) {
            // Null when the camera reports no CCD temperature — recorded as NULL,
            // not a fabricated 0.0 (the same honesty rule as gain, #670).
            ccdTemp = _runtime.CcdTemperature; // _runtime is written under _gate; read it there too
        }
        await _frames!.InsertAsync(new FrameDto(
            Id: frameId,
            SessionId: sessionId,
            TargetName: targetName,
            FrameType: frameType,
            FilterName: string.IsNullOrWhiteSpace(request.FilterName) ? null : request.FilterName,
            ExposureSeconds: request.ExposureSec, // §28: real seconds — a 0.5s bias no longer rounds up to 1
            Gain: request.Gain, // §28: null when the request carries none — no more -1 sentinel
            Offset: request.CameraOffset,
            TemperatureC: ccdTemp,
            CapturedUtc: capturedAt,
            FilePath: filePath,
            FileSizeBytes: fileSize,
            Width: width,
            Height: height,
            BitDepth: 16,
            Hfr: null, StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: Array.Empty<string>(),
            FocuserPosition: focuserPosition), CancellationToken.None).ConfigureAwait(false);
    }

    // ── §32.4 cache (single-flight with coalescing, mirrors TelescopeService) ────────────────────

    private void RefreshTick(object? state) => RefreshCacheOnce();

    private void RefreshCacheOnce() {
        Volatile.Write(ref _refreshPending, 1);
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) {
            return;
        }
        try {
            while (Interlocked.Exchange(ref _refreshPending, 0) == 1) {
                RefreshPass();
            }
        } finally {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: an unhandled exception escaping here crashes the process; the per-field reads already absorb device failures, this is the hard backstop. CA1031's log-and-recover boundary applies.")]
    private void RefreshPass() {
        if (Volatile.Read(ref _downloading) == 1) {
            return; // don't queue status reads behind the blocking image download
        }
        try {
            AlpacaCamera? client;
            bool needCaps;
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected) {
                    return;
                }
                client = _client;
                needCaps = _capabilities is null;
            }
            if (client is null) {
                return;
            }
            var runtime = ReadRuntime(client);
            var caps = needCaps ? ReadCapabilities(client) : null;
            var capsCommitted = false;
            lock (_gate) {
                if (_state == EquipmentConnectionState.Connected && ReferenceEquals(_client, client)) {
                    _runtime = runtime;
                    if (caps is not null) {
                        _capabilities = caps;
                        capsCommitted = true;
                    }
                }
            }
            // §36/§25.5: the first caps read after a connect carries the camera's sensor geometry —
            // cache it into the profile's optics section so the Planning Frame FOV works without the
            // user typing it (and re-cache on a swapped camera). Outside the lock: it does profile IO.
            if (capsCommitted && caps is not null) {
                MaybeAutoPopulateOptics(caps);
                MaybeAutoPopulateElectronics(caps);
            }
        } catch (Exception ex) {
            LogRuntimeReadFailed(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Runs on the status-refresh timer: a profile read/write failure (IO/serialization) must not crash the timer or fail the camera connect — log and skip; the user can still set optics manually. CA1031's log-and-recover boundary applies.")]
    private void MaybeAutoPopulateOptics(CameraCapabilitiesDto caps) {
        if (_profileStore is null) {
            return;
        }
        try {
            // Atomic read-modify-write so a concurrent PUT /optics (the user editing focal length while
            // this connect-time populate runs) can't be lost to a stale-snapshot overwrite — the decision
            // runs under the store lock against the live value. The flag tells us whether it actually wrote.
            var populated = false;
            _profileStore.UpdateOpticsSettings(current => {
                var next = AutoPopulatedOptics(current, caps);
                populated = next is not null;
                return next;
            });
            if (populated) {
                LogOpticsAutoPopulated(caps.SensorWidth, caps.SensorHeight, caps.PixelSizeUm);
            }
        } catch (Exception ex) {
            LogOpticsAutoPopulateFailed(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Runs on the status-refresh timer: a profile read/write failure (IO/serialization) must not crash the timer or fail the camera connect — log and skip; the user can still set electronics manually. CA1031's log-and-recover boundary applies.")]
    private void MaybeAutoPopulateElectronics(CameraCapabilitiesDto caps) {
        if (_profileStore is null) {
            return;
        }
        try {
            // Same atomic read-modify-write as the optics populate: the decision runs under the
            // store lock against the live value so a concurrent PUT /camera-electronics can't be
            // lost to a stale-snapshot overwrite.
            var populated = false;
            _profileStore.UpdateCameraElectronics(current => {
                var next = AutoPopulatedElectronics(current, caps);
                populated = next is not null;
                return next;
            });
            if (populated) {
                LogElectronicsAutoPopulated(caps.SensorName ?? "", caps.FullWellCapacityE, caps.ElectronsPerAdu, caps.CurrentGain);
            }
        } catch (Exception ex) {
            LogElectronicsAutoPopulateFailed(ex);
        }
    }

    /// <summary>
    /// NEXTGEN §3/§4 — the camera electronics to persist given the connected camera's caps, or null
    /// when no change is warranted. Merges PER FIELD: a property the driver didn't expose
    /// (<c>ReadCapabilities</c> reports 0 / null / −1 for it) keeps the stored value — a driver with
    /// partial support (say full well but no e⁻/ADU) must not clobber a user-entered or previously
    /// captured value with a zero. <c>ReadNoiseE</c> is never in ASCOM and always user-owned, so it's
    /// preserved outright. <c>QuantumEfficiencyPeak</c> is also never in ASCOM, but it IS a property
    /// of the sensor — so when it's UNSET it fills from the NEXTGEN §5 Tier-1
    /// <see cref="SensorQeLibrary"/> keyed off the reported sensor name (a non-zero stored value,
    /// user-entered or previously filled, is never overwritten). ASCOM reports full well / e⁻/ADU
    /// for the CURRENT readout mode, so reconnecting in e.g. High Full Well mode re-captures the
    /// bigger well automatically (the "differs → re-cache" case). Reported doubles are epsilon-compared
    /// against the stored ones (they round-trip through profile.json) so a serialization artefact
    /// doesn't re-write + raise Changed on every reconnect; returns null when nothing changes.
    /// </summary>
    internal static CameraElectronicsDto? AutoPopulatedElectronics(CameraElectronicsDto current, CameraCapabilitiesDto caps) {
        var hasAny = caps.FullWellCapacityE > 0 || caps.ElectronsPerAdu > 0 || !string.IsNullOrEmpty(caps.SensorName);
        if (!hasAny) {
            return null;
        }
        var next = current with {
            SensorName = string.IsNullOrEmpty(caps.SensorName) ? current.SensorName : caps.SensorName,
            FullWellE = caps.FullWellCapacityE > 0 && Math.Abs(caps.FullWellCapacityE - current.FullWellE) >= 1e-9
                ? caps.FullWellCapacityE
                : current.FullWellE,
            ElectronsPerAdu = caps.ElectronsPerAdu > 0 && Math.Abs(caps.ElectronsPerAdu - current.ElectronsPerAdu) >= 1e-9
                ? caps.ElectronsPerAdu
                : current.ElectronsPerAdu,
            Gain = caps.CurrentGain >= 0 ? caps.CurrentGain : current.Gain,
            // Tier-1 QE fill: only when unset — never over a user-entered (or previously
            // filled) value, and only for a sensor the library actually knows.
            QuantumEfficiencyPeak = current.QuantumEfficiencyPeak <= 0
                    && SensorQeLibrary.LookupPeakQe(caps.SensorName) is { } libraryQe
                ? libraryQe
                : current.QuantumEfficiencyPeak,
            AutoCaptured = true,
        };
        return next == current ? null : next;
    }

    /// <summary>
    /// §36/§25.5 + §30.7 — the optics to persist given the connected camera's caps, or null when no
    /// change is warranted: the camera reports no sensor geometry (zeros), or the stored geometry
    /// already matches. Updates only the camera-owned fields (sensor W/H + pixel size); focal length
    /// and reducer come from the telescope / manual entry and are preserved. A mismatch covers both
    /// "unset → populate" (first connect) and "differs → re-cache" (a swapped camera).
    /// </summary>
    internal static OpticsSettingsDto? AutoPopulatedOptics(OpticsSettingsDto current, CameraCapabilitiesDto caps) {
        if (caps.SensorWidth <= 0 || caps.SensorHeight <= 0 || caps.PixelSizeUm <= 0) {
            return null;
        }
        // Pixel size is a double that round-trips through profile.json; compare with an epsilon so a
        // serialization artefact (3.76 → 3.7599999999998) doesn't spuriously miss the "no change" case
        // and re-write + raise Changed on every reconnect with the same camera. Sub-micron pixels make
        // 1e-9 µm a safe threshold (no real sensor is that close).
        if (current.SensorWidthPx == caps.SensorWidth
            && current.SensorHeightPx == caps.SensorHeight
            && Math.Abs(current.PixelSizeUm - caps.PixelSizeUm) < 1e-9) {
            return null;
        }
        return current with {
            SensorWidthPx = caps.SensorWidth,
            SensorHeightPx = caps.SensorHeight,
            PixelSizeUm = caps.PixelSizeUm,
        };
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing camera property falls back to its default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static CameraStateDto ReadRuntime(AlpacaCamera c) {
        CameraState state;
        try { state = c.CameraState; } catch (Exception) { state = CameraState.Idle; }
        double? temp;
        try { temp = c.CCDTemperature; } catch (Exception) { temp = null; }
        bool coolerOn;
        try { coolerOn = c.CoolerOn; } catch (Exception) { coolerOn = false; }
        double? coolerPower;
        try { coolerPower = coolerOn ? c.CoolerPower : null; } catch (Exception) { coolerPower = null; }
        double? progress;
        try { progress = state == CameraState.Exposing ? c.PercentCompleted : null; } catch (Exception) { progress = null; }
        // §25.5.5 — cooler set-point read-back ("cooling to −10°C") + the current readout-mode
        // name (driver index → display name; an out-of-range index or no mode support → null).
        double? setpoint;
        try { setpoint = c.SetCCDTemperature; } catch (Exception) { setpoint = null; }
        string? readoutMode = null;
        try {
            var modes = c.ReadoutModes;
            var idx = c.ReadoutMode;
            if (modes is not null && idx >= 0 && idx < modes.Count) {
                readoutMode = modes[idx]?.ToString();
            }
        } catch (Exception) { }
        return new CameraStateDto(MapState(state), temp, coolerPower, coolerOn, progress,
            CoolerSetpointC: setpoint, ReadoutMode: readoutMode);
    }

    // Extracted (internal) for direct unit testing.
    internal static string MapState(CameraState state) => state switch {
        CameraState.Exposing or CameraState.Waiting => "exposing",
        CameraState.Reading or CameraState.Download => "downloading",
        CameraState.Error => "error",
        _ => "idle",
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: each capability falls back to a safe default (zeroed range / false flag) rather than failing the whole capability read; ranges of zero defer validation to the device. CA1031's log-and-recover boundary applies.")]
    private static CameraCapabilitiesDto ReadCapabilities(AlpacaCamera c) {
        int w = 0, h = 0;
        try { w = c.CameraXSize; h = c.CameraYSize; } catch (Exception) { }
        double pixelSize = 0;
        try { pixelSize = c.PixelSizeX; } catch (Exception) { }
        bool canSetTemp = false;
        try { canSetTemp = c.CanSetCCDTemperature; } catch (Exception) { }
        bool canAbort = false;
        try { canAbort = c.CanAbortExposure; } catch (Exception) { }
        bool canCoolerPower = false;
        try { _ = c.CoolerPower; canCoolerPower = true; } catch (Exception) { }
        // §25.5.5 dumb-cooler probe: ASCOM's contract is that CoolerOn throws
        // PropertyNotImplementedException on a camera with no cooler, so a successful read —
        // regardless of the value — means a cooler exists even when there's no TEC set-point
        // (CanSetCCDTemperature=false).
        bool hasCooler = false;
        try { _ = c.CoolerOn; hasCooler = true; } catch (Exception) { }
        int minGain = 0, maxGain = 0;
        try { minGain = c.GainMin; maxGain = c.GainMax; } catch (Exception) { }
        int minOffset = 0, maxOffset = 0;
        try { minOffset = c.OffsetMin; maxOffset = c.OffsetMax; } catch (Exception) { }
        int maxBinX = 1, maxBinY = 1;
        try { maxBinX = c.MaxBinX; maxBinY = c.MaxBinY; } catch (Exception) { }
        double minExp = 0, maxExp = 0;
        try { minExp = c.ExposureMin; maxExp = c.ExposureMax; } catch (Exception) { }
        // Bayer pattern (OSC): only SensorType.RGGB delivers a raw 2D Bayer mosaic we can debayer —
        // its effective pattern at the image origin is RGGB shifted by ASCOM's BayerOffsetX/Y.
        // SensorType.Color returns an already-debayered 3-plane array (rejected by ConvertImageArray),
        // and Monochrome / exotic CMYG/LRGB layouts leave this null → no debayer.
        string? bayerPattern = null;
        try {
            if (c.SensorType is SensorType.RGGB) {
                int ox = 0, oy = 0;
                try { ox = c.BayerOffsetX; oy = c.BayerOffsetY; } catch (Exception) { }
                bayerPattern = EffectiveBayerPattern(ox, oy);
            }
        } catch (Exception) { }
        // NEXTGEN §3/§4 exposure-planning electronics — ASCOM reports these for the CURRENT
        // readout mode; optional properties on many drivers, so each read falls back to
        // "unset" (0 / null / -1) rather than failing the caps read.
        double fullWell = 0;
        try { fullWell = c.FullWellCapacity; } catch (Exception) { }
        double ePerAdu = 0;
        try { ePerAdu = c.ElectronsPerADU; } catch (Exception) { }
        string? sensorName = null;
        try { sensorName = c.SensorName; } catch (Exception) { }
        int currentGain = -1;
        try { currentGain = c.Gain; } catch (Exception) { }
        // §25.5.5 — readout-mode display names in driver index order (the selection endpoint
        // addresses by index into this list); unsupported → null. Y pixel pitch for
        // asymmetric-pixel sensors; unreported → 0 (assume square).
        IReadOnlyList<string>? readoutModes = null;
        try {
            var modes = c.ReadoutModes;
            if (modes is { Count: > 0 }) {
                readoutModes = modes.Cast<object?>().Select(m => m?.ToString() ?? "").ToList();
            }
        } catch (Exception) { }
        double pixelSizeY = 0;
        try { pixelSizeY = c.PixelSizeY; } catch (Exception) { }
        return new CameraCapabilitiesDto(
            SensorWidth: w, SensorHeight: h, PixelSizeUm: pixelSize,
            CanSetTemperature: canSetTemp, CanAbortExposure: canAbort, CanGetCoolerPower: canCoolerPower,
            MinGain: minGain, MaxGain: maxGain,
            MinOffset: minOffset, MaxOffset: maxOffset,
            MinBinX: 1, MaxBinX: maxBinX, MinBinY: 1, MaxBinY: maxBinY,
            MinExposureSec: minExp, MaxExposureSec: maxExp,
            BayerPattern: bayerPattern,
            FullWellCapacityE: fullWell,
            ElectronsPerAdu: ePerAdu,
            SensorName: sensorName,
            CurrentGain: currentGain,
            HasCooler: hasCooler,
            ReadoutModes: readoutModes,
            PixelSizeUmY: pixelSizeY);
    }

    // RGGB native + ASCOM BayerOffsetX/Y → the effective 2×2 pattern at the image (0,0) origin,
    // so the FITS BAYERPAT can be written with XBAYROFF/YBAYROFF = 0.
    internal static string EffectiveBayerPattern(int offsetX, int offsetY) {
        int x = ((offsetX % 2) + 2) % 2;
        int y = ((offsetY % 2) + 2) % 2;
        return (x, y) switch {
            (1, 0) => "GRBG",
            (0, 1) => "GBRG",
            (1, 1) => "BGGR",
            _ => "RGGB",
        };
    }

    // ── Connect / teardown (template) ────────────────────────────────────────────────────────────

    private AlpacaCamera RequireConnectedClient() {
        AlpacaCamera? client;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        return client ?? throw new InvalidOperationException("camera is not connected");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: constructing the Alpaca client and setting Connected can throw arbitrary driver/HTTP/socket exceptions; any escape must surface as the Error state and be contained. CA1031's log-and-recover boundary applies.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of the AlpacaCamera is managed explicitly: on every non-adopt path SafeDisconnectDispose disposes it; on the adopt path it is stored in _client and disposed later by DisconnectAsync/Dispose. CA2000 cannot follow the transfer through the lock + helper.")]
    private void ConnectInBackground(DiscoveredDeviceDto device, long generation) {
        AlpacaCamera? client = null;
        var adopted = false;
        try {
            var host = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"discovered device '{device.Name}' carries neither an IP address nor a host name");
            }
            client = new AlpacaCamera(
                device.UseHttps ? ServiceType.Https : ServiceType.Http,
                host, device.IpPort, device.AlpacaDeviceNumber, strictCasing: false, logger: null);
            client.Connected = true;
            lock (_gate) {
                if (!_disposed && _connectGeneration == generation) {
                    _client = client;
                    _capabilities = null;   // re-read for the new device
                    _runtime = IdleRuntime; // don't serve a prior device's runtime
                    SetState(EquipmentConnectionState.Connected);
                    adopted = true;
                }
            }
            if (!adopted) {
                SafeDisconnectDispose(client);
                return;
            }
            client = null; // ownership transferred to _client
            RefreshCacheOnce();
            bool stillConnected;
            lock (_gate) {
                stillConnected = _state == EquipmentConnectionState.Connected;
            }
            if (stillConnected) {
                LogConnected(device.Name, host, device.IpPort, device.AlpacaDeviceNumber);
            }
        } catch (Exception ex) {
            if (!adopted) {
                if (client is not null) {
                    SafeDisconnectDispose(client);
                }
                lock (_gate) {
                    if (!_disposed && _connectGeneration == generation) {
                        SetState(EquipmentConnectionState.Error);
                    }
                }
                LogConnectFailed(ex, device.Name);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: AbortExposure / Connected = false can throw driver/HTTP exceptions, which must be swallowed so Dispose always runs. CA1031's log-and-recover boundary applies.")]
    private void SafeDisconnectDispose(AlpacaCamera client) {
        // Only interrupt when an exposure is actually in flight: several drivers (ZWO/QHY) throw
        // from AbortExposure when idle, which would put a spurious teardown entry in every normal
        // disconnect's log.
        if (Volatile.Read(ref _captureInFlight) == 1) {
            try {
                client.AbortExposure();
            } catch (Exception ex) {
                LogTeardownIgnored(ex);
            }
        }
        try {
            client.Connected = false;
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
        DisposeQuietly(client);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown: ASCOM-backed clients have been known to throw from Dispose(); the throw must be swallowed rather than escape a fire-and-forget Task.Run. CA1031's log-and-recover boundary applies.")]
    private void DisposeQuietly(AlpacaCamera client) {
        try {
            client.Dispose();
        } catch (Exception ex) {
            LogTeardownIgnored(ex);
        }
    }

    private void DisposeClientLocked() {
        var c = _client;
        _client = null;
        if (c is not null) {
            _ = Task.Run(() => SafeDisconnectDispose(c), CancellationToken.None);
        }
    }

    // Caller must hold _gate.
    private void SetState(EquipmentConnectionState state) {
        if (_state == state) {
            return;
        }
        _state = state;
        // Callers hold the service lock; the publisher's synchronous part only
        // serializes a small payload and hands off (see EquipmentEventPublisher).
        _events?.StateChanged(DeviceType.Camera, _device?.UniqueId, _device?.Name, state);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    public void Dispose() {
        AlpacaCamera? client;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            client = _client;
            _client = null;
        }
        _refreshTimer.Dispose();
        // Stop accepting analysis jobs and let the worker drain what it holds; analysis
        // pending at shutdown is acceptable loss (the frames themselves are safe on disk).
        _analysisQueue.Writer.TryComplete();
        CancelLiveViewForDispose();
        // Dispose the client directly (guarded): the courtesy AbortExposure/Connected=false are
        // blocking HTTP calls that would hang container shutdown if the device is unreachable.
        if (client is not null) {
            DisposeQuietly(client);
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Camera runtime read failed")]
    private partial void LogRuntimeReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-populated optics sensor geometry from the connected camera: {Width}×{Height} px, {PixelSizeUm} µm")]
    private partial void LogOpticsAutoPopulated(int width, int height, double pixelSizeUm);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not auto-populate optics from the camera; set them manually in Settings → Optics")]
    private partial void LogOpticsAutoPopulateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-captured camera electronics from the connected camera: sensor '{SensorName}', full well {FullWellE} e⁻, {ElectronsPerAdu} e⁻/ADU at gain {Gain} (current readout mode)")]
    private partial void LogElectronicsAutoPopulated(string sensorName, double fullWellE, double electronsPerAdu, int gain);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not auto-capture camera electronics from the camera; set them manually in Settings → Camera electronics")]
    private partial void LogElectronicsAutoPopulateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Focuser position snapshot failed; recording the frame without a focuser position")]
    private partial void LogFocuserSnapshotFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera connected: {Name} at {Host}:{Port}/{Device}")]
    private partial void LogConnected(string name, string host, int port, int device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Camera connect failed for {Name}")]
    private partial void LogConnectFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture {FrameId} failed")]
    private partial void LogCaptureFailed(Exception ex, Guid frameId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture {FrameId} wrote {Path} but catalog registration failed — the §28.8 startup scan will recover it on the next boot")]
    private partial void LogCatalogRegistrationFailed(Exception ex, Guid frameId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture {FrameId} never reported ImageReady within the {ExposureSec}s exposure + margin")]
    private partial void LogCaptureFailedNotReady(Guid frameId, double exposureSec);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture {FrameId} abandoned: the camera was disconnected or superseded mid-capture")]
    private partial void LogCaptureAbandonedDisconnect(Guid frameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture {FrameId} complete: {Width}x{Height} -> {Path}")]
    private partial void LogCaptureComplete(Guid frameId, int width, int height, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "exposure setting {Setting} unsupported by the driver — capture proceeds with device defaults")]
    private partial void LogExposureSettingSkipped(Exception ex, string setting);

    [LoggerMessage(Level = LogLevel.Warning, Message = "configured save directory {Dir} is unavailable — falling back to the daemon-local frames dir")]
    private partial void LogSaveDirUnavailable(string dir);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ignored error while disconnecting Camera during teardown")]
    private partial void LogTeardownIgnored(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "§29 pre-capture check blocked frame {FrameId}: save volume critically low ({FreeBytes} bytes free) and OnDiskSpaceCritical=abort — the exposure never started")]
    private partial void LogPreCaptureDiskBlocked(Guid frameId, long freeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§29 pre-capture disk probe failed — capture proceeds (the disk monitor owns reporting)")]
    private partial void LogPreCaptureDiskProbeFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "§59.5 frame {FrameId} analyzed: HFR {Hfr}, {Stars} stars")]
    private partial void LogFrameAnalyzed(Guid frameId, double hfr, int stars);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§59.5 frame {FrameId} analysis skipped: only {Stars} stars detected — too few for a stable HFR")]
    private partial void LogFrameAnalysisSkipped(Guid frameId, int stars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§59.5 frame {FrameId} analysis failed — the frame itself is safe; HFR stays unrecorded")]
    private partial void LogFrameAnalysisFailed(Exception ex, Guid frameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "§59.5 frame {FrameId} analysis skipped: the analysis backlog is full (captures are outpacing the detector) — HFR stays unrecorded for this frame")]
    private partial void LogFrameAnalysisBacklogged(Guid frameId);
}
