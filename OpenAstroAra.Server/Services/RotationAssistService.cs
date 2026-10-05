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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The readout cannot run on this rig as configured — no plate solver, no optics in the
/// profile. The REST start reports it as a conflict.</summary>
public class RotationAssistNotReadyException : InvalidOperationException {
    public RotationAssistNotReadyException() { }
    public RotationAssistNotReadyException(string message) : base(message) { }
    public RotationAssistNotReadyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The readout's control surface: what the REST endpoints need.</summary>
public interface IRotationAssistService {
    bool IsActive { get; }
    Task StartAsync(RotationAssistStartRequestDto request, CancellationToken ct);
    /// <summary>Stop the readout (idempotent; waits for the in-flight solve to drain).</summary>
    Task StopAsync();
    /// <summary>Done: stop the loop and take ONE 1×1 frame at the full plate-solve exposure; the readout ends
    /// <c>confirmed</c> (within tolerance) or <c>not_confirmed</c>.</summary>
    Task ConfirmAsync(CancellationToken ct);
    RotationAssistStatusDto GetStatus();
    /// <summary>The latest solved frame rendered as JPEG (auto-stretched, stars ringed) with its sequence
    /// number, or null before the first successful solve.</summary>
    (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame();
}

/// <summary>
/// The by-hand rotation readout (the daemon as a protractor), driven from the Plan screen's framing. A rig
/// without a rotator cannot act on a framing angle, so the user turns the camera while the daemon measures:
/// capture → plate-solve → the solved position angle and the signed, folded delta to the target. In
/// <c>loop</c> mode the readout repeats until stopped; in <c>single</c> mode it takes one frame, solves it
/// and ends in <c>stopped</c> with the result kept, so the user can turn, shoot again, and compare. The
/// exposure is the request's or, by default, the profile's plate-solve exposure. The client turns the delta
/// history into advice relative to the user's last move (the daemon cannot know which way "clockwise" turns
/// the sky on this optical train); that history survives a restart toward the SAME target, so single shots
/// still get "keep going / go back". One readout at a time; repeated solve failures end it in <c>error</c>
/// rather than spinning against a cloud.
///
/// Binning: the loop runs at the camera's largest square binning capped at 4 (a protractor needs no
/// resolution; binned frames download and solve several times faster, and a shorter exposure sees the same
/// stars). When the user says Done, <see cref="ConfirmAsync"/> takes one 1×1 frame at the profile's full
/// plate-solve exposure and judges THAT against the tolerance — the approval comes from a full-resolution
/// solve, never from the quick binned one.
/// </summary>
public sealed partial class RotationAssistService : IRotationAssistService, IDisposable {

    internal const int MaxConsecutiveFailures = 5;
    internal const int RecentWindow = 120;
    internal const double MinExposureSeconds = 0.01;
    internal const double MaxExposureSeconds = 60;
    internal const int MaxLoopBinning = 4;

    private readonly IPositionAngleSolver _solver;
    private readonly Func<double> _toleranceDeg;
    private readonly Func<double> _defaultExposureSeconds;
    private readonly ILogger<RotationAssistService> _logger;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _gate = new();

    private string _state = "idle";
    private string _mode = RotationAssistModes.Loop;
    private double _exposureSeconds;
    private int _binning = 1;
    private double? _target;
    private RotationAssistSampleDto? _confirmation;
    private long _seq;
    private DateTimeOffset? _started;
    private RotationAssistSampleDto? _latest;
    private readonly Queue<RotationAssistSampleDto> _recent = new();
    private string? _error;
    private int _consecutiveFailures;
    private byte[]? _frame;
    private long _frameSeq;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public RotationAssistService(IPositionAngleSolver solver, Func<double> toleranceDeg,
            Func<double>? defaultExposureSeconds = null, ILogger<RotationAssistService>? logger = null) {
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _toleranceDeg = toleranceDeg ?? throw new ArgumentNullException(nameof(toleranceDeg));
        _defaultExposureSeconds = defaultExposureSeconds ?? (() => 2.0);
        _logger = logger ?? NullLogger<RotationAssistService>.Instance;
    }

    public bool IsActive {
        get {
            lock (_gate) {
                return _state == "running";
            }
        }
    }

    private bool IsBusy {
        get {
            lock (_gate) {
                return _state is "running" or "confirming";
            }
        }
    }

    /// <summary>A finite angle, normalised into [0, 360). Pure — unit-tested.</summary>
    internal static double NormaliseTarget(double positionAngleDeg) {
        if (!double.IsFinite(positionAngleDeg)) {
            throw new ArgumentOutOfRangeException(nameof(positionAngleDeg), positionAngleDeg, "position angle must be a finite number of degrees.");
        }
        return AstroUtil.EuclidianModulus(positionAngleDeg, 360);
    }

