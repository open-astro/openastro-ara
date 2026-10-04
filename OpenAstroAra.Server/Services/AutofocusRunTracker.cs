#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The daemon-side record of the current / most recent autofocus run (§59.12 "UI during an AF run",
/// served at <c>GET /api/v1/autofocus/state</c>). <see cref="AutofocusSweepService"/> writes it as the
/// run progresses — every probe, the fit, the final measured focus and a rendered frame — so a client
/// that opens the Smart Focus pane mid-run (or after a reconnect that skipped the WS events) rehydrates
/// the whole picture from one GET instead of reconstructing it from the event stream.
///
/// Also the cancel seam: the sweep registers its run's <see cref="CancellationTokenSource"/> here so
/// <c>POST /api/v1/autofocus/cancel</c> can stop a run whoever started it (the focuser endpoint's job
/// or a sequence instruction) — the §65.5 job cancel only reaches the former.
///
/// In-memory and per-boot by design: a probe set is an instrument reading, not data the user revisits
/// (§59 — probe frames are never catalogued); the §50 Stats view owns the durable focus history.
/// </summary>
public sealed class AutofocusRunTracker {

    private readonly object _gate = new();

    private string _state = "idle";
    private string? _mode;
    private string? _phase;
    private string? _trigger;
    private string? _pendingTrigger;
    private DateTimeOffset? _started;
    private DateTimeOffset? _completed;
    private int? _startPosition;
    private int? _finalPosition;
    private double? _finalHfr;
    private int? _finalStars;
    private string? _filter;
    private double? _temperature;
    private int _totalSteps;
    private int _completedSteps;
    private int _sweepAttempt;
    private int? _stepSize;
    private string? _stepSizeSource;
    private readonly List<AutofocusProbeDto> _probes = new();
    private AutofocusCurveFitDto? _fit;
    private string? _reason;
    private int? _restoredPosition;
    private byte[]? _frame;
    private long _frameSeq;
    private int? _framePosition;
    private double? _frameHfr;
    private CancellationTokenSource? _runCts;

    /// <summary>Who is about to start a run. The focuser endpoint stamps <c>manual</c> right before it
    /// enqueues its job; a run that begins without a stamp is a sequence-driven one. Consumed by
    /// <see cref="Begin"/>.</summary>
    public void StampNextTrigger(string trigger) {
        lock (_gate) {
            _pendingTrigger = trigger;
        }
    }

    /// <summary>A run is starting: reset the snapshot and register its cancellation source.</summary>
    public void Begin(string mode, int startPosition, int totalSteps, string? filter, double? temperature, CancellationTokenSource runCts) {
        lock (_gate) {
            _state = "running";
            _mode = mode;
            _phase = mode == "smart" ? "smart" : "coarse";
            _trigger = _pendingTrigger ?? "sequence";
            _pendingTrigger = null;
            _started = DateTimeOffset.UtcNow;
            _completed = null;
            _startPosition = startPosition;
            _finalPosition = null;
            _finalHfr = null;
            _finalStars = null;
            _filter = filter;
            _temperature = temperature is { } t && double.IsFinite(t) ? t : null;
            _totalSteps = totalSteps;
            _completedSteps = 0;
            _sweepAttempt = 0;
            _stepSize = null;
            _stepSizeSource = null;
            _probes.Clear();
            _fit = null;
            _reason = null;
            _restoredPosition = null;
            _frame = null;
            _framePosition = null;
            _frameHfr = null;
            _runCts = runCts;
        }
    }

    /// <summary>The Smart→Classic hand-off: the mode flips, the phase restarts at the coarse search, and the
    /// Smart shots leave the record (they are not points on the Classic V-curve).</summary>
    public void FallBackToClassic(int totalSteps) {
        lock (_gate) {
            _mode = "classic";
            _phase = "coarse";
            _probes.RemoveAll(p => p.Phase == "smart");
            _totalSteps = totalSteps;
            _completedSteps = 0;
        }
    }

    public void SetPhase(string phase) {
        lock (_gate) {
            _phase = phase;
        }
    }

    /// <summary>§59.8 — the step size the Classic sweep resolved for this run and its source.</summary>
    public void SetStepSize(int stepSize, string source) {
        lock (_gate) {
            _stepSize = stepSize;
            _stepSizeSource = source;
        }
    }

    /// <summary>A new fine-sweep attempt (the first, or a re-centre on an edge minimum). The previous
    /// attempt's fine probes are dropped so the V-curve shows the sweep in progress; coarse probes stay.</summary>
    public int BeginSweepAttempt() {
        lock (_gate) {
            _sweepAttempt++;
            _probes.RemoveAll(p => p.Phase == "fine");
            _fit = null;
            _completedSteps = 0;
            _phase = "sweep";
            return _sweepAttempt;
        }
    }

    public AutofocusProbeDto AddProbe(string phase, int position, double hfr, int stars, bool kept) {
        lock (_gate) {
            var probe = new AutofocusProbeDto(_probes.Count + 1, phase, position, Finite(hfr), stars, kept);
            _probes.Add(probe);
            // Progress counts the fine sweep's probes, or a Smart run's shots (out of SmartMaxShots).
            if (phase is "fine" or "smart") {
                _completedSteps++;
            }
            return probe;
        }
    }

