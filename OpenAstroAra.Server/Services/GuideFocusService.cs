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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Decodes a fetched guide frame (FITS on disk) into 16-bit pixels. A seam so the loop's
/// measurement path is unit-testable without CFITSIO; the production decoder reads through the
/// daemon's own <see cref="OpenAstroAra.Fits.FitsImage"/> binding (the one polar alignment uses for
/// the same guider frames), never the NINA-era reader whose <c>cfitsionative.dll</c> is not shipped
/// on the Pi — every live-focus frame failed to decode there (2026-10-03).</summary>
public interface IGuideFrameDecoder {
    (ushort[] Pixels, int Width, int Height) Decode(string path);
}

public sealed class CfitsioGuideFrameDecoder : IGuideFrameDecoder {
    public (ushort[] Pixels, int Width, int Height) Decode(string path) {
        // The guider writes 16-bit unsigned FITS; ReadImageData16 asks CFITSIO for TUSHORT, so a
        // float or signed frame still comes back scaled into the ushort plane.
        using var fits = OpenAstroAra.Fits.FitsImage.Open(path);
        var (width, height) = fits.GetDimensions();
        return (fits.ReadImageData16(), width, height);
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
    // The guide optics (focal length mm, pixel µm, aperture mm) read from the profile at status time.
    private readonly Func<(double FocalLengthMm, double PixelSizeUm, double ApertureMm)?>? _optics;
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
    private string? _stopReason;
    private bool _inFocusHeld;
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
            Func<(ushort[] Pixels, int Width, int Height)>? syntheticFrames = null,
            Func<(double FocalLengthMm, double PixelSizeUm, double ApertureMm)?>? optics = null) {
        _guider = guider ?? throw new ArgumentNullException(nameof(guider));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _decoder = decoder ?? new CfitsioGuideFrameDecoder();
        _polarAlignActive = polarAlignActive;
        _logger = logger ?? NullLogger<GuideFocusService>.Instance;
        _syntheticFrames = syntheticFrames;
        _optics = optics;
    }

    /// <summary>Seeing FWHM assumed for the expected-HFR target; a typical backyard night.</summary>
    internal const double AssumedSeeingArcsec = 3.0;
    /// <summary>What the §59 detector reads for a star smaller than a pixel: the HFR floor on an
    /// undersampled guide camera (0.76 px measured on a 6.4"/px guide scope, 2026-10-03).</summary>
    internal const double DetectorHfrFloorPx = 0.7;
    /// <summary>Stars used for the per-frame HFR: the brightest few persist frame to frame, so the
    /// readout no longer jumps when faint stars flicker across the detection threshold.</summary>
    internal const int HfrStarCount = 12;
    /// <summary>How far above the expected HFR still counts as in focus (the client uses the same).</summary>
    internal const double TargetTolerance = 1.3;
    /// <summary>The loop stops itself once the MEDIAN HFR over this many frames is at or under the target:
    /// a median, not a streak, because seeing throws single frames well above it (an OAG at 3000 mm
    /// would never hold ten clean frames in a row).</summary>
    internal const int InFocusHoldFrames = 10;
    /// <summary>The stop reason a self-ended loop reports.</summary>
    internal const string StopReasonInFocus = "in_focus";

    /// <summary>
    /// The HFR an in-focus star should read: seeing and the aperture's diffraction added in quadrature,
    /// scaled to pixels (HFR ≈ FWHM / 2 for a Gaussian), never below the detector's floor. Null without
    /// a focal length and pixel size. Pure.
    /// </summary>
    internal static (double ExpectedHfrPx, double PlateScaleArcsec)? ExpectedInFocusHfr(double focalLengthMm, double pixelSizeUm, double apertureMm) {
        if (!(focalLengthMm > 0) || !(pixelSizeUm > 0) || !double.IsFinite(focalLengthMm) || !double.IsFinite(pixelSizeUm)) {
            return null;
        }
        var scale = 206.265 * pixelSizeUm / focalLengthMm;
        // Airy FWHM ≈ 1.02 λ/D at 550 nm, in arcseconds.
        var airy = apertureMm > 0 ? 1.02 * 550e-9 / (apertureMm / 1000.0) * 206265.0 : 0.0;
        var fwhm = Math.Sqrt(AssumedSeeingArcsec * AssumedSeeingArcsec + airy * airy);
        var hfr = Math.Max(DetectorHfrFloorPx, 0.5 * fwhm / scale);
        return (Math.Round(hfr, 2), Math.Round(scale, 2));
    }

