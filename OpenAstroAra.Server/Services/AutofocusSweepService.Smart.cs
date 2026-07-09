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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageAnalysis;
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
/// §59.2 Smart Focus — the one-frame runner (the payoff of the calibration slices #780/#781): when the
/// profile carries a usable calibration, an AF trigger reads the rig's defocus from ONE exposure via
/// <see cref="FocusInverseMap.PredictOffsetMagnitude"/> and moves straight to predicted focus — 2-3 shots
/// (30-90 s) instead of the 9-probe Classic V-curve (3-5 min). Classic remains the calibrator and the
/// §59.11 safety net; every Smart failure degrades to it, so the worst case is exactly today's behavior
/// plus up to three cheap shots.
///
/// The §59.11 fallback ladder implemented here:
///  * Not calibrated / calibration temp drift &gt; <see cref="CalibrationTempDeltaC"/> (§59.13) /
///    samples no longer rebuild a map → Classic, silently (mode is visible in `autofocus.started`).
///  * Shot 1 has &lt; <see cref="SmartMinStars"/> stars, or its features predict no magnitude
///    (starless / more defocused than anything calibrated) → `autofocus.fallback_classic` + Classic.
///  * Shot 2 worse than shot 1 (direction guess wrong — magnitude-only map, §59.2): reverse with HALF
///    the magnitude, shot 3. Still worse → restore the start position, `fallback_classic`, Classic.
///  * Shot 2 improved but missed the target: continue by ±20% of the move, shot 3, keep the better of
///    the two positions — three shots is the Smart budget (§59.3), never a fourth.
/// Direction guess: toward the calibrated best-focus position (the rig usually drifts around it).
/// Target: within <see cref="TargetHfrTolerancePct"/> percent of the calibration's in-focus HFR.
/// </summary>
public sealed partial class AutofocusSweepService {

    /// <summary>§59.3/§59.11 — fewer stars than this on the Smart shot means the feature medians are
    /// untrustworthy for a one-frame prediction; the run falls back to Classic (whose per-probe gate
    /// is the looser <see cref="MinStarsPerProbe"/>).</summary>
    internal const int SmartMinStars = 30;

    /// <summary>§59.13 `target_hfr_tolerance_pct` default — done when HFR is within this percentage
    /// above the calibration's fitted in-focus HFR.</summary>
    internal const double TargetHfrTolerancePct = 5.0;

    /// <summary>§59.13 `calibration_temp_delta_c` default — a calibration measured more than this many
    /// °C away from the current focuser temperature is stale (focus scale shifts thermally); recalibrate
    /// via Classic instead of trusting it. Unjudgeable when either reading is missing (the gate can't fire).</summary>
    internal const double CalibrationTempDeltaC = 8.0;

    // §59.3 phase-2 step 7 — the shot-3 trim when shot 2 improved but missed: ±20% of the applied move.
    private const double Shot3TrimFraction = 0.2;

    /// <summary>
    /// Try the Smart path. Returns <c>true</c> on success (focus reached, bookkeeping recorded),
    /// <c>false</c> when Smart ran and failed (§59.11 — the caller runs Classic; the start position is
    /// already restored and `autofocus.fallback_classic` published), and <c>null</c> when Smart never
    /// started (not calibrated / stale / unusable — the caller runs Classic silently).
    /// Runs INSIDE the sweep gate; cancellation restores the start position and propagates.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Smart-run boundary, same rationale as RunSweepCoreAsync: probe captures / focuser moves / the metric can throw device, HTTP, or math exceptions; any escape must degrade to a restored position + the Classic fallback so the Smart path is never worse than the old behavior. CA1031's log-and-recover boundary applies.")]
    private async Task<bool?> TrySmartFocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var calibration = _profiles.GetFocusCalibration();
        if (calibration is null) {
            return null; // never calibrated — Classic IS the calibrator (§59.1)
        }
        var info = _focuser.GetInfo();
        if (info is not { Connected: true }) {
            return null; // Classic's own guard reports the failure properly
        }

        if (calibration.FocuserTemperatureC is { } calTemp && double.IsFinite(info.Temperature)) {
            var drift = Math.Abs(info.Temperature - calTemp);
            if (drift > CalibrationTempDeltaC) {
                LogSmartSkippedStale(drift, CalibrationTempDeltaC);
                return null;
            }
        }

        var samples = new List<FocusCalibrationSample>(calibration.Samples.Count);
        foreach (var s in calibration.Samples) {
            samples.Add(s.ToSample());
        }
        var map = FocusInverseMap.Build(samples);
        if (map is null) {
            LogSmartSkipped("stored calibration samples no longer rebuild a usable inverse map");
            return null;
        }

