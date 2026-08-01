#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Core.Guiding;

public static class GuidingResponseAnalyzer {
    /// <summary>
    /// Estimate observed command-to-error-response latency. This is a guide-frame
    /// estimate, not a claim about internal motor latency: it measures the first
    /// subsequent frame where the vector error falls by at least 25 percent.
    /// </summary>
    public static double? EstimateObservedResponseLatencyMilliseconds(
        IReadOnlyList<GuidingTelemetrySample> samples) {
        var usable = Usable(samples);
        var delays = new List<double>();
        for (var i = 0; i < usable.Length - 1; i++) {
            if (usable[i].RaPulseMilliseconds + usable[i].DecPulseMilliseconds <= 0) continue;
            var before = VectorMagnitude(usable[i].RaRawPixels, usable[i].DecRawPixels);
            if (before <= double.Epsilon) continue;
            for (var j = i + 1; j < Math.Min(usable.Length, i + 5); j++) {
                var after = VectorMagnitude(usable[j].RaRawPixels, usable[j].DecRawPixels);
                var delay = (usable[j].TimestampUtc - usable[i].TimestampUtc).TotalMilliseconds;
                if (after <= before * .75 && delay >= 0 && double.IsFinite(delay)) {
                    delays.Add(delay);
                    break;
                }
            }
        }
        return delays.Count == 0 ? null : Percentile(delays.ToArray(), .5);
    }

    /// <summary>Estimate observed correction authority from error-vector change per pulse.</summary>
    public static double? EstimateEffectiveCorrectionAuthorityArcsecPerSecond(
        IReadOnlyList<GuidingTelemetrySample> samples, double guidePixelScaleArcsecPerPixel) {
        if (guidePixelScaleArcsecPerPixel <= 0) return null;
        var usable = Usable(samples);
        var authority = new List<double>();
        for (var i = 0; i < usable.Length - 1; i++) {
            var pulseMilliseconds = usable[i].RaPulseMilliseconds + usable[i].DecPulseMilliseconds;
            if (pulseMilliseconds <= 0) continue;
            var deltaPixels = VectorMagnitude(
                usable[i + 1].RaRawPixels - usable[i].RaRawPixels,
                usable[i + 1].DecRawPixels - usable[i].DecRawPixels);
            var value = deltaPixels * guidePixelScaleArcsecPerPixel / (pulseMilliseconds / 1000);
            if (double.IsFinite(value) && value >= 0) authority.Add(value);
        }
        return authority.Count == 0 ? null : Percentile(authority.ToArray(), .5);
    }

    /// <summary>
    /// Estimate DEC backlash delay from guide-direction reversals. A reversal is
    /// complete when raw DEC motion first exceeds a robust frame-to-frame noise
    /// threshold. Null means no usable reversal occurred.
    /// </summary>
    public static double? EstimateDeclinationReversalDelayMilliseconds(
        IReadOnlyList<GuidingTelemetrySample> samples) {
        var usable = samples.Where(s => !s.IsDither && !s.IsSettling && !s.IsCalibration && !s.StarLost).ToArray();
        if (usable.Length < 3) return null;
        var differences = usable.Zip(usable.Skip(1), (a, b) => b.DecRawPixels - a.DecRawPixels)
            .Where(double.IsFinite).Select(Math.Abs).OrderBy(v => v).ToArray();
        if (differences.Length == 0) return null;
        var noise = 1.4826 * Percentile(differences, .5) / Math.Sqrt(2);
        var threshold = Math.Max(3 * noise, .01);
        var delays = new List<double>();
        for (var i = 1; i < usable.Length; i++) {
            var previous = DirectionSign(usable[i - 1].DecPulseDirection);
            var current = DirectionSign(usable[i].DecPulseDirection);
            if (previous == 0 || current == 0 || previous == current) continue;
            for (var j = i + 1; j < Math.Min(usable.Length, i + 40); j++) {
                var movement = Math.Abs(usable[j].DecRawPixels - usable[j - 1].DecRawPixels);
                if (movement >= threshold) {
                    var delay = (usable[j].TimestampUtc - usable[i].TimestampUtc).TotalMilliseconds;
                    if (delay >= 0 && double.IsFinite(delay)) delays.Add(delay);
                    break;
                }
            }
        }
        return delays.Count == 0 ? null : Percentile(delays.ToArray(), .5);
    }

    private static int DirectionSign(string? direction) =>
        string.Equals(direction, "North", StringComparison.OrdinalIgnoreCase) ? 1
        : string.Equals(direction, "South", StringComparison.OrdinalIgnoreCase) ? -1 : 0;

    private static GuidingTelemetrySample[] Usable(IReadOnlyList<GuidingTelemetrySample> samples) =>
        samples.Where(s => !s.IsDither && !s.IsSettling && !s.IsCalibration && !s.StarLost).ToArray();

    private static double VectorMagnitude(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Percentile(double[] values, double percentile) {
        if (values.Length == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (values.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return values[low] + (values[high] - values[low]) * (position - low);
    }
}
