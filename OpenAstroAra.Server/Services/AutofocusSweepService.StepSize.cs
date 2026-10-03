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
/// §59.8 "step size: auto from the focuser's reported step size + previous curve width". The fine sweep
/// spans ±<c>Steps·StepSize</c>; a step sized for the wrong rig puts every outer probe on a plateau (the
/// sweep is wider than the V, the fit explains nothing) or never leaves the bottom (no wing to fit). Seen on
/// the RedCat rig on 2026-10-03: a 7 × 50-step sweep over a V whose HFR doubles 100 steps from focus fit at
/// R² 0.57 with eight probes flat at HFR 4.
///
/// Resolution order when the profile's <c>step_size_auto</c> is on:
///  1. <b>measured</b> — the HFR-doubling half-width the last completed sweep stored with the focus
///     calibration (<see cref="FocusCalibrationDto.CurveHalfWidthSteps"/>): the outer probes land at
///     <see cref="HalfWidthSweepFactor"/> × that offset (HFR ≈ 2.8× the minimum on a hyperbola — well up
///     the slope, short of the plateau).
///  2. <b>cfz</b> — no sweep yet, but the focuser reports µm/step and the optics are declared: one step ≈
///     half the critical focus zone (CFZ ≈ 2.2 µm × f²), the same seed the setup wizard applies.
///  3. <b>default</b> — the profile's stored step size: the deliberately wide first sweep that measures the V.
/// Off, the stored step size is used as typed (<b>manual</b>).
/// </summary>
public sealed partial class AutofocusSweepService {

    /// <summary>Outer fine probes sit at this multiple of the HFR-doubling offset. 1.5 puts them at ≈2.8×
    /// the minimum HFR on a hyperbolic V: on the slope, before the wings flatten out.</summary>
    internal const double HalfWidthSweepFactor = 1.5;

    /// <summary>Bounds for every automatic step size (the wizard's seed uses the same): a degenerate width or
    /// train can't produce a useless 1-step sweep or a hardware-grinding multi-thousand-step one.</summary>
    internal const int MinAutoStepSize = 5;
    internal const int MaxAutoStepSize = 500;

    /// <summary>CFZ ≈ this × f-ratio², in µm (the standard visual approximation).</summary>
    internal const double CfzCoefficientUm = 2.2;

    /// <summary>How far beyond the sampled half-span the fitted model is searched for the doubling offset —
    /// a sweep narrower than the V still yields a width (the model is analytic), within reason.</summary>
    internal const double HalfWidthSearchSpanFactor = 4.0;

