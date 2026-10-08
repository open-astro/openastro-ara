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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The main telescope's optics as the Bahtinov readout needs them: the working focal ratio (the
/// reducer applied) and the camera's unbinned pixel size.</summary>
public sealed record BahtinovOptics(double FocalRatio, double PixelSizeUm);

/// <summary>The Bahtinov readout's control surface: what the REST endpoints and the run guards need.</summary>
public interface IBahtinovFocusService {
    bool IsActive { get; }
    Task StartAsync(BahtinovFocusStartRequestDto request, CancellationToken ct);
    /// <summary>Stop the readout (idempotent; waits for the in-flight frame to drain).</summary>
    Task StopAsync();
    BahtinovFocusStatusDto GetStatus();
    /// <summary>The latest frame as JPEG — the star crop the overlay is drawn over, or the whole frame
    /// (auto-stretched, stars ringed) when nothing was measured — with its sequence number; null before the
    /// first frame.</summary>
    (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame();
}

/// <summary>
/// Setup → Smart Focus → Main telescope → Bahtinov mask (#1299): a live readout of the focus error while
/// the user turns the focuser by hand with the mask on. Each frame comes through the §59 analysis seam (the
/// same device path and in-flight gate as every capture, nothing persisted), <see cref="BahtinovAnalyzer"/>
/// fits the three spikes on the brightest star, and the readout keeps the signed offset of the central spike,
/// its estimate in micrometres of focuser travel and whether that sits inside the critical focus zone.
///
/// The star is locked once found (the next frame searches near it), and the sign convention is fixed by the
/// first measurement, so the number keeps one sign on one side of focus for the session. Which way that is
/// on the focuser depends on how the mask sits and how the image is flipped — the daemon cannot know it — so
/// the client turns the history into advice relative to the user's last move, as the guide-camera loop does.
///
/// A frame without a pattern (mask not yet fitted, clouds) is a sample with <c>Detected</c> false, never an
/// error; only the camera failing <see cref="MaxConsecutiveFailures"/> times in a row ends the readout in
/// <c>error</c>. The readout is attended by nature (the mask goes on and must come off), so it refuses to
/// start while an autofocus run or a sequence has the camera, and those refuse to start while it runs.
/// </summary>
public sealed partial class BahtinovFocusService : IBahtinovFocusService, IDisposable {

    internal const int MaxConsecutiveFailures = 3;
    internal const int RecentWindow = 120;
    internal const double MinExposureSeconds = 0.05;
    internal const double MaxExposureSeconds = 30;
    internal const int MaxBinning = 4;
    /// <summary>The in-focus limit on the offset when the profile has no optics: half a pixel.</summary>
    internal const double FallbackZonePx = 0.5;
    /// <summary>Frames without the star before the lock is dropped and the whole frame searched again.</summary>
    internal const int LockLostAfter = 5;

    private readonly IAnalysisFrameSource _frames;
    private readonly Func<BahtinovOptics?> _optics;
    private readonly Func<string?> _busyReason;
    private readonly ILogger<BahtinovFocusService> _logger;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _gate = new();

    private string _state = "idle";
    private double _exposureSeconds;
    private int _binning = 1;
    private long _seq;
    private DateTimeOffset? _started;
    private BahtinovSampleDto? _latest;
    private readonly Queue<BahtinovSampleDto> _recent = new();
    private double? _best;
    private string? _error;
    private int _consecutiveFailures;
    private byte[]? _frame;
    private long _frameSeq;
    private (int X, int Y)? _lock;
    private int _misses;
    private (double X, double Y)? _normal;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    /// <param name="frames">The main camera (or the synthetic sky's mask star in development).</param>
    /// <param name="optics">The profile's working focal ratio and pixel size, or null when not set.</param>
    /// <param name="busyReason">Why the camera is not free for a readout (an autofocus run, a sequence), or
    /// null when it is.</param>
    public BahtinovFocusService(IAnalysisFrameSource frames, Func<BahtinovOptics?> optics,
            Func<string?>? busyReason = null, ILogger<BahtinovFocusService>? logger = null) {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _optics = optics ?? throw new ArgumentNullException(nameof(optics));
        _busyReason = busyReason ?? (() => null);
        _logger = logger ?? NullLogger<BahtinovFocusService>.Instance;
    }

    public bool IsActive {
        get {
            lock (_gate) {
                return _state == "running";
            }
        }
    }