    private (double ExpectedHfrPx, double PlateScaleArcsec)? ExpectedQuietly() {
        try {
            return _optics?.Invoke() is { } o ? ExpectedInFocusHfr(o.FocalLengthMm, o.PixelSizeUm, o.ApertureMm) : null;
        } catch (Exception ex) when (ex is InvalidOperationException or IOException) {
            return null;
        }
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
                // A fresh run: the frame counter, the trend and the picture all start over, so the
                // frames from before a refocus cannot sit in the chart or hold the "best".
                _seq = 0;
                _frame = null;
                _frameSeq = 0;
                _stopReason = null;
                _inFocusHeld = false;
                _recent.Clear();
                _error = null;
                _consecutiveFailures = 0;
                _frame = null;
            }
            // A loop that gave up in `error` left its source behind; this Start replaces it.
            _loopCts?.Dispose();
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
        var expected = ExpectedQuietly();
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
                HasFrame: _frame is not null,
                ExpectedHfr: expected?.ExpectedHfrPx,
                PlateScaleArcsec: expected?.PlateScaleArcsec,
                StopReason: _stopReason);
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
        var lastRenew = DateTimeOffset.UtcNow;
        try {
            Directory.CreateDirectory(workDir);
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
                    if (InFocusHeld(ExpectedQuietly()?.ExpectedHfrPx)) {
                        double median;
                        lock (_gate) {
                            _state = "stopped";
                            _stopReason = StopReasonInFocus;
                            median = MedianRecentHfr(InFocusHoldFrames);
                        }
                        LogInFocusStopped(InFocusHoldFrames, median);
                        await ReleaseLeaseQuietlyAsync().ConfigureAwait(false);
                        return;
                    }
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
        } catch (Exception ex) {
            // Anything outside the per-frame boundary (the work directory, a lease renew that threw past
            // its own catch) ends the loop in `error` with the lease released, never stuck at `running`.
            lock (_gate) {
                _state = "error";
                _error = ex.Message;
            }
            LogLoopFaulted(ex);
            await ReleaseLeaseQuietlyAsync().ConfigureAwait(false);
        } finally {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Development only: one synthetic frame, paced like an exposure.
    private async Task<(GuideFocusSampleDto Sample, byte[]? Jpeg)> SyntheticSampleAsync(GuideFocusStartRequestDto request, CancellationToken ct) {
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(request.ExposureSec, 0.05, 1.0)), ct).ConfigureAwait(false);
        var (pixels, width, height) = _syntheticFrames!();
        var (jpeg, _, _) = CameraService.RenderLiveFrame(pixels, width, height, bayerPattern: null, annotate: true);
        // The seq is taken last: a frame that fails to render never advances it past the stored picture.
        var sample = Measure(pixels, width, height, NextSeq(), DateTimeOffset.UtcNow);
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
            // Only the bare name: an absolute or ../ value from the guider must not place the download (or the
            // delete in the finally) outside workDir.
            var filename = Path.GetFileName(completed.Filename ?? completed.Path ?? "");
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
            byte[]? jpeg = null;
            if (width >= 16 && height >= 16) {
                (jpeg, _, _) = CameraService.RenderLiveFrame(pixels, width, height, bayerPattern: null, annotate: true);
            }
            // The seq is taken last: a frame that fails to render never advances it past the stored picture,
            // which would make the client refetch the same JPEG on every poll.
            var sample = Measure(pixels, width, height, NextSeq(), DateTimeOffset.UtcNow);
            return (sample, jpeg);
        } finally {
            guider.SingleFrameComplete -= OnComplete;
            if (localPath is not null) {
                try { File.Delete(localPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Measure one guide frame with the §59 star detector. Pure — unit-tested on synthetic stars.
    /// HFR is the median over the <see cref="HfrStarCount"/> brightest stars (the detector sorts
    /// brightest-first); a starless frame reads HFR 0, Stars 0.</summary>
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
        var hfr = stars > 0 ? MedianHfrOfBrightest(result.StarList, HfrStarCount) : 0.0;
        if (!double.IsFinite(hfr) || hfr < 0) {
            hfr = 0.0;
        }
        var fwhm = result.StarList.Count > 0 ? fwhmSum / result.StarList.Count : 0.0;
        return new GuideFocusSampleDto(seq, at, Math.Round(hfr, 3), stars, Math.Round(peak, 0), Math.Round(double.IsFinite(fwhm) ? fwhm : 0.0, 3));
    }

    internal static double MedianHfrOfBrightest(IReadOnlyList<DetectedStar> brightestFirst, int count) {
        var n = Math.Min(count, brightestFirst.Count);
        if (n == 0) {
            return 0.0;
        }
        var hfrs = new double[n];
        for (int i = 0; i < n; i++) {
            hfrs[i] = brightestFirst[i].HFR;
        }
        Array.Sort(hfrs);
        return n % 2 == 1 ? hfrs[n / 2] : 0.5 * (hfrs[n / 2 - 1] + hfrs[n / 2]);
    }

    private long NextSeq() {
        lock (_gate) {
            return ++_seq;
        }
    }

    /// <summary>True once the median HFR of the last <see cref="InFocusHoldFrames"/> measurable frames
    /// (≥ 2 stars) is at or under <paramref name="expectedHfr"/> × <see cref="TargetTolerance"/>.
    /// Sticky for the run so the loop stops exactly once.</summary>
    internal bool InFocusHeld(double? expectedHfr) {
        if (expectedHfr is not > 0) {
            return false;
        }
        lock (_gate) {
            if (_inFocusHeld) {
                return true;
            }
            if (_recent.Count < InFocusHoldFrames) {
                return false;
            }
            var median = MedianRecentHfr(InFocusHoldFrames);
            _inFocusHeld = median > 0 && median <= expectedHfr.Value * TargetTolerance;
            return _inFocusHeld;
        }
    }

    // Caller holds _gate. Median over the last `count` samples with ≥ 2 stars; 0 when fewer than
    // `count` of them are measurable (a starless stretch must not read as focus).
    private double MedianRecentHfr(int count) {
        var hfrs = new List<double>(count);
        foreach (var s in _recent.Reverse()) {
            if (s.Stars >= 2 && s.Hfr > 0) {
                hfrs.Add(s.Hfr);
                if (hfrs.Count == count) {
                    break;
                }
            }
        }
        if (hfrs.Count < count) {
            return 0;
        }
        hfrs.Sort();
        return count % 2 == 1 ? hfrs[count / 2] : 0.5 * (hfrs[count / 2 - 1] + hfrs[count / 2]);
    }

    internal void Record(GuideFocusSampleDto sample, byte[]? jpeg) {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Guide focus: in focus — the median HFR over the last {Frames} frames is {Median:0.00} px, at or under the target; loop stopped")]
    private partial void LogInFocusStopped(int frames, double median);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: the in-flight capture did not finish within the stop grace — a late SingleFrameComplete may still arrive.")]
    private partial void LogStopTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: frame failed.")]
    private partial void LogFrameFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Guide-camera focus loop gave up after {Failures} consecutive failed frames: {Reason}")]
    private partial void LogGaveUp(int failures, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Guide-camera focus loop stopped on an unexpected fault")]
    private partial void LogLoopFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: lease renew failed (captures will fail if it expires).")]
    private partial void LogLeaseRenewFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide-camera focus loop: lease release failed (it expires on its own).")]
    private partial void LogLeaseReleaseFailed(Exception ex);
}
