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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Image.FileFormat.FITS;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Decodes a fetched guide frame (FITS on disk) into 16-bit pixels. A seam so the loop's
/// measurement path is unit-testable without CFITSIO; the production decoder reads through
/// <see cref="CFitsioFITSReader"/>.</summary>
public interface IGuideFrameDecoder {
    (ushort[] Pixels, int Width, int Height) Decode(string path);
}

public sealed class CfitsioGuideFrameDecoder : IGuideFrameDecoder {
    public (ushort[] Pixels, int Width, int Height) Decode(string path) {
        using var reader = new CFitsioFITSReader(path);
        return (reader.ReadAllPixelsAsUshort(), reader.Width, reader.Height);
    }
}

/// <summary>The guide-camera focus loop's control surface (the §45 polar-align routine stops a running
/// loop before it takes the guide camera).</summary>
public interface IGuideFocusService {
    bool IsActive { get; }
    Task StartAsync(GuideFocusStartRequestDto request, CancellationToken ct);
    Task StopAsync();
    GuideFocusStatusDto GetStatus();
    (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame();
}

/// <summary>
/// Focus assistant for the GUIDE camera (Setup → Smart Focus, second card). Ara never opens the guide camera
/// itself — on an off-axis guider or a guide scope it is the guider daemon's device, and two Alpaca clients
/// driving one sensor is exactly the failure that broke guiding when the guide camera was connected as the
/// main camera. Instead the loop borrows frames THROUGH the guider the way polar alignment does (§45
/// capture-fetch): take the daemon's single-client PA-session lease, ask for one <c>capture_single_frame</c>
/// at a time, download the saved FITS over the daemon's HTTP capture endpoint, measure it here (the §59
/// star detector: HFR, star count, peak, FWHM) and render it for the live view. The user turns the guide
/// scope's or OAG's helical focuser by hand while the HFR readout and trend fall; there is no motorised
/// guide focuser to sweep, so this is a readout, not an autofocus.
///
/// One loop at a time; refuses while the guider is guiding/calibrating (the daemon owns the camera then) and
/// while polar alignment runs (same lease). Repeated capture failures end the loop in <c>error</c> rather
/// than spinning forever against a dropped camera.
/// </summary>
public sealed partial class GuideFocusService : IGuideFocusService, IDisposable {

    internal const double MinExposureSec = 0.05;
    internal const double MaxExposureSec = 30.0;
    internal const int MaxConsecutiveFailures = 5;
    internal const int RecentWindow = 240;
    private const int LeaseTimeoutSeconds = 600;
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(240);

    private readonly GuiderService _guider;
    private readonly IPolarAlignFrameFetcher _fetcher;
    private readonly IGuideFrameDecoder _decoder;
    private readonly Func<bool>? _polarAlignActive;
    private readonly ILogger<GuideFocusService> _logger;
    // Development only (SyntheticSky): frames rendered locally instead of borrowed through the guider.
    private readonly Func<(ushort[] Pixels, int Width, int Height)>? _syntheticFrames;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _gate = new();

    private string _state = "idle";
    private double _exposureSec;
    private long _seq;
    private DateTimeOffset? _started;
    private GuideFocusSampleDto? _latest;
    private double? _bestHfr;
    private long? _bestSeq;
    private readonly Queue<GuideFocusSampleDto> _recent = new();
    private string? _error;
    private int _consecutiveFailures;
    private byte[]? _frame;
    private long _frameSeq;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public GuideFocusService(
            GuiderService guider,
            IPolarAlignFrameFetcher fetcher,
            IGuideFrameDecoder? decoder = null,
            Func<bool>? polarAlignActive = null,
            ILogger<GuideFocusService>? logger = null,
            Func<(ushort[] Pixels, int Width, int Height)>? syntheticFrames = null) {
        _guider = guider ?? throw new ArgumentNullException(nameof(guider));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _decoder = decoder ?? new CfitsioGuideFrameDecoder();
        _polarAlignActive = polarAlignActive;
        _logger = logger ?? NullLogger<GuideFocusService>.Instance;
        _syntheticFrames = syntheticFrames;
    }

