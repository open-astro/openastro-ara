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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Coarse focus search ahead of the fine V-curve sweep. The fine sweep only spans
/// ±<c>Steps·StepSize</c> and measures at full resolution, where a badly defocused star is a faint,
/// wide disc that sinks under the detection threshold — so a start far from focus used to fail with
/// "untrustworthy probe". The coarse pass measures on a software-binned frame (averaging lifts the
/// disc's surface brightness above the noise and, on a one-shot-colour sensor, folds whole Bayer
/// tiles into luminance), walks toward smaller HFR with growing steps until it overshoots, then
/// halves the step back down to the sweep half-width and hands the fine sweep its centre.
/// </summary>
public sealed partial class AutofocusSweepService {

    /// <summary>Software bin factor for coarse probes. A multiple of 2 so each bin covers whole
    /// Bayer tiles; 8 measured ~22 px native HFR on an 80 px disc where full resolution read 4.6.</summary>
    internal const int CoarseBinFactor = 8;

    /// <summary>Probe budget for the coarse pass (direction + expansion + refinement).</summary>
    internal const int CoarseMaxProbes = 20;

    /// <summary>The expanding step stops growing at this multiple of the sweep half-width.</summary>
    internal const int CoarseMaxStepMultiplier = 8;

    /// <summary>How many times the fine sweep may re-centre on an edge minimum before failing.</summary>
    internal const int MaxSweepRecentres = 2;

    /// <summary>Production coarse metric: native-pixel HFR measured on a software-binned frame, or
    /// +∞ when too few stars are measurable (treated as "worse than anything measured").</summary>
    internal static double DefaultCoarseMetric(AnalysisFrame frame, CancellationToken ct) {
        var factor = CoarseBinFactor;
        if (frame.Width / factor < 32 || frame.Height / factor < 32) {
            factor = 1;
        }
        var (pixels, width, height) = SoftwareBin(frame.Pixels.Span, frame.Width, frame.Height, factor);
        var result = StarDetector.Detect(
            pixels, width, height,
            new StarDetectionParams { Sensitivity = 8.0, NoiseReduction = 0, IsAutoFocus = true },
            ct);
        var hfr = result.AverageHFR;
        return result.DetectedStars >= MinStarsPerProbe && hfr > 0 && double.IsFinite(hfr)
            ? hfr * factor
            : double.PositiveInfinity;
    }

    internal static (ushort[] Pixels, int Width, int Height) SoftwareBin(ReadOnlySpan<ushort> pixels, int width, int height, int factor) {
        if (factor <= 1) {
            return (pixels.ToArray(), width, height);
        }
        int ow = width / factor, oh = height / factor;
        var output = new ushort[ow * oh];
        var area = factor * factor;
        for (int y = 0; y < oh; y++) {
            for (int x = 0; x < ow; x++) {
                long sum = 0;
                for (int j = 0; j < factor; j++) {
                    int row = (y * factor + j) * width + x * factor;
                    for (int i = 0; i < factor; i++) {
                        sum += pixels[row + i];
                    }
                }
                output[y * ow + x] = (ushort)(sum / area);
            }
        }
        return (output, ow, oh);
    }

    /// <summary>
    /// Find the centre for the fine sweep. Returns <paramref name="start"/> unchanged when the start
    /// already brackets the minimum (the usual in-session refocus), a new centre when focus lies
    /// elsewhere, or null (logged) when no position yields measurable stars or the probe budget runs
    /// out before the minimum is bracketed.
    /// </summary>
    private async Task<int?> CoarseCentreAsync(AutofocusSettingsDto settings, int start, (int Min, int Max) travel, IProgress<ApplicationStatus> progress, CancellationToken token) {
        if (_coarseMetric is null) {
            return start;
        }
        var baseStep = settings.Steps * settings.StepSize;
        var measured = new Dictionary<int, double>();
        _lastCoarseFailure = null;

        async Task<double> Measure(int position) {
            if (measured.TryGetValue(position, out var known)) {
                return known;
            }
            token.ThrowIfCancellationRequested();
            Report(progress, $"Autofocus: coarse search at {position}");
            var reached = await _focuser.MoveFocuser(position, token).ConfigureAwait(false);
            var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureSeconds, settings.Binning, token).ConfigureAwait(false);
            var hfr = _coarseMetric(frame, token);
            LogCoarseProbe(reached, hfr);
            measured[position] = hfr;
            // The coarse metric reports no star count (∞ = too few to measure); the run record shows it as 0.
            await RecordProbeAsync("coarse", reached, hfr, 0, double.IsFinite(hfr), CoarseMaxProbes, null).ConfigureAwait(false);
            return hfr;
        }