    /// <summary>The request's exposure (finite, 0.01–60 s) or <paramref name="fallback"/> when absent. Pure — unit-tested.</summary>
    internal static double ResolveExposure(double? requested, double fallback) {
        if (requested is not { } s) {
            return double.IsFinite(fallback) && fallback > 0 ? Math.Clamp(fallback, MinExposureSeconds, MaxExposureSeconds) : 2.0;
        }
        if (!double.IsFinite(s) || s < MinExposureSeconds || s > MaxExposureSeconds) {
            throw new ArgumentOutOfRangeException(nameof(requested), s, $"exposure must be between {MinExposureSeconds} and {MaxExposureSeconds} seconds.");
        }
        return s;
    }

    /// <summary><c>loop</c> (default) or <c>single</c>; anything else is a bad request. Pure — unit-tested.</summary>
    internal static string ResolveMode(string? requested) => requested?.Trim().ToLowerInvariant() switch {
        null or "" => RotationAssistModes.Loop,
        RotationAssistModes.Loop => RotationAssistModes.Loop,
        RotationAssistModes.SingleShot => RotationAssistModes.SingleShot,
        var other => throw new ArgumentException($"mode must be '{RotationAssistModes.Loop}' or '{RotationAssistModes.SingleShot}', not '{other}'.", nameof(requested)),
    };

    /// <summary>The readout frames' binning (Single and Loop): the request's (1 … the camera's maximum, capped at <see cref="MaxLoopBinning"/>;
    /// an unknown camera maximum allows up to the cap) or, absent, the largest allowed. Pure — unit-tested.</summary>
    internal static int ResolveBinning(int? requested, int maxBinning) {
        var ceiling = maxBinning > 0 ? Math.Min(maxBinning, MaxLoopBinning) : MaxLoopBinning;
        if (requested is not { } b) {
            return Math.Max(1, ceiling);
        }
        if (b < 1 || b > ceiling) {
            throw new ArgumentOutOfRangeException(nameof(requested), b, $"binning must be between 1 and {ceiling} on this camera.");
        }
        return b;
    }

    private int MaxBinningQuietly() {
        try {
            return Math.Max(0, _solver.MaxBinning);
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            return 0;
        }
    }

