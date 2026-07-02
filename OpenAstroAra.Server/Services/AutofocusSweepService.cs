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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §59.8 — the LIVE autofocus V-curve sweep (the orchestrator the curve-fit math has been waiting
/// for): step the focuser through <c>2·Steps+1</c> positions spanning ±<c>Steps·StepSize</c>
/// around the starting position, HFR-probe each through the §59 analysis-capture seam (same
/// device path + in-flight gate as real captures, nothing persisted), fit the curve with
/// <see cref="FocusCurveFit.FitBest"/>, and move to the predicted best.
///
/// Semantics (fail-loud, §37.11-policy-aware):
///  * The sweep probes in ONE direction (outermost position first, then stepping down through
///    every point) so mechanical backlash biases every sample identically.
///  * Any failed probe, a probe with fewer than <see cref="MinStarsPerProbe"/> stars, an
///    unusable fit, or a best-position extrapolated beyond the sampled range FAILS the sweep —
///    a fabricated focus position quietly ruins every subsequent frame.
///  * On failure, the starting position is restored when the profile's
///    <c>RestorePositionOnFailure</c> is set (best-effort; restore errors are logged, the sweep
///    still reports failure).
///  * Sweeps are serialized — a second caller waits; the focuser is a single physical axis.
///
/// Implements the sequencer's <see cref="IAutofocusExecutor"/> seam, which unlocks
/// <c>RunAutofocus</c>, <c>AutofocusAfterExposures</c>, and the meridian flip's
/// <c>AutoFocusAfterFlip</c> in one stroke. Live end-to-end validation is deferred (no focuser
/// on the dev rig — recorded in PORT_TODO); the orchestration is fully unit-tested with mocked
/// equipment and an injected focus metric.
/// </summary>
public sealed partial class AutofocusSweepService : IAutofocusExecutor, IDisposable {

    /// <summary>A probe with fewer detected stars than this is untrustworthy (clouds, slew smear,
    /// hot-pixel-only detections) and fails the sweep rather than feeding a junk HFR to the fit.</summary>
    internal const int MinStarsPerProbe = 3;

    /// <summary>
    /// The sweep's probe count for a given configuration — the ONE place the
    /// `2·Steps+1` scheme lives, shared with the autofocus job endpoint so the
    /// job's progress denominator can never silently drift from the sweep's
    /// real step count.
    /// </summary>
    public static int ProbeCount(AutofocusSettingsDto settings) =>
        settings.Steps >= 1 ? settings.Steps * 2 + 1 : 1;

    private readonly IProfileStore _profiles;
    private readonly IFocuserMediator _focuser;
    private readonly IAnalysisFrameSource _frames;
    private readonly Func<AnalysisFrame, CancellationToken, (double Hfr, int Stars)> _metric;
    private readonly ILogger<AutofocusSweepService> _logger;
    // One sweep at a time — the focuser is a single physical axis, and interleaved sweeps would
    // corrupt each other's curves. Waiters queue (same philosophy as the capture gate).
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    public AutofocusSweepService(
            IProfileStore profiles,
            IFocuserMediator focuser,
            IAnalysisFrameSource frames,
            ILogger<AutofocusSweepService>? logger = null,
            Func<AnalysisFrame, CancellationToken, (double Hfr, int Stars)>? metric = null) {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _focuser = focuser ?? throw new ArgumentNullException(nameof(focuser));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _logger = logger ?? NullLogger<AutofocusSweepService>.Instance;
        _metric = metric ?? DefaultMetric;
    }

    // Production metric: the §59 StarDetector at the autofocus-canonical parameters. HFR probes
    // are relative measurements, so the fixed sensitivity matters less than it being IDENTICAL
    // for every probe of the sweep.
    private static (double Hfr, int Stars) DefaultMetric(AnalysisFrame frame, CancellationToken ct) {
        var result = StarDetector.Detect(
            frame.Pixels.Span, frame.Width, frame.Height,
            new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true },
            ct);
        return (result.AverageHFR, result.DetectedStars);
    }

    /// <inheritdoc/>
    public async Task<bool> RunAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
        await _sweepGate.WaitAsync(token).ConfigureAwait(false);
        try {
            return await RunSweepCoreAsync(progress, token).ConfigureAwait(false);
        } finally {
            _sweepGate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sweep boundary: probe captures / focuser moves / the metric can throw device, HTTP, or math exceptions; any escape must degrade to a logged failed sweep (with the focuser restored) so the calling sequence step fails cleanly instead of faulting the worker. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> RunSweepCoreAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var settings = _profiles.GetAutofocusSettings();
        if (settings.Steps < 1 || settings.StepSize < 1) {
            LogSweepRejected($"invalid sweep configuration (Steps={settings.Steps}, StepSize={settings.StepSize})");
            return false;
        }
        var info = _focuser.GetInfo();
        if (info is not { Connected: true }) {
            LogSweepRejected("focuser is not connected");
            return false;
        }
        var startPosition = info.Position;
        var restoreOnFailure = settings.RestorePositionOnFailure;

        try {
            var points = new List<FocusPoint>(ProbeCount(settings));
            // Outermost-first, stepping DOWN through every position: one approach direction means
            // backlash biases every sample identically instead of splitting the curve in two.
            var top = startPosition + settings.Steps * settings.StepSize;
            var totalProbes = ProbeCount(settings);
            // The FIRST probe needs the same treatment: `top` is reached by an UPWARD move from
            // the start position, so without this overshoot its sample would carry up-approach
            // backlash while every other sample is approached downward — and the topmost point is
            // one of the two boundary points that most influence the fit. Overshoot above, then
            // enter the sweep moving down.
            await _focuser.MoveFocuser(top + settings.StepSize, token).ConfigureAwait(false);
            for (var i = 0; i <= settings.Steps * 2; i++) {
                token.ThrowIfCancellationRequested();
                var position = top - i * settings.StepSize;
                // Structured per-probe progress (Progress/MaxProgress), so consumers
                // (the §65.5 job endpoint) can tick real numbers instead of parsing
                // the status string.
                progress.Report(new ApplicationStatus {
                    Status = $"Autofocus: probing position {position} ({i + 1}/{totalProbes})",
                    Progress = i + 1,
                    MaxProgress = totalProbes,
                    ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue,
                });
                var reached = await _focuser.MoveFocuser(position, token).ConfigureAwait(false);
                var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureSeconds, settings.Binning, token).ConfigureAwait(false);
                var (hfr, stars) = _metric(frame, token);
                if (stars < MinStarsPerProbe || hfr <= 0 || double.IsNaN(hfr)) {
                    LogSweepFailed($"probe at {reached} was untrustworthy ({stars} stars, HFR {hfr:0.###}) — clouds or a slew smear can cause this");
                    await RestoreAsync(restoreOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                LogProbe(reached, hfr, stars);
                points.Add(new FocusPoint(reached, hfr, stars));
            }

            var fit = FocusCurveFit.FitBest(points);
            if (fit is not { IsUsable: true, WithinSampledRange: true }) {
                LogSweepFailed(fit is null
                    ? "curve fit produced no result"
                    : $"curve fit unusable (usable={fit.IsUsable}, inRange={fit.WithinSampledRange}, R²={fit.RSquared:0.###}) — widen the sweep or re-centre focus manually");
                await RestoreAsync(restoreOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            var best = (int)Math.Round(fit.BestPosition);
            Report(progress, $"Autofocus: moving to best focus {best} (R²={fit.RSquared:0.##})");
            // Approach best from the SAME direction as the probes (from above) so backlash at the
            // final move matches the backlash baked into every sample. Unconditional: the sweep
            // ends at the bottom, so even a best at (or rounding to) the top edge needs the
            // overshoot — a direct move there would be upward, i.e. backlash-inconsistent.
            await _focuser.MoveFocuser(best + settings.StepSize, token).ConfigureAwait(false);
            var final = await _focuser.MoveFocuser(best, token).ConfigureAwait(false);
            LogSweepComplete(final, fit.PredictedHfr, fit.RSquared, fit.Method);
            return true;
        } catch (OperationCanceledException) {
            // A cancelled sweep (abort/stop/shutdown) restores best-effort and propagates — the
            // caller decides what a cancelled step means; we just don't leave focus stranded at
            // a random probe position.
            await RestoreAsync(restoreOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            throw;
        } catch (Exception ex) {
            LogSweepError(ex);
            await RestoreAsync(restoreOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort restore boundary: a restore failure (device fault mid-recovery) must not mask the sweep's own failure/cancellation being reported. CA1031's log-and-recover boundary applies.")]
    private async Task RestoreAsync(bool restore, int startPosition, CancellationToken ct = default) {
        if (!restore) return;
        try {
            await _focuser.MoveFocuser(startPosition, ct).ConfigureAwait(false);
            LogRestored(startPosition);
        } catch (Exception ex) {
            LogRestoreFailed(ex, startPosition);
        }
    }

    private static void Report(IProgress<ApplicationStatus> progress, string status) =>
        progress?.Report(new ApplicationStatus { Status = status });

    public void Dispose() => _sweepGate.Dispose();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus sweep rejected: {Reason}")]
    private partial void LogSweepRejected(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus sweep failed: {Reason}")]
    private partial void LogSweepFailed(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Autofocus sweep errored")]
    private partial void LogSweepError(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus probe: position {Position} HFR {Hfr} ({Stars} stars)")]
    private partial void LogProbe(int position, double hfr, int stars);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus complete: position {Position}, predicted HFR {PredictedHfr}, R²={RSquared} ({Method})")]
    private partial void LogSweepComplete(int position, double predictedHfr, double rSquared, AFCurveFitting method);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus: restored focuser to starting position {Position}")]
    private partial void LogRestored(int position);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Autofocus: failed to restore focuser to {Position}")]
    private partial void LogRestoreFailed(Exception ex, int position);
}