    /// <summary>The step size this run sweeps with and where it came from (<c>manual</c> | <c>measured</c> |
    /// <c>cfz</c> | <c>default</c>). Pure — the sweep calls it once per run.</summary>
    internal static (int StepSize, string Source) ResolveStepSize(
            AutofocusSettingsDto settings, FocusCalibrationDto? calibration, double? focuserStepUm, OpticsSettingsDto? optics) {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.StepSizeAuto) {
            return (settings.StepSize, "manual");
        }
        if (calibration?.CurveHalfWidthSteps is { } halfWidth && double.IsFinite(halfWidth) && halfWidth > 0 && settings.Steps >= 1) {
            var step = (int)Math.Round(HalfWidthSweepFactor * halfWidth / settings.Steps);
            return (Math.Clamp(step, MinAutoStepSize, MaxAutoStepSize), "measured");
        }
        if (CfzStepSize(focuserStepUm, optics) is int cfz) {
            return (cfz, "cfz");
        }
        return (settings.StepSize, "default");
    }

    /// <summary>Half the critical focus zone in focuser steps, or null when the focuser doesn't report µm/step
    /// (the ZWO EAF via AlpacaBridge reports 0) or the optics aren't declared. The reducer, when set, scales the
    /// focal length as it does for the pixel scale.</summary>
    internal static int? CfzStepSize(double? focuserStepUm, OpticsSettingsDto? optics) {
        if (focuserStepUm is not { } um || !(um > 0) || optics is null) {
            return null;
        }
        if (!(optics.FocalLengthMm > 0) || !(optics.ApertureMm > 0)) {
            return null;
        }
        var focal = optics.FocalLengthMm * (optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0);
        var fRatio = focal / optics.ApertureMm;
        var cfzUm = CfzCoefficientUm * fRatio * fRatio;
        var steps = (int)Math.Round(cfzUm / 2 / um);
        return Math.Clamp(steps, MinAutoStepSize, MaxAutoStepSize);
    }

    /// <summary>The V's half-width: the focuser offset from the fitted best focus at which the fitted HFR
    /// reaches twice its minimum, averaged over both sides. Measured on the MODEL (parabola or hyperbola), so
    /// a sweep wider than the V (plateau wings) or narrower (no wing reached) still yields a width. Null for an
    /// unusable fit or a curve that never doubles within <see cref="HalfWidthSearchSpanFactor"/> × the sampled
    /// half-span (near-flat — nothing to size from).</summary>
    internal static double? CurveHalfWidth(FocusCurveFitResult fit, double minPosition, double maxPosition) {
        if (fit is not { IsUsable: true, Model: not null } || !(fit.PredictedHfr > 0) || !double.IsFinite(fit.BestPosition)) {
            return null;
        }
        var halfSpan = (maxPosition - minPosition) / 2;
        if (!(halfSpan > 0) || !double.IsFinite(halfSpan)) {
            return null;
        }
        var limit = halfSpan * HalfWidthSearchSpanFactor;
        var target = fit.PredictedHfr * 2;
        var up = DoublingOffset(fit.Model, fit.BestPosition, +1, limit, target);
        var down = DoublingOffset(fit.Model, fit.BestPosition, -1, limit, target);
        return (up, down) switch {
            (null, null) => null,
            (null, _) => down,
            (_, null) => up,
            _ => (up.Value + down.Value) / 2,
        };
    }

    // Both fitted models are upward curves with their vertex at the best position, so HFR is monotone in the
    // offset on each side — a bisection finds the crossing. Null when the model hasn't doubled by `limit`.
    private static double? DoublingOffset(Func<double, double> model, double best, int direction, double limit, double target) {
        var atLimit = model(best + direction * limit);
        if (!double.IsFinite(atLimit) || atLimit < target) {
            return null;
        }
        double lo = 0, hi = limit;
        for (var i = 0; i < 60 && hi - lo > 1e-3; i++) {
            var mid = (lo + hi) / 2;
            if (model(best + direction * mid) >= target) {
                hi = mid;
            } else {
                lo = mid;
            }
        }
        return hi;
    }

    /// <summary>Production µm/step source: the connected focuser's reported step size, or null when it reports
    /// none (0 — the EAF through AlpacaBridge) or no focuser is selected.</summary>
    internal static async Task<double?> FocuserStepUmAsync(IFocuserService focusers, CancellationToken ct) =>
        (await focusers.GetAsync(ct).ConfigureAwait(false))?.Capabilities is { StepSizeUm: > 0 } caps
            ? caps.StepSizeUm
            : null;

    // The inputs the resolver needs beyond the settings, read best-effort: a device or store hiccup here must
    // not fail the sweep — it just falls through to the profile's stored step size.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Step-size inputs are advisory: a focuser-info or profile read fault degrades to the stored step size. Log-and-recover boundary.")]
    private async Task<(int StepSize, string Source)> ResolveStepSizeQuietlyAsync(AutofocusSettingsDto settings, CancellationToken token) {
        FocusCalibrationDto? calibration = null;
        OpticsSettingsDto? optics = null;
        double? stepUm = null;
        try {
            calibration = _profiles.GetFocusCalibration();
            optics = _profiles.GetOpticsSettings();
            if (settings.StepSizeAuto && _focuserStepUm is not null) {
                stepUm = await _focuserStepUm(token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogStepSizeInputsFailed(ex);
        }
        return ResolveStepSize(settings, calibration, stepUm, optics);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus: sweeping with step size {StepSize} ({Source}; {Steps} steps each side)")]
    private partial void LogStepSize(int stepSize, string source, int steps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus: the V's HFR doubles {HalfWidth:0.#} steps from focus — stored for the next sweep's step size")]
    private partial void LogCurveHalfWidth(double halfWidth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Autofocus: could not read the step-size inputs (focuser step size / optics / calibration) — using the profile's stored step size")]
    private partial void LogStepSizeInputsFailed(Exception ex);
}
