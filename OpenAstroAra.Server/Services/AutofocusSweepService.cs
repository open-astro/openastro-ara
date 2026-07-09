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
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly Func<AnalysisFrame, CancellationToken, StarDetectionResult> _metric;
    private readonly ILogger<AutofocusSweepService> _logger;
    private readonly ImageHistoryService? _history;
    private readonly IFilterWheelMediator? _filterWheel;
    // §59.10 — optional surfacing of the collimation verdict (both nullable, guarded per-use like the
    // Safety precedent): a WS event for every confident read, a user notification only for the actionable
    // Slight/Significant bands. Absent in tests that don't exercise surfacing.
    private readonly IWsBroadcaster? _ws;
    private readonly INotificationService? _notifications;
    // One sweep at a time — the focuser is a single physical axis, and interleaved sweeps would
    // corrupt each other's curves. Waiters queue (same philosophy as the capture gate).
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    public AutofocusSweepService(
            IProfileStore profiles,
            IFocuserMediator focuser,
            IAnalysisFrameSource frames,
            ILogger<AutofocusSweepService>? logger = null,
            Func<AnalysisFrame, CancellationToken, StarDetectionResult>? metric = null,
            ImageHistoryService? history = null,
            IFilterWheelMediator? filterWheel = null,
            IWsBroadcaster? ws = null,
            INotificationService? notifications = null) {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _focuser = focuser ?? throw new ArgumentNullException(nameof(focuser));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _logger = logger ?? NullLogger<AutofocusSweepService>.Instance;
        _metric = metric ?? DefaultMetric;
        _history = history;
        _filterWheel = filterWheel;
        _ws = ws;
        _notifications = notifications;
    }

    // Production metric: the §59 StarDetector at the autofocus-canonical parameters. HFR probes
    // are relative measurements, so the fixed sensitivity matters less than it being IDENTICAL
    // for every probe of the sweep. Returns the whole result (not just HFR + count) so the sweep can
    // also read each probe's per-star donut metrics for the §59.10 end-of-sweep collimation check.
    private static StarDetectionResult DefaultMetric(AnalysisFrame frame, CancellationToken ct) =>
        StarDetector.Detect(
            frame.Pixels.Span, frame.Width, frame.Height,
            new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true },
            ct);

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
            // §59.10 — retain each probe's (position, detected stars) for the end-of-sweep collimation read.
            // At completion we feed the evaluator the single MOST-DEFOCUSED probe's stars (widest, best-resolved
            // donuts, each near-centre star once) rather than the union across steps, which would double-count
            // the same physical star and inflate the independent-star confidence.
            var probeStars = new List<(int Position, IReadOnlyList<DetectedStar> Stars)>(ProbeCount(settings));
            // §59.2 — every probe also yields a labelled (position, feature-vector) calibration sample for
            // free: the sweep IS the Smart Focus calibration pass (§59.15 "calibration piggybacks on Classic").
            var probeFeatures = new List<(int Position, FocusFeatureVector Features)>(ProbeCount(settings));
            int frameWidth = 0, frameHeight = 0;
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
                var result = _metric(frame, token);
                var hfr = result.AverageHFR;
                var stars = result.DetectedStars;
                if (stars < MinStarsPerProbe || hfr <= 0 || double.IsNaN(hfr)) {
                    LogSweepFailed($"probe at {reached} was untrustworthy ({stars} stars, HFR {hfr:0.###}) — clouds or a slew smear can cause this");
                    await RestoreAsync(restoreOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                LogProbe(reached, hfr, stars);
                points.Add(new FocusPoint(reached, hfr, stars));
                probeStars.Add((reached, result.StarList));
                probeFeatures.Add((reached, FocusFeatureExtractor.Extract(result)));
                frameWidth = frame.Width;
                frameHeight = frame.Height;
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
            await EvaluateCollimationQuietlyAsync(probeStars, fit.BestPosition, frameWidth, frameHeight).ConfigureAwait(false);
            RecordAutofocusQuietly();
            RecordCalibrationQuietly(probeFeatures);
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

    // §59.5 — the trigger family's reference point: temperature drift and HFR trend are both
    // measured "since the last autofocus", and the filter records which wheel slot this focus
    // position belongs to (first-use-of-a-filter trigger). Bookkeeping AFTER a completed sweep:
    // a GetInfo hiccup here must not turn a successful autofocus into a reported failure (which
    // would also restore the focuser to the stale pre-sweep position, undoing real work).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-success bookkeeping boundary: device-info reads for the history record may surface any device/comms exception; the sweep already succeeded and must be reported as such. CA1031's log-and-recover boundary applies.")]
    private void RecordAutofocusQuietly() {
        if (_history is null) return;
        try {
            _history.RecordAutofocus(
                _focuser.GetInfo()?.Temperature ?? double.NaN,
                _filterWheel?.GetInfo()?.SelectedFilter?.Name);
        } catch (Exception ex) {
            LogHistoryRecordFailed(ex);
        }
    }

    // §59.2 — refresh the Smart Focus calibration from THIS sweep's labelled samples. Every successful
    // Classic sweep recalibrates (the newest sweep reflects the rig's current optical + thermal state —
    // design decision flagged in PR #780), but only when the samples actually rebuild a usable
    // FocusInverseMap: a sweep whose feature medians can't anchor an inverse map (e.g. probes with junk
    // star lists despite a fittable AverageHFR curve) must not clobber a good stored calibration.
    // Post-success + best-effort like the other bookkeeping: a store/device fault here must never turn
    // a completed autofocus into a reported failure.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-success bookkeeping boundary: the calibration write touches device info + the profile store; the sweep already succeeded and must be reported as such. CA1031's log-and-recover boundary applies.")]
    private void RecordCalibrationQuietly(List<(int Position, FocusFeatureVector Features)> probeFeatures) {
        try {
            var samples = new List<FocusCalibrationSample>(probeFeatures.Count);
            foreach (var (position, features) in probeFeatures) {
                samples.Add(new FocusCalibrationSample(position, features));
            }
            if (FocusInverseMap.Build(samples) is null) {
                LogCalibrationSkipped(samples.Count);
                return;
            }

            var dtos = new List<FocusCalibrationSampleDto>(probeFeatures.Count);
            foreach (var (position, f) in probeFeatures) {
                dtos.Add(new FocusCalibrationSampleDto(
                    FocuserPosition: position,
                    StarCount: f.StarCount,
                    MedianHfr: f.MedianHFR,
                    MedianFwhm: f.MedianFWHM,
                    MedianRoundness: f.MedianRoundness,
                    MedianPeakToBackground: f.MedianPeakToBackground,
                    MedianDonutOuterDiameter: f.MedianDonutOuterDiameter,
                    MedianDonutInnerDiameter: f.MedianDonutInnerDiameter,
                    MedianRingThickness: f.MedianRingThickness,
                    MedianDonutShadowDepth: f.MedianDonutShadowDepth));
            }

            // NaN (focuser has no temperature probe) is unrepresentable in JSON — store null instead.
            double? temperature = _focuser.GetInfo()?.Temperature;
            if (temperature is { } t && !double.IsFinite(t)) {
                temperature = null;
            }

            _profiles.PutFocusCalibration(new FocusCalibrationDto(
                Samples: dtos,
                CalibratedUtc: DateTimeOffset.UtcNow,
                FocuserTemperatureC: temperature,
                Filter: _filterWheel?.GetInfo()?.SelectedFilter?.Name));
            LogCalibrationRecorded(dtos.Count);
        } catch (Exception ex) {
            LogCalibrationRecordFailed(ex);
        }
    }

    // §59.10 collimation health — a free byproduct of the completed sweep. The MOST-DEFOCUSED probe's donut
    // stars (widest, best-resolved obstruction shadow) vector-average (in CollimationEvaluator) into a
    // decentering verdict; a refractor / in-focus field yields no donut stars → Insufficient, silently skipped.
    // A confident read is logged + broadcast as a WS event; the actionable Slight/Significant bands also post a
    // user notification. Post-success + best-effort: a diagnostic read must never turn a completed autofocus
    // into a failure.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-success diagnostic boundary: the §59.10 collimation read runs after a successful sweep and must never fail it; any fault is logged and swallowed. CA1031's log-and-recover boundary applies.")]
    private async Task EvaluateCollimationQuietlyAsync(
            IReadOnlyList<(int Position, IReadOnlyList<DetectedStar> Stars)> probeStars,
            double bestPosition, int width, int height) {
        try {
            // Pick the probe furthest from the fitted best focus — the widest donut with the deepest, best-
            // resolved obstruction shadow, and each near-centre star counted once (no cross-step double-count).
            IReadOnlyList<DetectedStar> stars = Array.Empty<DetectedStar>();
            double maxDist = -1;
            foreach (var (position, list) in probeStars) {
                double dist = Math.Abs(position - bestPosition);
                if (dist > maxDist) {
                    maxDist = dist;
                    stars = list;
                }
            }

            var verdict = CollimationEvaluator.Evaluate(stars, width, height);
            if (verdict.Severity == CollimationSeverity.Insufficient) {
                return; // no confident read (refractor, in-focus, or too few near-centre donut stars)
            }
            LogCollimation(verdict.Severity, verdict.OffsetPercent, verdict.DirectionDegrees, verdict.StarsUsed);
            await PublishCollimationAsync(verdict).ConfigureAwait(false);
            if (verdict.Severity is CollimationSeverity.Slight or CollimationSeverity.Significant) {
                await NotifyCollimationAsync(verdict).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogCollimationFailed(ex);
        }
    }

    // Broadcast every confident verdict on the WS channel (cheap; the client decides what to show). AOT-safe
    // JsonElement construction (ToJsonString → Parse) per the GuiderService/SafetyReactionService precedent.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never abort the post-success collimation read. Log-and-recover boundary.")]
    private async Task PublishCollimationAsync(CollimationVerdict verdict) {
        if (_ws is null) {
            return;
        }
        try {
            var payload = new JsonObject {
                ["severity"] = verdict.Severity.ToString().ToLowerInvariant(),
                ["offset_percent"] = Math.Round(verdict.OffsetPercent, 2),
                ["direction_degrees"] = Math.Round(verdict.DirectionDegrees, 1),
                ["stars_used"] = verdict.StarsUsed,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.AutofocusCollimationVerdict, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogCollimationPublishFailed(ex);
        }
    }

    // Only the actionable bands notify the user (§59.10): Slight → a warning, Significant → critical with the
    // "collimate before continuing" recommendation. The eyepiece-referenced clock-position direction is a later
    // slice (it needs the optical train's mirror flips), so the message reports magnitude, not a clock face.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the completed sweep. Log-and-recover boundary.")]
    private async Task NotifyCollimationAsync(CollimationVerdict verdict) {
        if (_notifications is null) {
            return;
        }
        try {
            bool significant = verdict.Severity == CollimationSeverity.Significant;
            var message = $"Collimation check after autofocus: the obstruction-shadow centroid is offset {verdict.OffsetPercent:0.#}% of the donut diameter, measured across {verdict.StarsUsed} donut stars."
                + (significant ? " Recommended to collimate before continuing." : string.Empty);
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: significant ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                Category: NotificationCategory.Equipment,
                Title: significant ? "Significant miscollimation detected" : "Slight miscollimation detected",
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogCollimationNotifyFailed(ex);
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

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: completed sweep could not be recorded into the session history — the §59.5 triggers keep their previous reference point")]
    private partial void LogHistoryRecordFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus: §59.2 Smart Focus calibration refreshed from this sweep ({Samples} labelled samples)")]
    private partial void LogCalibrationRecorded(int samples);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: this sweep's {Samples} samples could not rebuild a usable inverse map — keeping the previously stored §59.2 Smart Focus calibration")]
    private partial void LogCalibrationSkipped(int samples);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: failed to record the §59.2 Smart Focus calibration — the sweep still succeeded")]
    private partial void LogCalibrationRecordFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus collimation ({Severity}): centroid offset {OffsetPercent:0.#}% of donut diameter toward {DirectionDegrees:0.#}° in the image frame, from {StarsUsed} donut stars")]
    private partial void LogCollimation(CollimationSeverity severity, double offsetPercent, double directionDegrees, int starsUsed);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: §59.10 collimation evaluation failed on a completed sweep — the sweep still succeeded")]
    private partial void LogCollimationFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: failed to broadcast the §59.10 collimation verdict WS event — the sweep still succeeded")]
    private partial void LogCollimationPublishFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: failed to post the §59.10 collimation notification — the sweep still succeeded")]
    private partial void LogCollimationNotifyFailed(Exception ex);
}
