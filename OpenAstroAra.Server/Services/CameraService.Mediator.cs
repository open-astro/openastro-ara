// The device-event members satisfy the equipment mediator interfaces but are never raised
// server-side (the Flutter client drives state over REST/WS), so CS0067 "event is never used" is
// expected here and intentionally suppressed for the whole file — same as the other
// *Service.Mediator.cs partials.
#pragma warning disable CS0067

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
using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e capture-path PRb — the real <see cref="CameraService"/> also serves the Sequencer's
/// <see cref="ICameraMediator"/> (live <c>GetInfo()</c> for <c>TakeExposure.Validate</c> + the
/// camera-control instructions' guards) and <see cref="IImagingMediator"/>, whose
/// <see cref="CaptureImage"/> runs the SAME §14e pipeline as the REST endpoint (expose → download →
/// §72 FITS → §28 catalog) and returns an inert <see cref="IExposureData"/> sentinel — the frame is
/// already persisted server-side. <see cref="CaptureAndPrepareImage"/> (the §28 plate-solve
/// capture) is real: it captures an unpersisted frame and hands the solver the raw pixels. The
/// remaining WPF-era in-memory members
/// (<see cref="PrepareImage(IImageData, PrepareImageParameters, CancellationToken)"/>/live view)
/// stay <see cref="NotSupportedException"/> until the §2105 image pipeline lands.
/// </summary>
public sealed partial class CameraService : ICameraMediator, IImagingMediator {

    // How long a sequencer capture waits for a concurrent (REST-initiated) capture to release the
    // in-flight gate before each re-check; captures are seconds-to-minutes, so 100ms is plenty fine.
    private static readonly TimeSpan CaptureGatePollInterval = TimeSpan.FromMilliseconds(100);