        var settings = _profiles.GetAutofocusSettings();
        var startPosition = info.Position;
        var targetHfr = map.InFocusHfr * (1.0 + TargetHfrTolerancePct / 100.0);
        await PublishAutofocusEventAsync(WsEventCatalog.AutofocusStarted, new JsonObject { ["mode"] = "smart" }).ConfigureAwait(false);

        try {
            // Shot 1 — read the rig's current defocus where it stands.
            var shot1 = await TakeSmartShotAsync(1, startPosition, settings, progress, token).ConfigureAwait(false);
            if (shot1.Features.StarCount < SmartMinStars) {
                return await FallBackAsync($"only {shot1.Features.StarCount} stars on the Smart shot (need {SmartMinStars})",
                    "too_few_stars", startPosition, restore: false).ConfigureAwait(false);
            }
            if (shot1.Hfr <= targetHfr) {
                // Already in focus — moving would only add backlash noise. One shot, done.
                LogSmartComplete(startPosition, shot1.Hfr, 1);
                RecordAutofocusQuietly();
                return true;
            }
            var magnitude = map.PredictOffsetMagnitude(shot1.Features);
            if (magnitude is null or <= 0) {
                return await FallBackAsync("the frame is more defocused than anything calibrated (or predicts no move)",
                    "outside_calibrated_range", startPosition, restore: false).ConfigureAwait(false);
            }

            // Direction guess (the map predicts magnitude only, §59.2): toward the calibrated best-focus
            // position — drift wanders around it, so the far side is the less likely one. Shot 2/3 verify.
            var direction = map.BestFocusOffset >= startPosition ? 1 : -1;
            var move = (int)Math.Round(direction * magnitude.Value);
            if (move == 0) {
                move = direction; // a sub-step prediction still probes the guessed side
            }

            // Shot 2 — at predicted focus.
            var position2 = await _focuser.MoveFocuser(startPosition + move, token).ConfigureAwait(false);
            var shot2 = await TakeSmartShotAsync(2, position2, settings, progress, token).ConfigureAwait(false);
            if (shot2.Features.StarCount < SmartMinStars) {
                // A starless/thin shot 2 (clouds, a bad move) has MedianHFR 0 and would "win" every raw
                // HFR comparison — never trust it as an improvement (review round-2 finding).
                return await FallBackAsync($"only {shot2.Features.StarCount} stars on shot 2 (need {SmartMinStars})",
                    "too_few_stars", startPosition, restore: settings.RestorePositionOnFailure).ConfigureAwait(false);
            }

            if (shot2.Hfr < shot1.Hfr) {
                if (shot2.Hfr <= targetHfr) {
                    LogSmartComplete(position2, shot2.Hfr, 2);
                    RecordAutofocusQuietly();
                    return true;
                }
                // Improved but missed — one ±20% trim in the same direction, then keep the better position.
                var trim = (int)Math.Round(move * Shot3TrimFraction);
                if (trim == 0) {
                    trim = direction;
                }
                var position3 = await _focuser.MoveFocuser(position2 + trim, token).ConfigureAwait(false);
                var shot3 = await TakeSmartShotAsync(3, position3, settings, progress, token).ConfigureAwait(false);
                // An untrustworthy (thin-star) trim shot counts as "worse": shot 2's position is a
                // VERIFIED improvement, so keep that known-good result rather than falling back.
                if (shot3.Features.StarCount < SmartMinStars || shot3.Hfr > shot2.Hfr) {
                    await _focuser.MoveFocuser(position2, token).ConfigureAwait(false);
                    LogSmartComplete(position2, shot2.Hfr, 3);
                } else {
                    LogSmartComplete(position3, shot3.Hfr, 3);
                }
                RecordAutofocusQuietly();
                return true;
            }

            // Shot 2 worse — the direction guess was wrong. Reverse from the START with half magnitude.
            var reversed = await _focuser.MoveFocuser(startPosition - Math.Sign(move) * Math.Max(1, Math.Abs(move) / 2), token).ConfigureAwait(false);
            var reversedShot = await TakeSmartShotAsync(3, reversed, settings, progress, token).ConfigureAwait(false);
            // The reversed shot must be BOTH trustworthy and a real improvement to claim success.
            if (reversedShot.Features.StarCount >= SmartMinStars && reversedShot.Hfr < shot1.Hfr) {
                LogSmartComplete(reversed, reversedShot.Hfr, 3);
                RecordAutofocusQuietly();
                return true;
            }

            // Diverged — three shots, still worse than where we started (§59.11).
            // Restore honors the profile's RestorePositionOnFailure like every Classic restore path —
            // a user who wants the focuser left where a failed run stopped gets that here too (the
            // Classic fallback then simply centers its sweep on wherever the ladder ended).
            return await FallBackAsync($"diverged after 3 shots (start HFR {shot1.Hfr:0.###}, best attempt {Math.Min(shot2.Hfr, reversedShot.Hfr):0.###})",
                "smart_focus_diverged", startPosition, restore: settings.RestorePositionOnFailure).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            await RestoreAsync(settings.RestorePositionOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            throw;
        } catch (Exception ex) {
            // Same boundary as RunSweepCoreAsync: a device/comms fault mid-Smart must degrade to a
            // restored position + the Classic fallback, never propagate out of RunAutofocusAsync
            // (review round-2 finding — MeridianFlipExecutor relies on the sweep's restore invariant).
            LogSmartErrored(ex);
            await RestoreAsync(settings.RestorePositionOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            await PublishAutofocusEventAsync(WsEventCatalog.AutofocusFallbackClassic, new JsonObject {
                ["reason"] = "smart_focus_error",
            }).ConfigureAwait(false);
            return false;
        }
    }

    private readonly record struct SmartShot(double Hfr, FocusFeatureVector Features);

    private async Task<SmartShot> TakeSmartShotAsync(
            int shotIndex, int position, AutofocusSettingsDto settings,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        progress.Report(new ApplicationStatus {
            Status = $"Smart Focus: shot {shotIndex} at position {position}",
            Progress = shotIndex,
            MaxProgress = 3,
            ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue,
        });
        var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureSeconds, settings.Binning, token).ConfigureAwait(false);
        var result = _metric(frame, token);
        var features = FocusFeatureExtractor.Extract(result);
        // The COMPARISON metric is the feature median (robust to the outliers a single frame carries),
        // not the detector's AverageHFR — Smart decisions ride on one frame per step, so the median's
        // outlier resistance matters more here than in the 9-probe sweep.
        var hfr = features.MedianHFR;
        LogSmartShot(shotIndex, position, hfr, features.StarCount);
        await PublishAutofocusEventAsync(WsEventCatalog.AutofocusShotComplete, new JsonObject {
            ["shot_index"] = shotIndex,
            ["position"] = position,
            ["hfr"] = double.IsFinite(hfr) ? Math.Round(hfr, 3) : 0.0,
            ["stars"] = features.StarCount,
        }).ConfigureAwait(false);
        return new SmartShot(hfr, features);
    }