    /// <summary>Micrometres of focuser travel per pixel of central-spike offset. A grating over part of the
    /// aperture throws its spike through the focal plane where that part's light lands, so with the sensor
    /// Δ from focus a spike moves by Δ × (its grating's centroid distance) / f. The central grating covers
    /// half the aperture (centroid 2D/3π off axis) and the crossing grating pair the other half, so the
    /// central spike sits ε ≈ 4Δ / (3π N) from the crossing at focal ratio N, i.e. Δ = ε · 3πN/4. (The
    /// crossing angle adds a ±tan θ / 2 term whose sign depends on how the mask is cut — about ±15% for a
    /// typical mask — so the estimate is good for "inside the zone or not", not to the micron.) Pure.</summary>
    internal static double MicronsPerPixel(BahtinovOptics optics, int binning) =>
        optics.PixelSizeUm * Math.Max(1, binning) * 3 * Math.PI / 4 * optics.FocalRatio;

    /// <summary>Half the critical focus zone in µm (the autofocus step sizing's approximation), or null
    /// without a focal ratio. Pure.</summary>
    internal static double? ZoneUm(BahtinovOptics? optics) =>
        optics is { FocalRatio: > 0 } o ? AutofocusSweepService.CfzCoefficientUm * o.FocalRatio * o.FocalRatio / 2 : null;

    /// <summary>The in-focus limit on the offset in (binned) pixels: the critical focus zone through
    /// <see cref="MicronsPerPixel"/> when the optics are known, else <see cref="FallbackZonePx"/>. Pure.</summary>
    internal static (double Px, bool FromOptics) ZonePx(BahtinovOptics? optics, int binning) {
        if (optics is { FocalRatio: > 0, PixelSizeUm: > 0 } o && ZoneUm(o) is { } um) {
            return (um / MicronsPerPixel(o, binning), true);
        }
        return (FallbackZonePx, false);
    }

    /// <summary>The request's exposure (finite, 0.05–30 s). Pure.</summary>
    internal static double ResolveExposure(double requested) {
        if (!double.IsFinite(requested) || requested < MinExposureSeconds || requested > MaxExposureSeconds) {
            throw new ArgumentOutOfRangeException(nameof(requested), requested, $"exposure must be between {MinExposureSeconds} and {MaxExposureSeconds} seconds.");
        }
        return requested;
    }

    /// <summary>The request's binning: 1 when absent, else 1 … the camera's maximum (capped at 4; an unknown
    /// maximum allows up to 4). Pure.</summary>
    internal static int ResolveBinning(int? requested, int cameraMax) {
        var ceiling = cameraMax > 0 ? Math.Min(cameraMax, MaxBinning) : MaxBinning;
        if (requested is not { } b) {
            return 1;
        }
        if (b < 1 || b > ceiling) {
            throw new ArgumentOutOfRangeException(nameof(requested), b, $"binning must be between 1 and {ceiling} on this camera.");
        }
        return b;
    }