    public void SetFit(FocusCurveFitResult fit, double minPosition, double maxPosition) {
        var curve = new List<AutofocusCurvePointDto>();
        foreach (var (x, y) in fit.Sample(minPosition, maxPosition)) {
            curve.Add(new AutofocusCurvePointDto(Math.Round(x, 1), Math.Round(y, 4)));
        }
        lock (_gate) {
            _phase = "fitting";
            _fit = new AutofocusCurveFitDto(
                fit.Method.ToString().ToLowerInvariant(),
                Finite(fit.RSquared),
                Finite(fit.BestPosition),
                Finite(fit.PredictedHfr),
                fit.WithinSampledRange,
                curve);
        }
    }

    /// <summary>The calibration's curve for a Smart run that was confirmed by a bracket, not fitted: HFR
    /// doubles <paramref name="halfWidthSteps"/> from <paramref name="centre"/> (the hyperbola
    /// h₀·√(1 + 3(d/w)²)). Algorithm "calibration", R² 1 — the pane draws it as the V the three shots
    /// were judged against and hides the R² tile for it.</summary>
    public void SetModelCurve(int centre, double inFocusHfr, double halfWidthSteps, double minPosition, double maxPosition) {
        var curve = new List<AutofocusCurvePointDto>();
        const int samples = 40;
        for (int i = 0; i <= samples; i++) {
            var x = minPosition + (maxPosition - minPosition) * i / samples;
            var d = (x - centre) / halfWidthSteps;
            curve.Add(new AutofocusCurvePointDto(Math.Round(x, 1), Math.Round(inFocusHfr * Math.Sqrt(1 + 3 * d * d), 4)));
        }
        lock (_gate) {
            _fit = new AutofocusCurveFitDto("calibration", 1.0, centre, Finite(inFocusHfr), true, curve);
        }
    }

    /// <summary>Attach a rendered frame (JPEG) of the probe at <paramref name="position"/>.</summary>
    public void SetFrame(byte[] jpeg, int position, double hfr) {
        lock (_gate) {
            _frame = jpeg;
            _frameSeq++;
            _framePosition = position;
            _frameHfr = Finite(hfr);
        }
    }

    public void Complete(int finalPosition, double? finalHfr, int? finalStars) {
        lock (_gate) {
            _state = "complete";
            _phase = "done";
            _completed = DateTimeOffset.UtcNow;
            _finalPosition = finalPosition;
            _finalHfr = finalHfr is { } h ? Finite(h) : null;
            _finalStars = finalStars;
            _runCts = null;
        }
    }

    public void Fail(string reason, int? restoredPosition) {
        lock (_gate) {
            _state = "failed";
            _phase = "done";
            _completed = DateTimeOffset.UtcNow;
            _reason = reason;
            _restoredPosition = restoredPosition;
            _runCts = null;
        }
    }

    public void Cancel(int? restoredPosition) {
        lock (_gate) {
            _state = "cancelled";
            _phase = "done";
            _completed = DateTimeOffset.UtcNow;
            _reason = "cancelled";
            _restoredPosition = restoredPosition;
            _runCts = null;
        }
    }

    /// <summary>Cancel the run in progress. False when nothing is running.</summary>
    public bool TryCancel() {
        CancellationTokenSource? cts;
        lock (_gate) {
            cts = _state == "running" ? _runCts : null;
        }
        if (cts is null) {
            return false;
        }
        try {
            cts.Cancel();
        } catch (ObjectDisposedException) {
            return false;
        }
        return true;
    }

    public bool IsRunning {
        get {
            lock (_gate) {
                return _state == "running";
            }
        }
    }

    public string State {
        get {
            lock (_gate) {
                return _state;
            }
        }
    }

    /// <summary>The rendered frame (JPEG) and its sequence, or null before the run has one. A method, not a
    /// property: it hands out the buffer the HTTP response streams, and the analyzer's property heuristic
    /// (CA1024) would read as a cheap value when it is a copy-on-read of a multi-KB image.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1024:Use properties where appropriate",
        Justification = "Returns a snapshot of a mutable multi-KB buffer taken under the lock; the live-view counterpart on CameraService is a method for the same reason.")]
    public (ReadOnlyMemory<byte> Jpeg, long Seq)? GetFrame() {
        lock (_gate) {
            return _frame is null ? null : (_frame, _frameSeq);
        }
    }

    public AutofocusRunDto Snapshot() {
        lock (_gate) {
            var duration = _started is { } s
                ? ((_completed ?? DateTimeOffset.UtcNow) - s).TotalSeconds
                : (double?)null;
            return new AutofocusRunDto(
                State: _state,
                Mode: _mode,
                Phase: _phase,
                Trigger: _trigger,
                StartedUtc: _started,
                CompletedUtc: _completed,
                DurationSeconds: duration is { } d ? Math.Round(d, 1) : null,
                StartPosition: _startPosition,
                FinalPosition: _finalPosition,
                FinalHfr: _finalHfr,
                FinalStars: _finalStars,
                Filter: _filter,
                FocuserTemperatureC: _temperature,
                TotalSteps: _totalSteps,
                CompletedSteps: _completedSteps,
                SweepAttempt: _sweepAttempt,
                StepSize: _stepSize,
                StepSizeSource: _stepSizeSource,
                Probes: _probes.ToArray(),
                Fit: _fit,
                Reason: _reason,
                RestoredPosition: _restoredPosition,
                HasFrame: _frame is not null,
                FrameSeq: _frameSeq,
                FramePosition: _framePosition,
                FrameHfr: _frameHfr);
        }
    }

    // NaN/∞ are unrepresentable in JSON; a probe the metric could not measure reads as 0 on the wire.
    private static double Finite(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0.0;
}