    public bool IsActive {
        get {
            lock (_gate) {
                return _state == "running";
            }
        }
    }

    /// <summary>Validate a start request before anything touches the guider. Pure — unit-tested.</summary>
    internal static void Validate(GuideFocusStartRequestDto request) {
        ArgumentNullException.ThrowIfNull(request);
        if (!(request.ExposureSec >= MinExposureSec && request.ExposureSec <= MaxExposureSec)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.ExposureSec,
                $"exposure must be {MinExposureSec}–{MaxExposureSec} s.");
        }
        if (request.Binning is int b && b < 1) {
            throw new ArgumentOutOfRangeException(nameof(request), b, "binning must be >= 1.");
        }
    }

    /// <summary>The daemon owns its camera while it guides or calibrates; the focus loop must not pull frames
    /// out from under it. Pure — unit-tested against the PHD2 app-state tokens.</summary>
    internal static bool GuiderBusy(string? appState) => appState switch {
        "Guiding" or "Calibrating" or "LostLock" or "Paused" => true,
        _ => false,
    };

    public async Task StartAsync(GuideFocusStartRequestDto request, CancellationToken ct) {
        Validate(request);
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive) {
                throw new InvalidOperationException("the guide-camera focus loop is already running");
            }
            if (_polarAlignActive?.Invoke() == true) {
                throw new InvalidOperationException("polar alignment is using the guide camera — stop it first");
            }
            PHD2Guider? guider = null;
            if (_syntheticFrames is null) {
                guider = _guider.RequireConnectedGuider(); // "guider is not connected" → 409
                if (GuiderBusy(guider.State)) {
                    throw new InvalidOperationException($"the guider is {guider.State.ToLowerInvariant()} — stop guiding before focusing the guide camera");
                }
                // The daemon's single-client lease: refused by the daemon too when it is guiding/calibrating.
                await guider.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
            }
            lock (_gate) {
                _state = "running";
                _exposureSec = request.ExposureSec;
                _started = DateTimeOffset.UtcNow;
                _latest = null;
                _bestHfr = null;
                _bestSeq = null;
                _recent.Clear();
                _error = null;
                _consecutiveFailures = 0;
                _frame = null;
            }
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loop = Task.Run(() => RunLoopAsync(guider, request, token), CancellationToken.None);
            LogStarted(request.ExposureSec);
        } finally {
            _opLock.Release();
        }
    }

    public async Task StopAsync() {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try {
            var (cts, loop) = (_loopCts, _loop);
            _loopCts = null;
            _loop = null;
            if (cts is not null) {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            if (loop is not null) {
                // The loop finishes its in-flight capture (≤ exposure + fetch) so the daemon never owes a
                // SingleFrameComplete to a listener that is gone — a later polar-align run would adopt it.
                try {
                    await loop.WaitAsync(TimeSpan.FromSeconds(MaxExposureSec + 30)).ConfigureAwait(false);
                } catch (TimeoutException) {
                    LogStopTimedOut();
                }
            }
            cts?.Dispose();
            bool wasRunning;
            lock (_gate) {
                wasRunning = _state == "running";
                if (wasRunning) {
                    _state = "stopped";
                }
            }
            if (wasRunning) {
                await ReleaseLeaseQuietlyAsync().ConfigureAwait(false);
                LogStopped();
            }
        } finally {
            _opLock.Release();
        }
    }

    public GuideFocusStatusDto GetStatus() {
        lock (_gate) {
            return new GuideFocusStatusDto(
                Active: _state == "running",
                State: _state,
                ExposureSec: _exposureSec,
                Seq: _seq,
                StartedUtc: _started,
                Latest: _latest,
                BestHfr: _bestHfr,
                BestSeq: _bestSeq,
                Recent: _recent.ToArray(),
                Error: _error,
                ConsecutiveFailures: _consecutiveFailures,
                HasFrame: _frame is not null);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1024:Use properties where appropriate",
        Justification = "Snapshot of a mutable multi-KB buffer taken under the lock; mirrors CameraService.GetLiveViewFrame.")]
    public (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame() {
        lock (_gate) {
            return _frame is null ? null : (_frame, _frameSeq);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Loop boundary: a capture, download, decode or detector fault is one failed frame (counted; the loop ends in `error` after MaxConsecutiveFailures), never a faulted background task.")]
    private async Task RunLoopAsync(PHD2Guider? guider, GuideFocusStartRequestDto request, CancellationToken ct) {
        var workDir = Path.Combine(Path.GetTempPath(), "ara-guide-focus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var lastRenew = DateTimeOffset.UtcNow;
        try {
            while (!ct.IsCancellationRequested) {
                if (guider is not null && DateTimeOffset.UtcNow - lastRenew > LeaseRenewInterval) {
                    try {
                        await guider.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                        lastRenew = DateTimeOffset.UtcNow;
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        LogLeaseRenewFailed(ex);
                    }
                }
                try {
                    var sample = guider is null
                        ? await SyntheticSampleAsync(request, ct).ConfigureAwait(false)
                        : await CaptureAndMeasureAsync(guider, request, workDir, ct).ConfigureAwait(false);
                    Record(sample.Sample, sample.Jpeg);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    LogFrameFailed(ex);
                    if (RecordFailure(ex.Message) >= MaxConsecutiveFailures) {
                        lock (_gate) {
                            _state = "error";
                            _error = $"gave up after {MaxConsecutiveFailures} failed frames — last: {ex.Message}";
                        }
                        LogGaveUp(MaxConsecutiveFailures, ex.Message);
                        await ReleaseLeaseQuietlyAsync().ConfigureAwait(false);
                        return;
                    }
                    // Back off a little so a dropped camera isn't hammered at the RPC rate.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
        } catch (OperationCanceledException) {
            // Stop — the status flips to stopped under StopAsync.
        } finally {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Development only: one synthetic frame, paced like an exposure.
    private async Task<(GuideFocusSampleDto Sample, byte[]? Jpeg)> SyntheticSampleAsync(GuideFocusStartRequestDto request, CancellationToken ct) {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(request.ExposureSec, 0.05, 1.0)), ct).ConfigureAwait(false);
        var (pixels, width, height) = _syntheticFrames!();
        var sample = Measure(pixels, width, height, NextSeq(), DateTimeOffset.UtcNow);
        var (jpeg, _, _) = CameraService.RenderLiveFrame(pixels, width, height, bayerPattern: null, annotate: true);
        return (sample, jpeg);
    }

    private async Task<(GuideFocusSampleDto Sample, byte[]? Jpeg)> CaptureAndMeasureAsync(
            PHD2Guider guider, GuideFocusStartRequestDto request, string workDir, CancellationToken ct) {
        var tcs = new TaskCompletionSource<SingleFrameCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnComplete(object? sender, SingleFrameCompleteEventArgs e) => tcs.TrySetResult(e);
        guider.SingleFrameComplete += OnComplete;
        string? localPath = null;
        try {
            var exposureMs = Math.Max(1, (int)Math.Round(request.ExposureSec * 1000.0));
            await guider.CaptureSolverFrameAsync(
                exposureMs: exposureMs, binning: request.Binning is > 1 ? request.Binning : null,
                gain: null, subframe: null, path: null, save: true, ct).ConfigureAwait(false);
            // Not cancellable mid-exposure on purpose (see StopAsync): the daemon owes exactly one event.
            var completed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(request.ExposureSec + 30), CancellationToken.None).ConfigureAwait(false);
            var filename = completed.Filename ?? (completed.Path is { Length: > 0 } p ? Path.GetFileName(p) : null);
            if (!completed.Success || string.IsNullOrEmpty(filename)) {
                throw new InvalidOperationException(completed.Error ?? "the guider saved no frame");
            }
            var host = guider.ConnectedHost;
            if (string.IsNullOrEmpty(host)) {
                throw new InvalidOperationException("guider connection endpoint unknown — cannot fetch the saved frame");
            }
            localPath = Path.Combine(workDir, filename);
            await _fetcher.FetchAsync(host, guider.ConnectedRpcPort, filename, localPath, CancellationToken.None).ConfigureAwait(false);
            var (pixels, width, height) = _decoder.Decode(localPath);
            var sample = Measure(pixels, width, height, NextSeq(), DateTimeOffset.UtcNow);
            byte[]? jpeg = null;
            if (width >= 16 && height >= 16) {
                (jpeg, _, _) = CameraService.RenderLiveFrame(pixels, width, height, bayerPattern: null, annotate: true);
            }
            return (sample, jpeg);
        } finally {
            guider.SingleFrameComplete -= OnComplete;
            if (localPath is not null) {
                try { File.Delete(localPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Measure one guide frame with the §59 star detector. Pure — unit-tested on synthetic stars.
    /// HFR is the detector's average over every star it found; a starless frame reads HFR 0, Stars 0.</summary>
    internal static GuideFocusSampleDto Measure(ushort[] pixels, int width, int height, long seq, DateTimeOffset at) {
        var result = StarDetector.Detect(
            pixels, width, height,
            new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true },
            CancellationToken.None);
        double peak = 0, fwhmSum = 0;
        foreach (var star in result.StarList) {
            peak = Math.Max(peak, star.MaxBrightness);
            fwhmSum += star.FWHM;
        }
        var stars = result.DetectedStars;
        var hfr = stars > 0 && double.IsFinite(result.AverageHFR) && result.AverageHFR > 0 ? result.AverageHFR : 0.0;
        var fwhm = result.StarList.Count > 0 ? fwhmSum / result.StarList.Count : 0.0;
        return new GuideFocusSampleDto(seq, at, Math.Round(hfr, 3), stars, Math.Round(peak, 0), Math.Round(double.IsFinite(fwhm) ? fwhm : 0.0, 3));
    }

    private long NextSeq() {
        lock (_gate) {
            return ++_seq;
        }
    }

    private void Record(GuideFocusSampleDto sample, byte[]? jpeg) {
        lock (_gate) {
            _latest = sample;
            _consecutiveFailures = 0;
            _error = null;
            _recent.Enqueue(sample);
            while (_recent.Count > RecentWindow) {
                _recent.Dequeue();
            }
            // "Best so far" needs ≥ 2 stars: a single hot pixel measured as a star reads as a perfect HFR.
            if (sample.Stars >= 2 && sample.Hfr > 0 && (_bestHfr is null || sample.Hfr < _bestHfr)) {
                _bestHfr = sample.Hfr;
                _bestSeq = sample.Seq;
            }
            if (jpeg is not null) {
                _frame = jpeg;
                _frameSeq = sample.Seq;
            }
        }
    }

    private int RecordFailure(string message) {
        lock (_gate) {
            _error = message;
            return ++_consecutiveFailures;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lease release: it auto-expires on the daemon regardless; a dropped guider must not fault the stop.")]
    private async Task ReleaseLeaseQuietlyAsync() {
        if (_syntheticFrames is not null) {
            return;
        }
        try {
            var guider = _guider.RequireConnectedGuider();
            await guider.SetPaSessionAsync(active: false, timeoutS: null, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogLeaseReleaseFailed(ex);
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }
        _loopCts?.Dispose();
        _opLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Guide-camera focus loop started ({ExposureSec}s frames through the guider).")]
    private partial void LogStarted(double exposureSec);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guide-camera focus loop stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: the in-flight capture did not finish within the stop grace — a late SingleFrameComplete may still arrive.")]
    private partial void LogStopTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: frame failed.")]
    private partial void LogFrameFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Guide-camera focus loop gave up after {Failures} consecutive failed frames: {Reason}")]
    private partial void LogGaveUp(int failures, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: lease renew failed (captures will fail if it expires).")]
    private partial void LogLeaseRenewFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: lease release failed (it expires on its own).")]
    private partial void LogLeaseReleaseFailed(Exception ex);
}