    public async Task StartAsync(BahtinovFocusStartRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var exposure = ResolveExposure(request.ExposureSec);
        var binning = ResolveBinning(request.Binning, _frames.MaxBinning);
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive) {
                throw new InvalidOperationException("the Bahtinov readout is already running");
            }
            if (_busyReason() is { } busy) {
                throw new InvalidOperationException($"the camera is busy: {busy}");
            }
            lock (_gate) {
                // Every start is a new session: a new star, a new sign convention, a new trend.
                _state = "running";
                _exposureSeconds = exposure;
                _binning = binning;
                _started = DateTimeOffset.UtcNow;
                _latest = null;
                _recent.Clear();
                _best = null;
                _error = null;
                _consecutiveFailures = 0;
                _frame = null;
                _frameSeq = 0;
                _lock = null;
                _misses = 0;
                _normal = null;
            }
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var loopCt = _loopCts.Token;
            _loop = Task.Run(() => RunLoopAsync(exposure, binning, loopCt), CancellationToken.None);
            LogStarted(exposure, binning);
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
                try {
                    // Let the in-flight exposure drain so the next camera user never collides with it.
                    await loop.WaitAsync(TimeSpan.FromSeconds(MaxExposureSeconds + 30)).ConfigureAwait(false);
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
                LogStopped();
            }
        } finally {
            _opLock.Release();
        }
    }

    public BahtinovFocusStatusDto GetStatus() {
        var optics = OpticsQuietly();
        lock (_gate) {
            var binning = Math.Max(1, _binning);
            var (zonePx, fromOptics) = ZonePx(optics, binning);
            return new BahtinovFocusStatusDto(
                Active: _state == "running",
                State: _state,
                ExposureSec: _exposureSeconds,
                Binning: binning,
                Seq: _seq,
                StartedUtc: _started,
                Latest: _latest,
                Recent: _recent.ToArray(),
                BestOffsetPx: _best,
                ZonePx: Math.Round(zonePx, 3),
                ZoneFromOptics: fromOptics,
                ZoneUm: ZoneUm(optics) is { } um ? Math.Round(um, 1) : null,
                FocalRatio: optics?.FocalRatio is > 0 and var f ? Math.Round(f, 2) : null,
                WithinZone: _latest?.WithinZone ?? false,
                Error: _error,
                ConsecutiveFailures: _consecutiveFailures,
                HasFrame: _frame is not null,
                FrameSeq: _frameSeq);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1024:Use properties where appropriate",
        Justification = "Snapshot of a mutable multi-KB buffer taken under the lock; mirrors GuideFocusService.GetFrame.")]
    public (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame() {
        lock (_gate) {
            return _frame is null ? null : (_frame, _frameSeq);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The optics come from the profile; a read fault must not take the readout down — no optics means the fallback zone.")]
    private BahtinovOptics? OpticsQuietly() {
        try {
            return _optics() is { FocalRatio: > 0, PixelSizeUm: > 0 } o && double.IsFinite(o.FocalRatio) && double.IsFinite(o.PixelSizeUm) ? o : null;
        } catch (Exception) {
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Loop boundary: a capture fault is one failed frame (counted; the readout ends in `error` after MaxConsecutiveFailures), never a faulted background task.")]
    private async Task RunLoopAsync(double exposureSeconds, int binning, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                AnalysisFrame frame;
                try {
                    frame = await _frames.CaptureForAnalysisAsync(exposureSeconds, binning, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    LogCaptureFaulted(ex);
                    int failures;
                    lock (_gate) {
                        failures = ++_consecutiveFailures;
                        if (failures >= MaxConsecutiveFailures) {
                            _state = "error";
                            _error = $"{failures} frames in a row failed ({ex.Message}) — check the camera, then start again";
                        }
                    }
                    if (failures >= MaxConsecutiveFailures) {
                        LogGaveUp(failures);
                        return;
                    }
                    continue;
                }
                (int X, int Y)? near;
                (double X, double Y)? normal;
                lock (_gate) {
                    near = _lock;
                    normal = _normal;
                }
                // The fit is a few tens of ms of arithmetic on a Pi 4; off the loop's thread so a status read
                // never waits on it.
                var (measurement, problem, jpeg) = await Task.Run(() => Measure(frame, near, normal), ct).ConfigureAwait(false);
                var optics = OpticsQuietly();
                lock (_gate) {
                    Record(frame, measurement, problem, jpeg, optics, binning);
                }
            }
        } catch (OperationCanceledException) {
            // stopped
        }
    }

    private (BahtinovMeasurement? Measurement, BahtinovProblem Problem, byte[]? Jpeg) Measure(
            AnalysisFrame frame, (int X, int Y)? near, (double X, double Y)? normal) {
        var m = BahtinovAnalyzer.Analyze(frame.Pixels.Span, frame.Width, frame.Height, out var problem, near, normal);
        // A star near the lock that no longer shows a pattern still deserves its crop (the user sees the
        // mask came off, or clouds); without a measurement the whole frame shows where the stars are.
        return (m, problem, m is null ? RenderQuietly(frame) : RenderCropQuietly(frame, m));
    }

    /// <summary>Append one frame's result. Caller holds <see cref="_gate"/>.</summary>
    private void Record(AnalysisFrame frame, BahtinovMeasurement? m, BahtinovProblem problem, byte[]? jpeg,
            BahtinovOptics? optics, int binning) {
        _seq++;
        _consecutiveFailures = 0;
        BahtinovSampleDto sample;
        if (m is null) {
            if (++_misses >= LockLostAfter) {
                _lock = null;
            }
            sample = new BahtinovSampleDto(_seq, frame.CapturedAt, Detected: false, ProblemToken(problem),
                OffsetPx: null, DefocusUm: null, WithinZone: false, StarX: _lock?.X ?? 0, StarY: _lock?.Y ?? 0, PeakAdu: 0);
        } else {
            _misses = 0;
            _lock = (m.StarX, m.StarY);
            _normal ??= (m.NormalX, m.NormalY);
            var (zonePx, _) = ZonePx(optics, binning);
            var offset = Math.Round(m.OffsetPx, 3);
            double? defocus = optics is not null ? Math.Round(m.OffsetPx * MicronsPerPixel(optics, binning), 1) : null;
            sample = new BahtinovSampleDto(_seq, frame.CapturedAt, Detected: true, Problem: null,
                offset, defocus, WithinZone: Math.Abs(m.OffsetPx) <= zonePx,
                m.StarX, m.StarY, m.PeakAdu, OverlayFor(m));
            if (_best is not { } best || Math.Abs(offset) < Math.Abs(best)) {
                _best = offset;
            }
            LogSample(offset, defocus ?? double.NaN);
        }
        // Only the latest sample carries the overlay; the trend window keeps the numbers.
        _latest = sample;
        _recent.Enqueue(sample with { Overlay = null });
        while (_recent.Count > RecentWindow) {
            _recent.Dequeue();
        }
        if (jpeg is not null) {
            _frame = jpeg;
            _frameSeq = _seq;
        }
    }

    private static string? ProblemToken(BahtinovProblem problem) => problem switch {
        BahtinovProblem.NoStar => "no_star",
        BahtinovProblem.NearEdge => "near_edge",
        BahtinovProblem.NoPattern => "no_pattern",
        _ => null,
    };

    /// <summary>The three fitted lines clipped to the crop square, and the X's crossing. Pure.</summary>
    internal static BahtinovOverlayDto OverlayFor(BahtinovMeasurement m) {
        var size = m.CropSize;
        // The rhos are measured from the star's centre in crop coordinates.
        var cx = m.StarX - m.CropLeft + 0.5;
        var cy = m.StarY - m.CropTop + 0.5;
        BahtinovLineDto Line(string role, BahtinovSpike s) {
            var (nx, ny) = BahtinovAnalyzer.Normal(s.AngleDeg);
            var px = cx + nx * s.RhoPx;
            var py = cy + ny * s.RhoPx;
            var a = s.AngleDeg * Math.PI / 180;
            var ux = Math.Cos(a);
            var uy = Math.Sin(a);
            // Clip p + t·u to [0, size]² (Liang–Barsky over the four edges).
            double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
            foreach (var (p, d) in new[] { (px, ux), (py, uy) }) {
                if (Math.Abs(d) < 1e-12) {
                    continue;
                }
                var ta = (0 - p) / d;
                var tb = (size - p) / d;
                t0 = Math.Max(t0, Math.Min(ta, tb));
                t1 = Math.Min(t1, Math.Max(ta, tb));
            }
            if (!double.IsFinite(t0) || !double.IsFinite(t1) || t1 < t0) {
                t0 = -size;
                t1 = size;
            }
            return new BahtinovLineDto(role,
                Math.Round(px + t0 * ux, 2), Math.Round(py + t0 * uy, 2),
                Math.Round(px + t1 * ux, 2), Math.Round(py + t1 * uy, 2),
                Math.Round(s.AngleDeg, 2));
        }
        return new BahtinovOverlayDto(size,
            [Line("outer", m.OuterA), Line("central", m.Central), Line("outer", m.OuterB)],
            Math.Round(m.IntersectionX, 2), Math.Round(m.IntersectionY, 2), Math.Round(m.SpreadDeg, 2));
    }

    // The star crop the overlay is drawn on: auto-stretched, no star rings (the lines are the annotation).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Cosmetic: a failed JPEG render must never fail the readout. Log-and-recover boundary.")]
    private byte[]? RenderCropQuietly(AnalysisFrame frame, BahtinovMeasurement m) {
        try {
            var size = m.CropSize;
            var crop = new ushort[size * size];
            var src = frame.Pixels.Span;
            for (var y = 0; y < size; y++) {
                src.Slice((m.CropTop + y) * frame.Width + m.CropLeft, size).CopyTo(crop.AsSpan(y * size, size));
            }
            var (jpeg, _, _) = CameraService.RenderLiveFrame(crop, size, size, bayerPattern: null, annotate: false);
            return jpeg;
        } catch (Exception ex) {
            LogRenderFailed(ex);
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Cosmetic: a failed JPEG render must never fail the readout. Log-and-recover boundary.")]
    private byte[]? RenderQuietly(AnalysisFrame frame) {
        if (frame.Width < 64 || frame.Height < 64) {
            return null;
        }
        try {
            // The capture's own buffer when it is a whole array (it is from the camera), so a frame without a
            // pattern does not cost a full-frame copy every time.
            var pixels = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
                && segment.Offset == 0 && segment.Array is { } array && array.Length == segment.Count
                ? array
                : frame.Pixels.ToArray();
            var (jpeg, _, _) = CameraService.RenderLiveFrame(pixels, frame.Width, frame.Height, bayerPattern: null, annotate: true);
            return jpeg;
        } catch (Exception ex) {
            LogRenderFailed(ex);
            return null;
        }
    }

    public void Dispose() {
        _disposed = true;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _opLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bahtinov readout started ({Exposure:0.##} s, {Binning}×{Binning})")]
    private partial void LogStarted(double exposure, int binning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bahtinov readout stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bahtinov readout: the in-flight frame did not drain in time — stopping anyway")]
    private partial void LogStopTimedOut();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Bahtinov readout: offset {Offset:+0.###;-0.###} px, {Defocus:0.#} µm")]
    private partial void LogSample(double offset, double defocus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bahtinov readout: a capture faulted (counted as a failed frame)")]
    private partial void LogCaptureFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bahtinov readout gave up after {Failures} consecutive failed frames")]
    private partial void LogGaveUp(int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bahtinov readout: could not render the frame — the readout continues without a picture")]
    private partial void LogRenderFailed(Exception ex);
}
