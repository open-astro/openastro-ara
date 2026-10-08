#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>What the caller of <see cref="PaResidualTracker"/> should do after an input.</summary>
internal enum PaResidualAction {
    None,
    /// <summary>A measurement began (guiding started).</summary>
    Started,
    /// <summary>Another <see cref="PaResidualTracker.ProgressEverySeconds"/> of clean guiding is in.</summary>
    Progress,
    /// <summary>Enough clean guiding: fit it (<see cref="PaResidualTracker.Estimator"/>).</summary>
    Complete,
    /// <summary>Guiding ended before enough clean guiding was in; nothing to report.</summary>
    Cancelled,
}

/// <summary>
/// #1311 — when to sample the guide stream for the polar alignment residual. Each guided run
/// (guiding started → stopped) gets one measurement over its first <see cref="TargetSeconds"/> of
/// clean guiding; dithers, settles and pauses are left out and a lost star closes a segment.
/// Guiding that stops after <see cref="MinSeconds"/> of clean guiding still reports what it has.
/// Pure and single-threaded: GuiderService serialises the calls.
/// </summary>
internal sealed class PaResidualTracker {

    public PaResidualTracker(double targetSeconds = 300, double minSeconds = 120, double progressEverySeconds = 30) {
        TargetSeconds = targetSeconds;
        MinSeconds = minSeconds;
        ProgressEverySeconds = progressEverySeconds;
    }

    public double TargetSeconds { get; }
    public double MinSeconds { get; }
    public double ProgressEverySeconds { get; }

    public PaResidualEstimator Estimator { get; } = new();

    /// <summary>True from guiding started until this run's measurement completed or was dropped.</summary>
    public bool Measuring { get; private set; }

    // This guided run already produced (or dropped) its measurement: ignore frames until the next start.
    private bool _runDone;
    // Inside a dither / settle or a guider pause: frames are not samples.
    private bool _settling;
    private bool _paused;
    private double _nextProgressAt;

    public PaResidualAction OnStep(double timeSec, double decRawPx, double decDurationMs) {
        if (_runDone) {
            return PaResidualAction.None;
        }
        // The daemon can connect to a guider that is already guiding: start on its first frame.
        var action = Measuring ? PaResidualAction.None : Begin();
        if (_settling || _paused) {
            return action;
        }
        Estimator.Add(timeSec, decRawPx, decDurationMs);
        var sampled = Estimator.SampleSeconds;
        if (sampled >= TargetSeconds) {
            return Finish();
        }
        if (sampled >= _nextProgressAt) {
            _nextProgressAt = (System.Math.Floor(sampled / ProgressEverySeconds) + 1) * ProgressEverySeconds;
            return action == PaResidualAction.Started ? action : PaResidualAction.Progress;
        }
        return action;
    }

    /// <summary>One <c>guider.event</c> kind (see <see cref="Contracts.WsEvents.WsEventCatalog.GuiderEvent"/>).</summary>
    public PaResidualAction OnMarker(string? kind) {
        switch (kind) {
            case "guiding_started":
                return Begin();
            case "dithered":
            case "settling":
                _settling = true;
                Estimator.Break();
                return PaResidualAction.None;
            case "settle_done":
                _settling = false;
                Estimator.Break();
                return PaResidualAction.None;
            case "paused":
                _paused = true;
                Estimator.Break();
                return PaResidualAction.None;
            case "resumed":
                _paused = false;
                Estimator.Break();
                return PaResidualAction.None;
            case "star_lost":
            case "lock_position_lost":
                Estimator.Break();
                return PaResidualAction.None;
            case "calibration_started":
            case "guiding_stopped":
                return End();
            default:
                return PaResidualAction.None;
        }
    }

    /// <summary>The guider went away (disconnect, link loss): drop any measurement in flight.</summary>
    public PaResidualAction Reset() {
        var wasMeasuring = Measuring;
        Measuring = false;
        _runDone = false;
        _settling = false;
        _paused = false;
        Estimator.Clear();
        return wasMeasuring ? PaResidualAction.Cancelled : PaResidualAction.None;
    }

    private PaResidualAction Begin() {
        Estimator.Clear();
        Measuring = true;
        _runDone = false;
        _settling = false;
        _paused = false;
        _nextProgressAt = ProgressEverySeconds;
        return PaResidualAction.Started;
    }

    private PaResidualAction Finish() {
        Measuring = false;
        _runDone = true;
        return PaResidualAction.Complete;
    }

    // Guiding stopped (or recalibration began): report what there is if it is enough. The next
    // guiding start begins a fresh measurement; until then frames are ignored.
    private PaResidualAction End() {
        if (!Measuring) {
            _runDone = true;
            return PaResidualAction.None;
        }
        if (Estimator.SampleSeconds >= MinSeconds) {
            return Finish();
        }
        Measuring = false;
        _runDone = true;
        Estimator.Clear();
        return PaResidualAction.Cancelled;
    }
}