    public async Task StartAsync(RotationAssistStartRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var target = NormaliseTarget(request.PositionAngleDeg);
        var exposure = ResolveExposure(request.ExposureSeconds, DefaultExposureQuietly());
        var mode = ResolveMode(request.Mode);
        var binning = ResolveBinning(request.Binning, MaxBinningQuietly());
        try {
            _solver.EnsureReady();
        } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException ex) {
            throw new RotationAssistNotReadyException(ex.Message, ex);
        }
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsBusy) {
                throw new InvalidOperationException(IsActive
                    ? "the rotation readout is already running"
                    : "the rotation readout is confirming the framing — wait for it, or stop it first");
            }
            lock (_gate) {
                // A new target is a new job: the history (and the advice built on it) starts over. The same
                // target — a single shot after a turn, a loop restarted after a cloud — keeps it.
                // Compared to 0.01°, folded across north: a float-rounded resend of the same dial angle is the same target.
                if (_target is not { } prev || Math.Abs(AstroUtil.EuclidianModulus(prev - target + 180, 360) - 180) > 0.005) {
                    _latest = null;
                    _recent.Clear();
                    _frame = null;
                    _frameSeq = 0;
                }
                _state = "running";
                _mode = mode;
                _exposureSeconds = exposure;
                _binning = binning;
                _confirmation = null;
                _target = target;
                _started = DateTimeOffset.UtcNow;
                _error = null;
                _consecutiveFailures = 0;
            }
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var loopCt = _loopCts.Token;
            var single = mode == RotationAssistModes.SingleShot;
            _loop = Task.Run(() => RunLoopAsync(target, exposure, binning, single, loopCt), CancellationToken.None);
            LogStarted(target, mode, exposure, binning);
        } finally {
            _opLock.Release();
        }
    }

    public async Task ConfirmAsync(CancellationToken ct) {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            double target;
            lock (_gate) {
                if (_state == "confirming") {
                    throw new InvalidOperationException("the rotation readout is already confirming the framing");
                }
                if (_target is not { } t) {
                    throw new InvalidOperationException("nothing to confirm — start the readout toward a framing angle first");
                }
                // Done approves a turn the readout has measured; with no solve toward this target yet
                // (a new angle cleared the history, or every frame failed) there is nothing to approve.
                if (_latest is null) {
                    throw new InvalidOperationException("nothing to confirm yet — wait for a frame to solve toward this angle");
                }
                target = t;
            }
            // Stop the loop (if any) and let its in-flight solve drain, so the full frame never collides with it.
            await CancelLoopAsync().ConfigureAwait(false);
            var exposure = DefaultExposureQuietly();
            lock (_gate) {
                _state = "confirming";
                _error = null;
                _confirmation = null;
            }
            _loopCts = new CancellationTokenSource();
            var loopCt = _loopCts.Token;
            _loop = Task.Run(() => RunConfirmAsync(target, exposure, loopCt), CancellationToken.None);
            LogConfirming(target, exposure);
        } finally {
            _opLock.Release();
        }
    }

    /// <summary>Cancel the background task and wait for it to drain. Caller holds <see cref="_opLock"/>.</summary>
    private async Task CancelLoopAsync() {
        var (cts, loop) = (_loopCts, _loop);
        _loopCts = null;
        _loop = null;
        if (cts is not null) {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        if (loop is not null) {
            try {
                // Let the in-flight capture/solve drain so the next camera user never collides with it.
                await loop.WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            } catch (TimeoutException) {
                LogStopTimedOut();
            }
        }
        cts?.Dispose();
    }

    public async Task StopAsync() {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try {
            await CancelLoopAsync().ConfigureAwait(false);
            bool wasRunning;
            lock (_gate) {
                wasRunning = _state is "running" or "confirming";
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

    public RotationAssistStatusDto GetStatus() {
        var tolerance = ToleranceQuietly();
        var defaultExposure = DefaultExposureQuietly();
        var maxBinning = MaxBinningQuietly();
        lock (_gate) {
            return new RotationAssistStatusDto(
                Active: _state == "running",
                State: _state,
                TargetPositionAngleDeg: _target ?? 0,
                ToleranceDeg: tolerance,
                Seq: _seq,
                StartedUtc: _started,
                Latest: _latest,
                Recent: _recent.ToArray(),
                WithinTolerance: _latest is { } l && Math.Abs(l.DeltaDeg) <= tolerance,
                Error: _error,
                ConsecutiveFailures: _consecutiveFailures,
                HasFrame: _frame is not null,
                FrameSeq: _frameSeq,
                Mode: _mode,
                ExposureSeconds: _exposureSeconds > 0 ? _exposureSeconds : defaultExposure,
                DefaultExposureSeconds: defaultExposure,
                Binning: _binning,
                AutoBinning: ResolveBinning(null, maxBinning),
                MaxBinning: maxBinning,
                Confirmation: _confirmation);
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
        Justification = "The tolerance comes from the profile; a read fault must not take the status endpoint down — fall back to a sane default.")]
    private double ToleranceQuietly() {
        try {
            var t = _toleranceDeg();
            return double.IsFinite(t) && t > 0 ? t : 1.0;
        } catch (Exception) {
            return 1.0;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The default exposure comes from the profile; a read fault must not take the status endpoint down — fall back to a sane default.")]
    private double DefaultExposureQuietly() {
        try {
            return ResolveExposure(null, _defaultExposureSeconds());
        } catch (Exception) {
            return 2.0;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Loop boundary: a capture or solver fault is one failed sample (counted; the loop ends in `error` after MaxConsecutiveFailures, or at once in single mode), never a faulted background task.")]
    private async Task RunLoopAsync(double target, double exposureSeconds, int binning, bool single, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                RotationSolve? solved = null;
                try {
                    solved = await _solver.SolvePositionAngleAsync(exposureSeconds, binning, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    LogSolveFaulted(ex);
                }
                if (solved is { } s && double.IsFinite(s.PositionAngleDeg)) {
                    var pa = s.PositionAngleDeg;
                    var delta = CenteringService.FoldRotationDelta(target, pa);
                    var jpeg = RenderQuietly(s.Frame);
                    lock (_gate) {
                        RecordSample(s, delta, jpeg);
                        if (single) {
                            _state = "stopped";
                        }
                    }
                    LogSample(pa, delta);
                    if (single) {
                        LogSingleDone();
                        return;
                    }
                    continue;
                }
                int failures;
                bool gaveUp;
                lock (_gate) {
                    failures = ++_consecutiveFailures;
                    gaveUp = single || failures >= MaxConsecutiveFailures;
                    if (gaveUp) {
                        _state = "error";
                        _error = single
                            ? "the frame would not solve — check the sky and the exposure, then take another"
                            : $"{failures} solves in a row failed — check the sky, the exposure and the plate-solver settings, then start again";
                    }
                }
                if (gaveUp) {
                    LogGaveUp(failures);
                    return;
                }
            }
        } catch (OperationCanceledException) {
            // stopped
        }
    }

    /// <summary>Append one solve as the latest sample (and the picture, when it rendered). Caller holds <see cref="_gate"/>.</summary>
    private RotationAssistSampleDto RecordSample(RotationSolve s, double delta, byte[]? jpeg) {
        _seq++;
        _consecutiveFailures = 0;
        _latest = new RotationAssistSampleDto(
            _seq, DateTimeOffset.UtcNow, Math.Round(AstroUtil.EuclidianModulus(s.PositionAngleDeg, 360), 2), Math.Round(delta, 2),
            Math.Round(s.RaDeg, 4), Math.Round(s.DecDeg, 4), Math.Round(s.PixelScaleArcsec, 3), s.Flipped,
            s.Frame.Width, s.Frame.Height);
        _recent.Enqueue(_latest);
        while (_recent.Count > RecentWindow) {
            _recent.Dequeue();
        }
        if (jpeg is not null) {
            _frame = jpeg;
            _frameSeq = _seq;
        }
        return _latest;
    }

    // The Done check: one 1×1 frame at the full plate-solve exposure. Its solve becomes the latest sample (so
    // the picture and the scope box on the sky show the real pointing) AND the confirmation; the verdict is
    // the tolerance applied to it. A failed solve ends in `error` — the user retries or goes back to the loop.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Task boundary: a capture or solver fault is the confirmation failing (reported in `error`), never a faulted background task.")]
    private async Task RunConfirmAsync(double target, double exposureSeconds, CancellationToken ct) {
        RotationSolve? solved = null;
        try {
            solved = await _solver.SolvePositionAngleAsync(exposureSeconds, 1, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            LogSolveFaulted(ex);
        }
        if (ct.IsCancellationRequested) {
            return;
        }
        if (solved is { } s && double.IsFinite(s.PositionAngleDeg)) {
            var delta = CenteringService.FoldRotationDelta(target, s.PositionAngleDeg);
            var jpeg = RenderQuietly(s.Frame);
            var tolerance = ToleranceQuietly();
            bool within;
            lock (_gate) {
                _confirmation = RecordSample(s, delta, jpeg);
                within = Math.Abs(_confirmation.DeltaDeg) <= tolerance;
                _state = within ? "confirmed" : "not_confirmed";
            }
            LogConfirmed(s.PositionAngleDeg, delta, within);
            return;
        }
        lock (_gate) {
            _consecutiveFailures++;
            _state = "error";
            _error = "the full-resolution frame would not solve — check the sky and the plate-solve exposure, then press Done again";
        }
        LogConfirmFailed();
    }

    // §64's renderer (auto-stretch + star rings, ≤1024 px) gives the readout its picture. Frames too small
    // to carry stars (unit-test stubs) are skipped; a render fault never touches the readout.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Cosmetic: a failed JPEG render must never fail the readout. Log-and-recover boundary.")]
    private byte[]? RenderQuietly(AnalysisFrame frame) {
        if (frame.Width < 64 || frame.Height < 64) {
            return null;
        }
        try {
            var (jpeg, _, _) = CameraService.RenderLiveFrame(frame.Pixels.ToArray(), frame.Width, frame.Height, bayerPattern: null, annotate: true);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout started toward position angle {Target:0.#}° ({Mode}, {Exposure:0.##} s, {Binning}×{Binning})")]
    private partial void LogStarted(double target, string mode, double exposure, int binning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout: confirming the framing toward {Target:0.#}° with one 1×1 frame at {Exposure:0.##} s")]
    private partial void LogConfirming(double target, double exposure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout: full-resolution solve {Solved:0.##}°, {Delta:+0.##;-0.##}° to the target — {Within}")]
    private partial void LogConfirmed(double solved, double delta, bool within);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: the confirmation frame would not solve")]
    private partial void LogConfirmFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout: single frame solved — done")]
    private partial void LogSingleDone();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: the in-flight solve did not drain in time — stopping anyway")]
    private partial void LogStopTimedOut();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout: solved position angle {Solved:0.##}°, {Delta:+0.##;-0.##}° to the target")]
    private partial void LogSample(double solved, double delta);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: a solve faulted (counted as a failed sample)")]
    private partial void LogSolveFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout gave up after {Failures} consecutive failed solves")]
    private partial void LogGaveUp(int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: could not render the solved frame — the readout continues without a picture")]
    private partial void LogRenderFailed(Exception ex);
}
