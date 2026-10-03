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
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>One capture-and-solve of the main camera, reporting the sky position angle (null = the solve
/// failed). <see cref="CenteringService"/> implements it over the profile's plate-solver stack; tests inject
/// a scripted one.</summary>
public interface IPositionAngleSolver {
    Task<double?> SolvePositionAngleAsync(CancellationToken ct);
}

/// <summary>The readout's control surface: the sequencer seam plus what the REST endpoints need.</summary>
public interface IRotationAssistService : IRotationAssistExecutor {
    bool IsActive { get; }
    Task StartAsync(RotationAssistStartRequestDto request, CancellationToken ct);
    RotationAssistStatusDto GetStatus();
}

/// <summary>
/// The by-hand rotation readout (the daemon as a protractor). A rig without a rotator cannot act on a
/// framing angle, so a run's <c>Rotate camera by hand</c> step starts this loop and parks the run awaiting the
/// user: capture → plate-solve → the solved position angle and the signed, folded delta to the target, again
/// and again, until the user presses Resume. The client shows the delta and advises relative to the user's
/// last move (the daemon cannot know which way "clockwise" turns the sky on this optical train). One loop at
/// a time; repeated solve failures end it in <c>error</c> rather than spinning against a cloud.
/// </summary>
public sealed partial class RotationAssistService : IRotationAssistService, IDisposable {

    internal const int MaxConsecutiveFailures = 5;
    internal const int RecentWindow = 120;

    private readonly IPositionAngleSolver _solver;
    private readonly Func<double> _toleranceDeg;
    private readonly ILogger<RotationAssistService> _logger;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _gate = new();

    private string _state = "idle";
    private double _target;
    private long _seq;
    private DateTimeOffset? _started;
    private RotationAssistSampleDto? _latest;
    private readonly Queue<RotationAssistSampleDto> _recent = new();
    private string? _error;
    private int _consecutiveFailures;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public RotationAssistService(IPositionAngleSolver solver, Func<double> toleranceDeg, ILogger<RotationAssistService>? logger = null) {
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _toleranceDeg = toleranceDeg ?? throw new ArgumentNullException(nameof(toleranceDeg));
        _logger = logger ?? NullLogger<RotationAssistService>.Instance;
    }

    public bool IsActive {
        get {
            lock (_gate) {
                return _state == "running";
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

    public Task StartAsync(RotationAssistStartRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync(request.PositionAngleDeg, ct);
    }

    public async Task StartAsync(double targetPositionAngleDeg, CancellationToken token) {
        var target = NormaliseTarget(targetPositionAngleDeg);
        await _opLock.WaitAsync(token).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive) {
                throw new InvalidOperationException("the rotation readout is already running");
            }
            lock (_gate) {
                _state = "running";
                _target = target;
                _started = DateTimeOffset.UtcNow;
                _latest = null;
                _recent.Clear();
                _error = null;
                _consecutiveFailures = 0;
            }
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var ct = _loopCts.Token;
            _loop = Task.Run(() => RunLoopAsync(target, ct), CancellationToken.None);
            LogStarted(target);
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
                    // Let the in-flight capture/solve drain so the next camera user never collides with it.
                    await loop.WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
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

    public RotationAssistStatusDto GetStatus() {
        var tolerance = ToleranceQuietly();
        lock (_gate) {
            return new RotationAssistStatusDto(
                Active: _state == "running",
                State: _state,
                TargetPositionAngleDeg: _target,
                ToleranceDeg: tolerance,
                Seq: _seq,
                StartedUtc: _started,
                Latest: _latest,
                Recent: _recent.ToArray(),
                WithinTolerance: _latest is { } l && Math.Abs(l.DeltaDeg) <= tolerance,
                Error: _error,
                ConsecutiveFailures: _consecutiveFailures);
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
        Justification = "Loop boundary: a capture or solver fault is one failed sample (counted; the loop ends in `error` after MaxConsecutiveFailures), never a faulted background task.")]
    private async Task RunLoopAsync(double target, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                double? solved = null;
                try {
                    solved = await _solver.SolvePositionAngleAsync(ct).ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    LogSolveFaulted(ex);
                }
                if (solved is { } pa && double.IsFinite(pa)) {
                    var delta = CenteringService.FoldRotationDelta(target, pa);
                    lock (_gate) {
                        _seq++;
                        _consecutiveFailures = 0;
                        _latest = new RotationAssistSampleDto(_seq, DateTimeOffset.UtcNow, Math.Round(AstroUtil.EuclidianModulus(pa, 360), 2), Math.Round(delta, 2));
                        _recent.Enqueue(_latest);
                        while (_recent.Count > RecentWindow) {
                            _recent.Dequeue();
                        }
                    }
                    LogSample(pa, delta);
                    continue;
                }
                int failures;
                lock (_gate) {
                    failures = ++_consecutiveFailures;
                    if (failures >= MaxConsecutiveFailures) {
                        _state = "error";
                        _error = $"{failures} solves in a row failed — check the sky, the exposure and the plate-solver settings, then start again";
                    }
                }
                if (failures >= MaxConsecutiveFailures) {
                    LogGaveUp(failures);
                    return;
                }
            }
        } catch (OperationCanceledException) {
            // stopped
        }
    }

    public void Dispose() {
        _disposed = true;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _opLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout started toward position angle {Target:0.#}°")]
    private partial void LogStarted(double target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: the in-flight solve did not drain in time — stopping anyway")]
    private partial void LogStopTimedOut();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotation readout: solved position angle {Solved:0.##}°, {Delta:+0.##;-0.##}° to the target")]
    private partial void LogSample(double solved, double delta);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout: a solve faulted (counted as a failed sample)")]
    private partial void LogSolveFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation readout gave up after {Failures} consecutive failed solves")]
    private partial void LogGaveUp(int failures);
}
