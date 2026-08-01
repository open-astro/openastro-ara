#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Core.Guiding;

public static class GuidingScorer {
    private const int BootstrapSeed = 0x4A5247;

    public static GuidingRunMetrics CalculateMetrics(
        IReadOnlyList<GuidingTelemetrySample> samples,
        double guidePixelScaleArcsecPerPixel,
        double? mainPixelScaleArcsecPerPixel = null) {
        var usable = samples.Where(s => !s.IsDither && !s.IsSettling && !s.IsCalibration).ToArray();
        if (usable.Length == 0) return new GuidingRunMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, null, true, null);
        var ra = usable.Select(s => s.RaRawPixels * guidePixelScaleArcsecPerPixel).ToArray();
        var dec = usable.Select(s => s.DecRawPixels * guidePixelScaleArcsecPerPixel).ToArray();
        var total = ra.Zip(dec, (r, d) => Math.Sqrt(r * r + d * d)).ToArray();
        var pulseLimited = usable.Count(s => s.RaPulseLimited || s.DecPulseLimited) / (double)usable.Length;
        var cappedSameSign = usable.Count(s => (s.RaPulseLimited && Math.Sign(s.RaRawPixels) == Math.Sign(s.RaGuidePixels))
            || (s.DecPulseLimited && Math.Sign(s.DecRawPixels) == Math.Sign(s.DecGuidePixels))) / (double)usable.Length;
        var pulseSigns = usable.Select(s => PulseSign(s.RaPulseDirection, s.RaPulseMilliseconds))
            .Where(sign => sign != 0).ToArray();
        var oscillation = pulseSigns.Zip(pulseSigns.Skip(1), (a, b) => a != b).Count()
            / (double)Math.Max(1, pulseSigns.Length - 1);
        var reversals = usable.Zip(usable.Skip(1), (a, b) =>
            !string.IsNullOrWhiteSpace(a.DecPulseDirection) && !string.IsNullOrWhiteSpace(b.DecPulseDirection)
            && !string.Equals(a.DecPulseDirection, b.DecPulseDirection, StringComparison.OrdinalIgnoreCase)).Count() / (double)Math.Max(1, usable.Length - 1);
        var starLoss = usable.Count(s => s.StarLost) / (double)usable.Length;
        return new GuidingRunMetrics(usable.Length, RobustRms(total), RobustRms(ra), RobustRms(dec), Percentile(total, .95),
            Percentile(total, .99), oscillation, pulseLimited, cappedSameSign, starLoss, reversals, 0, null,
            starLoss > .1 || cappedSameSign > .5,
            GuidingResponseAnalyzer.EstimateDeclinationReversalDelayMilliseconds(usable),
            GuidingResponseAnalyzer.EstimateObservedResponseLatencyMilliseconds(usable),
            GuidingResponseAnalyzer.EstimateEffectiveCorrectionAuthorityArcsecPerSecond(
                usable, guidePixelScaleArcsecPerPixel));
    }

    public static GuidingScoreBreakdown CalculateScore(GuidingRunMetrics metrics, double? mainImageBaselineEccentricity = null) {
        ArgumentNullException.ThrowIfNull(metrics);
        var residual = metrics.RobustTotalRms;
        var tail = metrics.Percentile95TotalError * .7 + metrics.Percentile99TotalError * .3;
        var image = metrics.MainCameraEccentricity is { } e && mainImageBaselineEccentricity is { } b
            ? Math.Max(0, e - b) * 2 : 0;
        var warnings = new List<string>();
        if (metrics.CriticalRegression) warnings.Add("Critical regression detected.");
        if (metrics.PulseSaturationRate > .1) warnings.Add("Pulse saturation exceeds 10%.");
        if (metrics.StarLossRate > .02) warnings.Add("Guide-star loss exceeds 2%.");
        if (metrics.DeclinationReversalDelayMilliseconds is > 3000)
            warnings.Add("DEC reversal delay exceeds 3 seconds; prefer unidirectional guiding.");
        var backlash = Math.Clamp((metrics.DeclinationReversalDelayMilliseconds ?? 0) / 3000, 0, 1);
        return new GuidingScoreBreakdown(
            .35 * residual + .20 * tail + .15 * metrics.OscillationRate + .10 * metrics.PulseSaturationRate
            + .10 * metrics.StarLossRate + .05 * Math.Max(metrics.DecReversalRate, backlash) + .05 * image,
            residual, tail, metrics.OscillationRate, metrics.PulseSaturationRate, metrics.StarLossRate,
            metrics.DecReversalRate, image, warnings);
    }

    public static bool IsAutomaticWinner(GuidingCandidateResult baseline, GuidingCandidateResult candidate,
        double minimumImprovementPercent = 8, double confidence = 1) {
        if (baseline.Score.Total <= 0 || candidate.Score.Total >= baseline.Score.Total) return false;
        var improvement = (baseline.Score.Total - candidate.Score.Total) / baseline.Score.Total * 100;
        return improvement >= minimumImprovementPercent && confidence >= .8
            && !candidate.Metrics.CriticalRegression && candidate.Metrics.StarLossRate <= baseline.Metrics.StarLossRate + .02;
    }

    /// <summary>
    /// Estimate P(candidate score &lt; baseline score) with a deterministic moving-block bootstrap.
    /// Blocks preserve short-range guiding correlation; deterministic sampling makes replay tests stable.
    /// </summary>
    public static double EstimateImprovementConfidence(
        IReadOnlyList<GuidingTelemetrySample> baselineSamples,
        IReadOnlyList<GuidingTelemetrySample> candidateSamples,
        double guidePixelScaleArcsecPerPixel,
        int resamples = 200,
        int blockLength = 8) {
        var baseline = Usable(baselineSamples);
        var candidate = Usable(candidateSamples);
        if (baseline.Length < 8 || candidate.Length < 8 || resamples <= 0 || guidePixelScaleArcsecPerPixel <= 0)
            return 0;
        var randomState = (uint)BootstrapSeed;
        var better = 0;
        for (var iteration = 0; iteration < resamples; iteration++) {
            var baselineSample = ResampleBlocks(baseline, ref randomState, blockLength);
            var candidateSample = ResampleBlocks(candidate, ref randomState, blockLength);
            var baselineScore = CalculateScore(CalculateMetrics(baselineSample, guidePixelScaleArcsecPerPixel)).Total;
            var candidateScore = CalculateScore(CalculateMetrics(candidateSample, guidePixelScaleArcsecPerPixel)).Total;
            if (candidateScore < baselineScore) better++;
        }
        return better / (double)resamples;
    }

    private static GuidingTelemetrySample[] Usable(IReadOnlyList<GuidingTelemetrySample> samples) =>
        samples.Where(s => !s.IsDither && !s.IsSettling && !s.IsCalibration).ToArray();

    private static GuidingTelemetrySample[] ResampleBlocks(
        GuidingTelemetrySample[] source, ref uint randomState, int blockLength) {
        var length = source.Length;
        var result = new GuidingTelemetrySample[length];
        var block = Math.Clamp(blockLength, 1, length);
        for (var offset = 0; offset < length; offset += block) {
            var start = NextIndex(ref randomState, length);
            var count = Math.Min(block, length - offset);
            for (var index = 0; index < count; index++)
                result[offset + index] = source[(start + index) % length];
        }
        return result;
    }

    private static int NextIndex(ref uint state, int exclusiveUpperBound) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)(state % (uint)exclusiveUpperBound);
    }

    private static double RobustRms(double[] values) {
        if (values.Length == 0) return 0;
        var median = Percentile(values, .5);
        var deviations = values.Select(v => v - median).ToArray();
        var sigma = 1.4826 * Percentile(deviations.Select(Math.Abs).ToArray(), .5);
        var inliers = values.Where(v => Math.Abs(v - median) <= Math.Max(3 * sigma, double.Epsilon)).ToArray();
        return inliers.Length == 0 ? Math.Sqrt(values.Select(v => v * v).Average()) : Math.Sqrt(inliers.Select(v => v * v).Average());
    }

    private static double Percentile(double[] values, double percentile) {
        if (values.Length == 0) return 0;
        var ordered = values.OrderBy(v => v).ToArray();
        var position = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return ordered[low] + (ordered[high] - ordered[low]) * (position - low);
    }

    private static int PulseSign(string? direction, double durationMilliseconds) {
        if (direction is not null) {
            if (direction.Equals("East", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("South", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("North", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("West", StringComparison.OrdinalIgnoreCase))
                return direction.Equals("East", StringComparison.OrdinalIgnoreCase)
                    || direction.Equals("South", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        }
        return Math.Sign(durationMilliseconds);
    }
}