    /// <summary>The §59.11 in-run failure exit: optionally restore the start position, announce the
    /// hand-off, and return <c>false</c> so the caller runs the Classic sweep.</summary>
    private async Task<bool?> FallBackAsync(string logReason, string wireReason, int startPosition, bool restore) {
        LogSmartFellBack(logReason);
        if (restore) {
            await RestoreAsync(true, startPosition, CancellationToken.None).ConfigureAwait(false);
        }
        await PublishAutofocusEventAsync(WsEventCatalog.AutofocusFallbackClassic, new JsonObject {
            ["reason"] = wireReason,
        }).ConfigureAwait(false);
        return false;
    }

    // Best-effort WS publish shared by the §59.15 lifecycle events; same AOT-safe construction and
    // log-and-recover boundary as the §59.10 collimation publish.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never abort or fail the AF run. Log-and-recover boundary.")]
    private async Task PublishAutofocusEventAsync(string type, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(type, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogAutofocusPublishFailed(ex, type);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus skipped ({Reason}) — running the Classic sweep")]
    private partial void LogSmartSkipped(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus skipped: calibration is thermally stale ({DriftC:0.#} °C drift > {LimitC} °C) — running the Classic sweep")]
    private partial void LogSmartSkippedStale(double driftC, double limitC);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus shot {Shot}: position {Position} HFR {Hfr:0.###} ({Stars} stars)")]
    private partial void LogSmartShot(int shot, int position, double hfr, int stars);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus complete: position {Position}, HFR {Hfr:0.###}, {Shots} shot(s)")]
    private partial void LogSmartComplete(int position, double hfr, int shots);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Smart Focus fell back to the Classic sweep: {Reason}")]
    private partial void LogSmartFellBack(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Smart Focus errored — restoring and falling back to the Classic sweep")]
    private partial void LogSmartErrored(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: failed to broadcast the {EventType} WS event — the AF run continues")]
    private partial void LogAutofocusPublishFailed(Exception ex, string eventType);
}