        var pos = start;
        var h = await Measure(pos).ConfigureAwait(false);
        int dir;
        bool InTravel(int position) => position >= travel.Min && position <= travel.Max;
        var up = start + baseStep;
        var hUp = InTravel(up) ? await Measure(up).ConfigureAwait(false) : double.PositiveInfinity;
        if (hUp < h) {
            dir = 1;
            pos = up;
            h = hUp;
        } else {
            var down = start - baseStep;
            var hDown = InTravel(down) ? await Measure(down).ConfigureAwait(false) : double.PositiveInfinity;
            if (hDown < h) {
                dir = -1;
                pos = down;
                h = hDown;
            } else if (double.IsFinite(h)) {
                LogCoarseResult(start, h);
                return start;
            } else {
                _lastCoarseFailure = $"no measurable stars at {start} or ±{baseStep} — check the sky, the exposure, or rough-focus by hand";
                LogSweepFailed(_lastCoarseFailure);
                return null;
            }
        }

        // Expand: double the step while HFR keeps improving; the first rise brackets the minimum.
        var step = baseStep;
        var bracketed = false;
        while (measured.Count < CoarseMaxProbes) {
            step = Math.Min(step * 2, baseStep * CoarseMaxStepMultiplier);
            var next = pos + dir * step;
            if (!InTravel(next)) {
                bracketed = true; // the travel stop is the far side of the bracket
                break;
            }
            var hNext = await Measure(next).ConfigureAwait(false);
            if (hNext < h) {
                pos = next;
                h = hNext;
            } else {
                bracketed = true;
                break;
            }
        }
        if (!bracketed) {
            _lastCoarseFailure = $"coarse search ran {measured.Count} probes without bracketing focus (best HFR {h:0.#} at {pos}) — rough-focus by hand";
            LogSweepFailed(_lastCoarseFailure);
            return null;
        }

        // Refine: halve the step around the best point until it is within the fine sweep's half-width.
        while (step > baseStep) {
            step = Math.Max(baseStep, step / 2);
            foreach (var candidate in new[] { pos + step, pos - step }) {
                if (!InTravel(candidate)) {
                    continue;
                }
                var hc = await Measure(candidate).ConfigureAwait(false);
                if (hc < h) {
                    pos = candidate;
                    h = hc;
                }
            }
        }
        LogCoarseResult(pos, h);
        return pos;
    }

    /// <summary>Production travel source: an absolute focuser's cached [Min, Max] once its driver has
    /// reported a real range; unbounded for a relative focuser (its tracked position has no travel stop
    /// at 0, so clamping there would pin every sweep near the start); null for an unreported range or
    /// no focuser, which keeps the sweep at or above 0.</summary>
    internal static async Task<(int Min, int Max)?> FocuserTravelAsync(IFocuserService focusers, CancellationToken ct) =>
        (await focusers.GetAsync(ct).ConfigureAwait(false))?.Capabilities switch {
            { AbsoluteFocuser: false } => (int.MinValue, int.MaxValue),
            { MaxPosition: > 0 } caps => (caps.MinPosition, caps.MaxPosition),
            _ => null,
        };

    /// <summary>Clamp a fine-sweep centre so every probe, from <c>centre − Steps·StepSize</c> up to the
    /// overshoot at <c>centre + Steps·StepSize + StepSize</c>, lies inside <paramref name="travel"/>.
    /// A travel too short for the whole sweep only keeps the bottom probe at or above the minimum.</summary>
    internal static int ClampSweepCentre(int centre, AutofocusSettingsDto settings, (int Min, int Max) travel) {
        var halfWidth = settings.Steps * settings.StepSize;
        var lowest = travel.Min == int.MinValue ? int.MinValue : travel.Min + halfWidth;
        var highest = travel.Max == int.MaxValue ? int.MaxValue : travel.Max - halfWidth - settings.StepSize;
        return highest >= lowest ? Math.Clamp(centre, lowest, highest) : Math.Max(centre, lowest);
    }

    /// <summary>The edge position when the sampled minimum sits strictly at an end of the sweep.</summary>
    internal static int? EdgeMinimum(IReadOnlyList<FocusPoint> points) {
        if (points.Count < 2) {
            return null;
        }
        var sorted = new List<FocusPoint>(points);
        sorted.Sort((a, b) => a.Position.CompareTo(b.Position));
        var minIdx = 0;
        for (int i = 1; i < sorted.Count; i++) {
            if (sorted[i].Hfr < sorted[minIdx].Hfr) {
                minIdx = i;
            }
        }
        if (minIdx == 0 && sorted[0].Hfr < sorted[1].Hfr) {
            return (int)Math.Round(sorted[0].Position);
        }
        var last = sorted.Count - 1;
        if (minIdx == last && sorted[last].Hfr < sorted[last - 1].Hfr) {
            return (int)Math.Round(sorted[last].Position);
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus coarse probe: position {Position} HFR {Hfr:0.##} (native px, binned measurement)")]
    private partial void LogCoarseProbe(int position, double hfr);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus coarse search: centring the fine sweep at {Position} (HFR {Hfr:0.##})")]
    private partial void LogCoarseResult(int position, double hfr);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus probe at {Position} skipped: too few stars to measure ({Stars} stars, HFR {Hfr:0.###})")]
    private partial void LogProbeSkipped(int position, int stars, double hfr);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autofocus: curve minimum at the sweep edge — re-centring at {Position} (re-sweep {Attempt})")]
    private partial void LogSweepRecentred(int position, int attempt);
}
