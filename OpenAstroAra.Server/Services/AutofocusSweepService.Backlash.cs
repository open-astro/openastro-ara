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
using OpenAstroAra.Server.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §59.7 layer 1 — the backlash probe appended to a successful Classic sweep (#1139). The sweep and
/// Smart Focus approach every position from above, so the only backlash that ever bites is the
/// UPWARD one (the Smart bracket's + side, a vertex shot above the centre). Measured in three frames
/// from best focus, where the fitted V turns HFR back into distance:
/// <list type="number">
/// <item>move DOWN 4 steps and shoot (the control: HFR must read above the baseline there);</item>
/// <item>move straight UP to best and shoot — a focuser with backlash stops short, and the V's
/// model says by how much (<see cref="InvertBelowBest"/>);</item>
/// <item>move DOWN 4 steps again, UP to best + that estimate, and shoot: back at the baseline means
/// the estimate is verified.</item>
/// </list>
/// The probe always ends by landing on best from above, so the focuser rests exactly where the sweep
/// left it. Best-effort: a probe that cannot measure (too few stars, no model) records nothing.
/// </summary>
public sealed partial class AutofocusSweepService {

    /// <summary>How far below best the control shot sits, in sweep steps.</summary>
    internal const int BacklashProbeSteps = 4;
    /// <summary>A return shot counts as "back at focus" when its HFR is within the fitted curve's rise over
    /// this fraction of a sweep step above the measured baseline — the V is flat near focus (a 30-step
    /// shortfall on a 100-step sweep lifts HFR by ~1 %), so a fixed percentage would miss real backlash;
    /// the curve's own slope says what the sweep can resolve.</summary>
    internal const double BacklashResolutionSteps = 0.25;

    /// <summary>The HFR at or under which a return shot is "back at focus": the measured baseline plus the
    /// model's rise over a quarter step below best. Null when the model does not rise there.</summary>
    internal static double? ReturnTolerance(Func<double, double>? model, double best, double baselineHfr, int stepSize) {
        if (model is null || stepSize <= 0 || !double.IsFinite(baselineHfr) || baselineHfr <= 0) {
            return null;
        }
        var rise = model(best - stepSize * BacklashResolutionSteps) - model(best);
        return double.IsFinite(rise) && rise > 0 ? baselineHfr + rise : null;
    }

    internal readonly record struct BacklashMeasurement(double? UpSteps, bool Verified);

    /// <summary>The distance below <paramref name="best"/> at which the fitted model predicts
    /// <paramref name="hfr"/> — i.e. how far short of best a focuser that reads <paramref name="hfr"/>
    /// after an upward move actually stopped. <paramref name="hfr"/> must be expressed on the MODEL's
    /// scale (the caller shifts a measured excess onto <c>model(best)</c>). Clamped to
    /// [0, <paramref name="maxOffset"/>]; null when the model is missing or not rising below best.</summary>
    internal static double? InvertBelowBest(Func<double, double>? model, double best, double hfr, double maxOffset) {
        if (model is null || maxOffset <= 0 || !double.IsFinite(hfr)) {
            return null;
        }
        var atBest = model(best);
        var atMax = model(best - maxOffset);
        if (!double.IsFinite(atBest) || !double.IsFinite(atMax) || atMax <= atBest) {
            return null;
        }
        if (hfr <= atBest) {
            return 0;
        }
        if (hfr >= atMax) {
            return maxOffset;
        }
        double lo = 0, hi = maxOffset;
        for (var i = 0; i < 50; i++) {
            var mid = (lo + hi) / 2;
            if (model(best - mid) < hfr) {
                lo = mid;
            } else {
                hi = mid;
            }
        }
        return (lo + hi) / 2;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-success measurement: a capture/metric/focuser fault in the backlash probe must not turn a completed sweep into a failure. Log-and-recover boundary.")]
    private async Task<BacklashMeasurement> MeasureBacklashQuietlyAsync(AutofocusSettingsDto settings, FocusCurveFitResult fit, int best, double baselineHfr, (int Min, int Max) travel, CancellationToken token) {
        if (fit.Model is null || !double.IsFinite(baselineHfr) || baselineHfr <= 0 || settings.StepSize <= 0) {
            return new BacklashMeasurement(null, false);
        }
        // The control shot must stay inside the focuser's travel: shorten the span to what is below
        // best, and give up when less than one step is.
        var span = Math.Min(BacklashProbeSteps * settings.StepSize, best - travel.Min);
        if (span < settings.StepSize) {
            LogBacklashProbeInconclusive("best focus sits at the bottom of the travel; no room for the control shot");
            return new BacklashMeasurement(null, false);
        }
        if (ReturnTolerance(fit.Model, best, baselineHfr, settings.StepSize) is not double tolerance) {
            LogBacklashProbeInconclusive("the fitted curve does not rise below best focus");
            return new BacklashMeasurement(null, false);
        }
        try {
            _tracker?.SetPhase("backlash");
            // 1. the control, below best (approached downward like every sweep sample)
            var low = await _focuser.MoveFocuser(best - span, token).ConfigureAwait(false);
            var control = await BacklashShotAsync(settings, low, 1, token).ConfigureAwait(false);
            if (control is not double controlHfr || controlHfr <= tolerance) {
                LogBacklashProbeInconclusive(control is null ? "the control shot had too few stars" : "the control shot did not read out of focus");
                return await BacklashDoneAsync(settings, best, null, false, token).ConfigureAwait(false);
            }
            // 2. straight up to best: a focuser with backlash stops short, and reads it as HFR
            var up = await _focuser.MoveFocuser(best, token).ConfigureAwait(false);
            var back = await BacklashShotAsync(settings, up, 2, token).ConfigureAwait(false);
            if (back is not double backHfr) {
                LogBacklashProbeInconclusive("the return shot had too few stars");
                return await BacklashDoneAsync(settings, best, null, false, token).ConfigureAwait(false);
            }
            if (backHfr <= tolerance) {
                LogBacklashMeasured(0, true);
                return await BacklashDoneAsync(settings, best, 0, true, token).ConfigureAwait(false);
            }
            // Invert the return shot's EXCESS over the measured baseline on the model's own minimum
            // (review of #1319): the confirmation frame routinely reads off the fit's predicted minimum
            // (0.96 measured vs 1.62 re-fitted on 2026-10-03), so inverting the absolute HFR against
            // model(best) would misplace the shortfall by that whole gap.
            var estimate = InvertBelowBest(fit.Model, best, fit.Model(best) + (backHfr - baselineHfr), span);
            if (estimate is not double offset || offset <= 0) {
                LogBacklashProbeInconclusive("the fitted curve could not place the return shot");
                return await BacklashDoneAsync(settings, best, null, false, token).ConfigureAwait(false);
            }
            var steps = Math.Max(1, (int)Math.Round(offset));
            if (best + steps > travel.Max) {
                LogBacklashProbeInconclusive("the compensated return would exceed the focuser's travel");
                return await BacklashDoneAsync(settings, best, null, false, token).ConfigureAwait(false);
            }
            // 3. verify: down again, then up to best + estimate — the counter overshoots by the
            // backlash so the optics land ON best
            await _focuser.MoveFocuser(best - span, token).ConfigureAwait(false);
            var compensated = await _focuser.MoveFocuser(best + steps, token).ConfigureAwait(false);
            var check = await BacklashShotAsync(settings, compensated, 3, token).ConfigureAwait(false);
            var verified = check is double checkHfr && checkHfr <= tolerance;
            LogBacklashMeasured(steps, verified);
            return await BacklashDoneAsync(settings, best, steps, verified, token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // A cancel during the probe must not throw away the sweep that already succeeded (the
            // sweep's own catch would restore the pre-sweep position): land on best, record nothing
            // for backlash, and let the sweep complete — same posture as the confirmation frame.
            LogBacklashProbeInconclusive("cancelled during the probe");
            await LandQuietlyAsync(best, settings).ConfigureAwait(false);
            return new BacklashMeasurement(null, false);
        } catch (Exception ex) {
            LogBacklashProbeFailed(ex);
            await LandQuietlyAsync(best, settings).ConfigureAwait(false);
            return new BacklashMeasurement(null, false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort landing after a failed or cancelled probe; a focuser fault here is logged, the sweep's result stands.")]
    private async Task LandQuietlyAsync(int best, AutofocusSettingsDto settings) {
        try {
            await LandFromAboveAsync(best, settings, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception restore) {
            LogBacklashProbeFailed(restore);
        }
    }

    /// <summary>Land on best from above (the sweep's resting discipline) and hand back the result.</summary>
    private async Task<BacklashMeasurement> BacklashDoneAsync(AutofocusSettingsDto settings, int best, double? steps, bool verified, CancellationToken token) {
        await LandFromAboveAsync(best, settings, token).ConfigureAwait(false);
        return new BacklashMeasurement(steps, verified);
    }

    /// <summary>One probe frame for the backlash phase: its HFR, or null when it had too few stars.</summary>
    private async Task<double?> BacklashShotAsync(AutofocusSettingsDto settings, int position, int index, CancellationToken token) {
        var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureSeconds, settings.Binning, token).ConfigureAwait(false);
        var result = _metric(frame, token);
        var hfr = result.AverageHFR;
        var kept = result.DetectedStars >= MinStarsPerProbe && hfr > 0 && double.IsFinite(hfr) && !result.NoiseFlooded;
        // No frame: the run's picture stays the confirmation frame at best focus (review of #1319) —
        // a defocused control shot must never replace the focused field the Smart Focus pane shows.
        await RecordProbeAsync("backlash", position, hfr, result.DetectedStars, kept, 3, frame: null).ConfigureAwait(false);
        LogBacklashShot(index, position, hfr, result.DetectedStars);
        return kept ? hfr : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backlash probe shot {Index} at {Position}: HFR {Hfr:0.000}, {Stars} stars")]
    private partial void LogBacklashShot(int index, int position, double hfr, int stars);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backlash probe: upward backlash {Steps} steps (verified={Verified})")]
    private partial void LogBacklashMeasured(int steps, bool verified);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backlash probe inconclusive: {Reason}; nothing recorded")]
    private partial void LogBacklashProbeInconclusive(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backlash probe failed; nothing recorded")]
    private partial void LogBacklashProbeFailed(Exception ex);
}