    // Upper bound on the gate wait. A legitimate in-flight capture self-bounds to its exposure +
    // ImageReadyMargin, so the only way the gate stays held past a generous ceiling is a leaked
    // flag (e.g. a faulted background task). Surfacing a clear TimeoutException beats blocking the
    // whole sequence run until the run token is cancelled. 20 min clears the longest DSO exposure
    // (§18.J caps the workflow at 900s) plus download + margin with room to spare.
    private static readonly TimeSpan CaptureGateMaxWait = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Synchronous live snapshot for the Sequencer from the §32.4 cache (never throws after
    /// Dispose). Populates what the instructions consume: <c>Connected</c> (TakeExposure/cooling
    /// guards), name/id, plus temperature + cooler state for the camera-control instructions'
    /// display surface. The ~40 remaining CameraInfo members stay at defaults — no registered
    /// instruction reads them headless.
    /// </summary>
    public CameraInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var runtime = _runtime;
            return new CameraInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                Temperature = connected ? runtime.CcdTemperature ?? double.NaN : double.NaN,
                CoolerOn = connected && runtime.CoolerOn,
                CoolerPower = connected ? runtime.CoolerPowerPct ?? double.NaN : double.NaN,
                // #1187 — the CoolCamera/WarmCamera instructions' Validate() refuses a camera that
                // reports no set-point regulation; left unset, every cooling step failed validation.
                CanSetTemperature = connected && _capabilities?.CanSetTemperature == true,
                TemperatureSetPoint = connected ? runtime.CoolerSetpointC ?? double.NaN : double.NaN,
            };
        }
    }

    /// <summary>
    /// §14e PRb — the sequencer capture: maps the NINA <see cref="CaptureSequence"/> onto the shared
    /// pipeline and blocks until the frame is in the catalog. Serializes against REST captures via
    /// the same in-flight gate (waiting, not failing — a sequence must queue behind a manual
    /// snapshot, not abort). Throws on a failed capture so the instruction's attempt/error policy
    /// engages; genuine sequencer cancellation aborts the exposure and propagates.
    /// </summary>
    public async Task<IExposureData> CaptureImage(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus>? progress, string targetName = "") {
        ArgumentNullException.ThrowIfNull(sequence);
        AlpacaCamera? client;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            throw new InvalidOperationException("camera is not connected");
        }
        if (_frames is null) {
            throw new InvalidOperationException("frame catalog is not configured; captures cannot be stored");
        }

        var request = new ExposureRequestDto(
            ExposureSec: sequence.ExposureTime,
            Gain: sequence.Gain < 0 ? null : sequence.Gain, // NINA convention: -1 = camera default
            BinX: Math.Max(1, (int)(sequence.Binning?.X ?? 1)),
            BinY: Math.Max(1, (int)(sequence.Binning?.Y ?? 1)),
            FilterName: sequence.FilterType?.Name,
            CameraOffset: sequence.Offset < 0 ? null : sequence.Offset); // same -1 convention as Gain
        var imageType = string.IsNullOrWhiteSpace(sequence.ImageType) ? ImageTypes.LIGHT : sequence.ImageType;
        var effectiveTarget = string.IsNullOrWhiteSpace(targetName) ? "Sequence capture" : targetName;

        // Acquire the shared in-flight gate (REST rejects when busy; the sequencer WAITS — a
        // sequence capture must queue behind a manual snapshot, not abort the run).
        var gateDeadline = DateTimeOffset.UtcNow + CaptureGateMaxWait;
        while (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= gateDeadline) {
                throw new TimeoutException(
                    $"timed out after {CaptureGateMaxWait.TotalMinutes:0} min waiting for an in-progress capture to release the camera. Likely a manual capture running a very long exposure or a download stalled on a slow bridge; check the daemon log for an active capture. If none is running the in-flight gate is stuck and the daemon must be restarted to clear it — a reconnect does not reset the gate.");
            }
            await Task.Delay(CaptureGatePollInterval, token).ConfigureAwait(false);
        }
        try {
            // Re-snapshot under the gate: a disconnect/reconnect during a long queue wait can
            // supersede the client we grabbed before the loop, and we must not issue ASCOM commands
            // (ApplyExposureSettings/StartExposure) against a stale connection — fast-fail instead.
            // (_frames needs no re-check: it's set once in the constructor and never reassigned.)
            lock (_gate) {
                client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            }
            if (client is null) {
                throw new InvalidOperationException("camera disconnected while the capture was queued");
            }
            var frameId = Guid.NewGuid();
            // progress is intentionally not forwarded: the headless daemon reports capture progress
            // to clients over the §60.9 WS stream (SequencerService), not through NINA's in-process
            // IProgress reporter, which has no subscriber in the server.
            var ok = await CaptureCoreAsync(client, frameId, request, imageType, MapFrameType(imageType), effectiveTarget, token).ConfigureAwait(false);
            if (!ok) {
                throw new InvalidOperationException(
                    $"capture of '{effectiveTarget}' failed — see the daemon log for the cause (device timeout, disconnect, or storage failure)");
            }
            LogSequencerCaptureComplete(frameId, imageType, effectiveTarget);
            return new PersistedFrameExposureData();
        } finally {
            Interlocked.Exchange(ref _captureInFlight, 0);
            RefreshCacheOnce();
        }
    }

    // NINA ImageTypes → §28 catalog FrameType. SNAPSHOT counts as a light; DARKFLAT (used by some
    // NINA flows) coarsens to the catalog's Dark — the FrameType enum has no DarkFlat — while the
    // FITS IMAGETYP header preserves the finer "DARKFLAT" string. The catalog type is intentionally
    // broader than the header for these, not a mismatch.
    internal static FrameType MapFrameType(string? imageType) => imageType?.ToUpperInvariant() switch {
        "FLAT" => FrameType.Flat,
        "DARK" or "DARKFLAT" => FrameType.Dark,
        "BIAS" => FrameType.Bias,
        _ => FrameType.Light,
    };

    /// <summary>
    /// Inert <see cref="IExposureData"/> returned by <see cref="CaptureImage"/>: the frame is
    /// persisted server-side by the pipeline, and the WPF-era in-memory image path is not ported —
    /// ARA's <c>TakeExposure</c> discards this by design; anything that tries to consume it fails
    /// loudly rather than silently working with no data.
    /// </summary>
    private sealed class PersistedFrameExposureData : IExposureData {
        public int BitDepth => 16;
        public ImageMetaData MetaData { get; } = new ImageMetaData();
        public Task<IImageData> ToImageData(IProgress<ApplicationStatus>? progress = default, CancellationToken cancelToken = default) =>
            Task.FromException<IImageData>(new NotSupportedException(
                "The captured frame was persisted to the §28 catalog server-side; the in-memory image pipeline is not ported (§2105)."));
    }

    // ── §28 plate-solve capture ──────────────────────────────────────────────────────────────────
    // CaptureSolver's only image source. Before this landed it threw NotSupported, which took down
    // every centering caller (CenterAndRotate, POST /platesolve/center, the §58.4 flip recenter,
    // the §35 safety re-center) on the first exposure. Rides the same unpersisted capture core as
    // the §59 autofocus probe: expose → download → wrap the raw 16-bit frame as IImageData (the
    // CLI solvers write it to a temp FITS themselves) → an AutoSTF 8-bit render for the progress
    // thumbnail. The frame is deliberately not catalogued (SNAPSHOT semantics).
    public async Task<IRenderedImage> CaptureAndPrepareImage(CaptureSequence sequence, PrepareImageParameters parameters, CancellationToken token, IProgress<ApplicationStatus>? progress) {
        ArgumentNullException.ThrowIfNull(sequence);
        var request = SolveCaptureRequest(sequence);
        if (request.ExposureSec <= 0 || double.IsNaN(request.ExposureSec) || double.IsInfinity(request.ExposureSec)) {
            throw new ArgumentOutOfRangeException(nameof(sequence), request.ExposureSec,
                "plate-solve exposure must be a positive, finite number of seconds (Options → Plate solving → Exposure time)");
        }
        // Same caps check as the autofocus probe: ApplyExposureSettings' TrySet logs-and-skips an
        // unsupported binning, so the frame would come back unbinned while PlateSolveParameter
        // still hands the solver a binned pixel scale — an opaque solver failure instead of this.
        CameraCapabilitiesDto? caps;
        lock (_gate) {
            caps = _capabilities;
        }
        if (caps is not null && caps.MaxBinX > 0 && caps.MaxBinY > 0
                && (request.BinX > caps.MaxBinX || request.BinY > caps.MaxBinY)) {
            throw new ArgumentOutOfRangeException(nameof(sequence), $"{request.BinX}x{request.BinY}",
                $"plate-solve binning exceeds the camera's supported maximum ({caps.MaxBinX}x{caps.MaxBinY}) — lower it in Options → Plate solving → Binning");
        }
        // Same shape for the exposure: outside the camera's range it would surface as an opaque
        // StartExposure device fault instead of this.
        if (caps is not null && caps.MaxExposureSec > 0
                && (request.ExposureSec < caps.MinExposureSec || request.ExposureSec > caps.MaxExposureSec)) {
            throw new ArgumentOutOfRangeException(nameof(sequence), request.ExposureSec,
                $"plate-solve exposure is outside the camera's supported range ({caps.MinExposureSec}–{caps.MaxExposureSec} s) — change it in Options → Plate solving → Exposure time");
        }
        var frame = await CaptureUnpersistedAsync(request, "plate-solve", token).ConfigureAwait(false);
        // Hardware binning mixes the CFA cells, so only a 1×1 OSC frame is still a Bayer mosaic.
        // Metadata-only on this path: the solver's temp FITS carries no BAYERPAT card (the persisted
        // capture path stamps that header itself); the CLI solvers work on the raw mosaic regardless.
        var isBayered = caps?.BayerPattern is not null && request.BinX == 1 && request.BinY == 1;
        // The legacy profile is only CARRIED by the wrap (RenderImage hands it to the RenderedImage,
        // which reads it in Stretch/DetectStars); the solve path never calls those and SaveToDisk
        // does not touch it — so null is fine.
        var profile = _legacyProfile?.Invoke();
        var cameraName = _device?.Name;
        // The AutoSTF render is ~50-200 ms on a full frame; keep it off the caller's thread.
        return await Task.Run(() => RenderForSolve(frame, request, isBayered, cameraName, profile), token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The exposure the solve sequence asks for, in the daemon's own request shape. NINA's
    /// CaptureSequence uses -1 for "leave gain/offset at the camera's current value"; ARA's DTO
    /// says that with null. Binning below 1 (an unset BinningMode) reads as 1×1. FilterName is
    /// carried for completeness only: ApplyExposureSettings does not drive the wheel from it (both
    /// centering call sites pass no filter, and CaptureSolver owns the filter restore).
    /// </summary>
    internal static ExposureRequestDto SolveCaptureRequest(CaptureSequence sequence) {
        var binX = Math.Max(1, (int)(sequence.Binning?.X ?? 1));
        var binY = Math.Max(1, (int)(sequence.Binning?.Y ?? 1));
        return new ExposureRequestDto(
            ExposureSec: sequence.ExposureTime,
            Gain: sequence.Gain >= 0 ? sequence.Gain : null,
            BinX: binX,
            BinY: binY,
            FilterName: sequence.FilterType?.Name,
            CameraOffset: sequence.Offset >= 0 ? sequence.Offset : null);
    }

    /// <summary>
    /// Wrap an unpersisted frame for the solver: the raw 16-bit pixels as <see cref="IImageData"/>
    /// (what <c>ImageSolver</c> hands the CLI solver, which writes its own temp FITS) plus an
    /// AutoSTF 8-bit render whose only consumer is <c>CaptureSolver</c>'s progress thumbnail.
    /// Static and side-effect free so the wrap is unit-testable without a camera.
    /// </summary>
    internal static IRenderedImage RenderForSolve(AnalysisFrame frame, ExposureRequestDto request, bool isBayered,
            string? cameraName, OpenAstroAra.Profile.Interfaces.IProfileService? profile) {
        // AnalysisFrame owns the only reference to the downloaded buffer; reuse it rather than
        // copying a full frame (~120 MB on a 60 MP sensor) per solve attempt.
        var pixels = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
                && segment.Offset == 0 && segment.Array is { } arr && segment.Count == arr.Length
            ? arr
            : frame.Pixels.ToArray();
        var meta = new ImageMetaData();
        meta.Image.ExposureTime = request.ExposureSec;
        meta.Image.ImageType = "SNAPSHOT";
        meta.Image.ExposureStart = frame.CapturedAt.UtcDateTime;
        meta.Camera.BinX = request.BinX;
        meta.Camera.BinY = request.BinY;
        meta.Camera.Gain = request.Gain ?? -1;
        meta.Camera.Offset = request.CameraOffset ?? -1;
        if (!string.IsNullOrEmpty(cameraName)) {
            meta.Camera.Name = cameraName;
        }
        var raw = new BaseImageData(pixels, frame.Width, frame.Height, bitDepth: 16, isBayered: isBayered,
            meta, profile!, null!, null!);
        // The one AutoSTF display render the codebase has, so the two paths cannot drift.
        return raw.RenderImage();
    }

    // ── Unported IImagingMediator surface — the §2105 image pipeline lands these ────────────────
    public Task<IRenderedImage> PrepareImage(IImageData imageData, PrepareImageParameters parameters, CancellationToken token) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("the in-memory render pipeline is not ported (§2105)"));
    public Task<IRenderedImage> PrepareImage(IExposureData imageData, PrepareImageParameters parameters, CancellationToken token) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("the in-memory render pipeline is not ported (§2105)"));
    public Task<bool> StartLiveView(CaptureSequence sequence, CancellationToken ct) =>
        Task.FromException<bool>(new NotSupportedException("live view is not ported (§2105)"));
    public void DestroyImage() { }
    public int GetImageRotation() => 0;
    public void SetImageRotation(int rotation) { }
    void IImagingMediator.SetSubSambleRectangle(ObservableRectangle observableRectangle) { }
    public event EventHandler<ImagePreparedEventArgs>? ImagePrepared;

    // ── ICameraMediator control surface ──────────────────────────────────────────────────────────
    // The capture-producing members stay NotSupported: TakeExposure goes through IImagingMediator
    // above, and no registered instruction calls raw Capture/Download/LiveView (per the #315
    // capture-block note, IsFreeToCapture now truthfully reflects the shared in-flight gate).

    public Task Capture(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus> progress) =>
        Task.FromException(new NotSupportedException("raw mediator Capture is not wired; captures go through IImagingMediator.CaptureImage"));
    public IAsyncEnumerable<IExposureData> LiveView(CancellationToken token) =>
        throw new NotSupportedException("live view is not ported (§2105)");
    public IAsyncEnumerable<IExposureData> LiveView(CaptureSequence sequence, CancellationToken token) =>
        throw new NotSupportedException("live view is not ported (§2105)");
    public Task<IExposureData> Download(CancellationToken token) =>
        Task.FromException<IExposureData>(new NotSupportedException("raw mediator Download is not wired; captures go through IImagingMediator.CaptureImage"));

    void ICameraMediator.AbortExposure() {
        AlpacaCamera? client;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is not null) {
            TryAbortQuietly(client);
            RefreshCacheOnce();
        }
    }

    // Per-exposure settings ride on the CaptureSequence; these WPF-era knobs are no-ops headless.
    public void SetReadoutMode(short mode) { }
    public void SetReadoutModeForNormalImages(short mode) { }
    public void SetBinning(short x, short y) { }
    public void SetDewHeater(bool onOff) { }
    public void SetUSBLimit(int usbLimit) { }
    void ICameraMediator.SetSubSambleRectangle(ObservableRectangle observableRectangle) { }

    // ── #1187 sequencer cooling (NINA CameraVM.CoolCamera/WarmCamera semantics) ──────────────────
    // Every set-point and cooler write goes through SetCoolerAsync, so the §25.5.5 capability gate
    // and the #1076 cooling-fan interlock (fan started before the first cooler-on, a fan that cannot
    // start refuses the cool, fan stopped after the cooler-off) cover sequences exactly as they
    // cover the REST cooler control.

    /// <summary>Ramp checkpoint spacing: the set-point moves along the start→target line every
    /// 15 s (NINA), in whole degrees until the last checkpoint, which writes the exact target.</summary>
    internal static readonly TimeSpan CoolingRampInterval = TimeSpan.FromSeconds(15);
    /// <summary>Sensor poll spacing while waiting for the final set-point (NINA: 5 s).</summary>
    internal static readonly TimeSpan CoolingPollInterval = TimeSpan.FromSeconds(5);
    /// <summary>Give up after this long without the sensor closing on the target by
    /// <see cref="CoolingProgressC"/> while the TEC is pinned (≥99 % or ≤1 %) or its power is
    /// unreadable (NINA: 2 min at saturated power).</summary>
    internal static readonly TimeSpan CoolingStallTimeout = TimeSpan.FromMinutes(2);
    /// <summary>The same, while the TEC still reports headroom (1–99 %). NINA waited forever here;
    /// a bound keeps a regulator that settles just outside tolerance from hanging the sequence.</summary>
    internal static readonly TimeSpan CoolingRegulatingTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Pause at the warm target before the cooler-off (NINA: 20 s).</summary>
    internal static readonly TimeSpan WarmCoolerOffSettle = TimeSpan.FromSeconds(20);
    /// <summary>Cooling is done at target + 1 °C, warming at target − 1 °C (NINA).</summary>
    internal const double CoolingReachedToleranceC = 1.0;
    internal const double CoolingProgressC = 0.5;
    /// <summary><see cref="AtTargetTemp"/> tolerance (NINA's CameraVM.AtTargetTemp: ±2 °C).</summary>
    internal const double AtTargetToleranceC = 2.0;
    /// <summary>WarmCamera's set-point target (NINA): a TEC cannot warm past ambient, so on a
    /// colder night the warm stalls short of it and still switches the cooler off.</summary>
    internal const double WarmCameraTargetC = 20.0;

    /// <summary>Test seam for the cooling ramp's waits (ramps run minutes; tests run them instantly).</summary>
    internal Func<TimeSpan, CancellationToken, Task> CoolingDelay { get; set; } = Task.Delay;

    // The final target of an in-flight CoolCamera/WarmCamera (the device set-point read-back is only
    // the current ramp step). NaN when no sequencer cooling is running. Guarded by _gate.
    private double _coolingTargetC = double.NaN;

    /// <summary>The in-flight sequencer cooling target, else the cooler's set-point read-back while
    /// the cooler is on; NaN when disconnected or the cooler is off.</summary>
    public double TargetTemp {
        get {
            lock (_gate) {
                if (_disposed || _state != EquipmentConnectionState.Connected || _client is null) {
                    return double.NaN;
                }
                if (!double.IsNaN(_coolingTargetC)) {
                    return _coolingTargetC;
                }
                return _runtime.CoolerOn ? _runtime.CoolerSetpointC ?? double.NaN : double.NaN;
            }
        }
    }

    public bool AtTargetTemp {
        get {
            double temperature;
            lock (_gate) {
                temperature = _state == EquipmentConnectionState.Connected ? _runtime.CcdTemperature ?? double.NaN : double.NaN;
            }
            var target = TargetTemp;
            return !double.IsNaN(temperature) && !double.IsNaN(target) && Math.Abs(temperature - target) <= AtTargetToleranceC;
        }
    }

    /// <summary>
    /// #1187 — the sequencer's CoolCamera. <paramref name="duration"/> zero (or a start already
    /// within 1 °C) writes the target at once; otherwise the set-point ramps linearly over it. Then
    /// waits for the sensor to reach target + 1 °C. False (with the reason reported on
    /// <paramref name="progress"/>) when the camera is disconnected, cannot take a set-point, the
    /// fan interlock or driver refuses the write, or the sensor stalls short of the target.
    /// Cancellation holds the set-point at the current sensor temperature and rethrows.
    /// </summary>
    public async Task<bool> CoolCamera(double temperature, TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
        const string operation = "Cooling";
        var refusal = CoolingRefusal(needsSetpoint: true);
        if (refusal is not null) {
            return CoolingFailed(operation, refusal, progress);
        }
        LogSequencerCoolStart(temperature, duration);
        SetCoolingTarget(temperature);
        try {
            var shortfall = await RegulateTemperatureAsync(temperature, duration, cooling: true, progress, ct).ConfigureAwait(false);
            if (shortfall is not null) {
                return CoolingFailed(operation, shortfall, progress);
            }
            progress?.Report(new ApplicationStatus { Status = string.Empty });
            return true;
        } catch (InvalidOperationException ex) {
            // SetCoolerAsync's refusals (fan interlock, capability gate, driver rejection) and a
            // camera that disconnected mid-ramp.
            return CoolingFailed(operation, ex.Message, progress);
        } finally {
            SetCoolingTarget(double.NaN);
        }
    }

    /// <summary>
    /// #1187 — the sequencer's WarmCamera: with the cooler on, ramp the set-point toward
    /// <see cref="WarmCameraTargetC"/> over <paramref name="duration"/> (a stall short of it — a cold
    /// night — is logged, not a failure), settle 20 s, then switch the cooler off. A cooler that is
    /// off (or whose state cannot be read) is never switched on; it only gets the cooler-off.
    /// Cancellation (an operator stopping the sequence) skips the cooler-off and leaves the TEC
    /// holding, unlike the §58 unattended warm ramp, which must always reach its cooler-off.
    /// </summary>
    public async Task<bool> WarmCamera(TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
        const string operation = "Warming";
        var refusal = CoolingRefusal(needsSetpoint: false);
        if (refusal is not null) {
            return CoolingFailed(operation, refusal, progress);
        }
        LogSequencerWarmStart(duration);
        try {
            var telemetry = await ReadCoolingTelemetryAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("camera is not connected");
            bool canSetTemperature;
            lock (_gate) {
                canSetTemperature = _capabilities?.CanSetTemperature != false;
            }
            if (telemetry.CoolerOn == true) {
                if (canSetTemperature && !double.IsNaN(telemetry.Temperature)
                        && telemetry.Temperature < WarmCameraTargetC - CoolingReachedToleranceC) {
                    SetCoolingTarget(WarmCameraTargetC);
                    var shortfall = await RegulateTemperatureAsync(WarmCameraTargetC, duration, cooling: false, progress, ct).ConfigureAwait(false);
                    if (shortfall is not null) {
                        LogSequencerWarmShort(shortfall);
                    }
                }
                progress?.Report(new ApplicationStatus { Status = "Waiting to turn the cooler off" });
                await CoolingDelay(WarmCoolerOffSettle, ct).ConfigureAwait(false);
            }
            await SetCoolerAsync(enabled: false, targetTemperatureC: null, ct).ConfigureAwait(false);
            progress?.Report(new ApplicationStatus { Status = string.Empty });
            return true;
        } catch (InvalidOperationException ex) {
            return CoolingFailed(operation, ex.Message, progress);
        } finally {
            SetCoolingTarget(double.NaN);
        }
    }

    /// <summary>Ramp (or set) the set-point, then wait for the sensor. Null when the target was
    /// reached, else why not. Throws <see cref="InvalidOperationException"/> on a refused write or
    /// a lost camera, and rethrows cancellation after holding the set-point where the sensor is.</summary>
    private async Task<string?> RegulateTemperatureAsync(double target, TimeSpan duration, bool cooling,
            IProgress<ApplicationStatus>? progress, CancellationToken ct) {
        var label = cooling ? "Cooling camera" : "Warming camera";
        var start = (await ReadCoolingTelemetryAsync().ConfigureAwait(false))?.Temperature ?? double.NaN;
        var wrote = false;
        try {
            double? last = null;
            if (duration > TimeSpan.Zero && !double.IsNaN(start) && Math.Abs(start - target) > CoolingReachedToleranceC) {
                var checkpoints = (int)(duration.Ticks / CoolingRampInterval.Ticks);
                for (var i = 1; i <= checkpoints; i++) {
                    var fraction = (double)(i * CoolingRampInterval.Ticks) / duration.Ticks;
                    var step = fraction >= 1
                        ? target
                        : Math.Round(start + ((target - start) * fraction), MidpointRounding.AwayFromZero);
                    if (step != last) {
                        ct.ThrowIfCancellationRequested();
                        await SetCoolerAsync(enabled: true, step, ct).ConfigureAwait(false);
                        wrote = true;
                        last = step;
                    }
                    ReportCooling(progress, label, target, fraction);
                    await CoolingDelay(CoolingRampInterval, ct).ConfigureAwait(false);
                }
            }
            if (last != target) {
                ct.ThrowIfCancellationRequested();
                await SetCoolerAsync(enabled: true, target, ct).ConfigureAwait(false);
                wrote = true;
            }

            var telemetry = await ReadCoolingTelemetryAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("camera is not connected");
            var best = Distance(telemetry.Temperature, target);
            var idle = TimeSpan.Zero;
            while (!Reached(telemetry.Temperature, target, cooling)) {
                var span = Math.Abs(start - target);
                ReportCooling(progress, label, target, double.IsNaN(span) || span == 0 ? 0 : 1 - (Distance(telemetry.Temperature, target) / span));
                await CoolingDelay(CoolingPollInterval, ct).ConfigureAwait(false);
                telemetry = await ReadCoolingTelemetryAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("camera is not connected");
                var distance = Distance(telemetry.Temperature, target);
                if (best - distance >= CoolingProgressC) {
                    best = distance;
                    idle = TimeSpan.Zero;
                    continue;
                }
                idle += CoolingPollInterval;
                var regulating = telemetry.CoolerPowerPct is > 1 and < 99;
                if (idle >= (regulating ? CoolingRegulatingTimeout : CoolingStallTimeout)) {
                    var power = telemetry.CoolerPowerPct is double p ? p.ToString("0", CultureInfo.InvariantCulture) + " %" : "unknown";
                    return string.Create(CultureInfo.InvariantCulture,
                        $"could not reach {target:0.#} °C (sensor at {telemetry.Temperature:0.#} °C, cooler power {power}, no progress for {idle.TotalMinutes:0} min)");
                }
            }
            return null;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested && wrote) {
            // NINA: a cancelled ramp leaves the TEC holding where the sensor is, not still driving
            // toward a target nobody is waiting for.
            await HoldSetpointAtSensorAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static double Distance(double temperature, double target) =>
        double.IsNaN(temperature) ? double.PositiveInfinity : Math.Abs(temperature - target);

    private static bool Reached(double temperature, double target, bool cooling) =>
        !double.IsNaN(temperature)
        && (cooling ? temperature <= target + CoolingReachedToleranceC : temperature >= target - CoolingReachedToleranceC);

    private async Task HoldSetpointAtSensorAsync() {
        var temperature = (await ReadCoolingTelemetryAsync().ConfigureAwait(false))?.Temperature ?? double.NaN;
        if (double.IsNaN(temperature)) {
            return;
        }
        try {
            await SetCoolerAsync(enabled: true, temperature, CancellationToken.None).ConfigureAwait(false);
        } catch (InvalidOperationException ex) {
            LogSequencerCoolingHoldFailed(ex);
        }
    }

    /// <summary>Why a sequencer cooling call cannot start, or null. Reads the capabilities first if
    /// the refresh has not yet; unknown capabilities let the write be attempted.</summary>
    private string? CoolingRefusal(bool needsSetpoint) {
        bool connected;
        CameraCapabilitiesDto? caps;
        lock (_gate) {
            connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            caps = _capabilities;
        }
        if (!connected) {
            return "camera is not connected";
        }
        if (caps is null) {
            RefreshCacheOnce();
            lock (_gate) {
                caps = _capabilities;
            }
        }
        return CoolerCapabilityError(caps?.CanSetTemperature, caps?.HasCooler, needsSetpoint ? 0.0 : null);
    }

    private bool CoolingFailed(string operation, string reason, IProgress<ApplicationStatus>? progress) {
        LogSequencerCoolingFailed(operation, reason);
        progress?.Report(new ApplicationStatus { Status = $"{operation} failed: {reason}" });
        return false;
    }

    private static void ReportCooling(IProgress<ApplicationStatus>? progress, string label, double target, double fraction) =>
        progress?.Report(new ApplicationStatus {
            Status = string.Create(CultureInfo.InvariantCulture, $"{label} to {target:0.#} °C"),
            Progress = Math.Clamp(fraction, 0, 1),
        });

    private void SetCoolingTarget(double target) {
        lock (_gate) {
            _coolingTargetC = target;
        }
    }

    private readonly record struct CoolingTelemetry(double Temperature, double? CoolerPowerPct, bool? CoolerOn);

    /// <summary>A direct (uncached) sensor read for the ramp's decisions — the §32.4 cache can be a
    /// refresh pass behind. Null when the camera is not connected.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported or failing temperature/power/cooler read (or a client disposed by a concurrent disconnect) degrades to unknown, which the stall timeout then bounds. Same pattern as ReadRuntime.")]
    private async Task<CoolingTelemetry?> ReadCoolingTelemetryAsync() {
        AlpacaCamera? client;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            return null;
        }
        return await Task.Run(() => {
            double temperature;
            try { temperature = client.CCDTemperature; } catch (Exception) { temperature = double.NaN; }
            double? power;
            try { power = client.CoolerPower; } catch (Exception) { power = null; }
            bool? coolerOn;
            try { coolerOn = client.CoolerOn; } catch (Exception) { coolerOn = null; }
            return new CoolingTelemetry(temperature, power, coolerOn);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public void RegisterCaptureBlock(ICameraConsumer cameraConsumer) { }
    public void ReleaseCaptureBlock(ICameraConsumer cameraConsumer) { }
    public bool IsFreeToCapture(ICameraConsumer cameraConsumer) => Volatile.Read(ref _captureInFlight) == 0;
    public void RegisterCaptureBlock(object cameraConsumer) { }
    public void ReleaseCaptureBlock(object cameraConsumer) { }
    public bool IsFreeToCapture(object cameraConsumer) => Volatile.Read(ref _captureInFlight) == 0;

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instructions never
    // call them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ICameraConsumer consumer) { }
    public void RemoveConsumer(ICameraConsumer consumer) { }
    public void Broadcast(CameraInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "CameraService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo()/CaptureImage and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, EventArgs, Task>? DownloadTimeout;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequencer CoolCamera: target {TargetC} °C over {Duration}")]
    private partial void LogSequencerCoolStart(double targetC, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequencer WarmCamera over {Duration}")]
    private partial void LogSequencerWarmStart(TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequencer WarmCamera stopped short of ambient ({Reason}); switching the cooler off anyway")]
    private partial void LogSequencerWarmShort(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sequencer {Operation} failed: {Reason}")]
    private partial void LogSequencerCoolingFailed(string operation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sequencer cooling cancelled; holding the set-point at the sensor temperature failed")]
    private partial void LogSequencerCoolingHoldFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequencer capture {FrameId} complete ({ImageType}, target '{Target}')")]
    private partial void LogSequencerCaptureComplete(Guid frameId, string imageType, string target);
}
