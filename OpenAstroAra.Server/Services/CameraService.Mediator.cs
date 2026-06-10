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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e capture-path PRb — the real <see cref="CameraService"/> also serves the Sequencer's
/// <see cref="ICameraMediator"/> (live <c>GetInfo()</c> for <c>TakeExposure.Validate</c> + the
/// camera-control instructions' guards) and <see cref="IImagingMediator"/>, whose
/// <see cref="CaptureImage"/> runs the SAME §14e pipeline as the REST endpoint (expose → download →
/// §72 FITS → §28 catalog) and returns an inert <see cref="IExposureData"/> sentinel — the frame is
/// already persisted server-side, and the WPF-era in-memory image pipeline
/// (<see cref="CaptureAndPrepareImage"/>/<see cref="PrepareImage(IImageData, PrepareImageParameters, CancellationToken)"/>/live view)
/// stays <see cref="NotSupportedException"/> until the §2105 image pipeline lands.
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
                    $"timed out after {CaptureGateMaxWait.TotalMinutes:0} min waiting for an in-progress capture to release the camera; the in-flight gate may be stuck — check the daemon log");
            }
            await Task.Delay(CaptureGatePollInterval, token).ConfigureAwait(false);
        }
        try {
            // Re-snapshot under the gate: a disconnect/reconnect during a long queue wait can
            // supersede the client we grabbed before the loop, and we must not issue ASCOM commands
            // (ApplyExposureSettings/StartExposure) against a stale connection — fast-fail instead.
            lock (_gate) {
                client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            }
            if (client is null) {
                throw new InvalidOperationException("camera disconnected while the capture was queued");
            }
            var frameId = Guid.NewGuid();
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
    // NINA flows) lands as Dark, matching the FITS IMAGETYP it carries.
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

    // ── Unported IImagingMediator surface — the §2105 image pipeline lands these ────────────────
    public Task<IRenderedImage> CaptureAndPrepareImage(CaptureSequence sequence, PrepareImageParameters parameters, CancellationToken token, IProgress<ApplicationStatus>? progress) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("the in-memory render pipeline is not ported (§2105)"));
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

    public bool AtTargetTemp => false;
    public double TargetTemp => double.NaN;

    public Task<bool> CoolCamera(double temperature, TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) =>
        Task.FromResult(false); // ramped cooling orchestration rides with the capture-path engine wiring
    public Task<bool> WarmCamera(TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) =>
        Task.FromResult(false);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Sequencer capture {FrameId} complete ({ImageType}, target '{Target}')")]
    private partial void LogSequencerCaptureComplete(Guid frameId, string imageType, string target);
}
