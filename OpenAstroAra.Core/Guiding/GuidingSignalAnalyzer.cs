#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Core.Guiding;

public static class GuidingSignalAnalyzer {
    private const int MinimumSamples = 8;

    public static MountCharacterization Analyze(
        MountFingerprint fingerprint,
        GuidingTelemetryWindow window,
        double? guidePixelScaleArcsecPerPixel) {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(window);

        var usable = window.Samples
            .Where(s => !s.IsDither && !s.IsSettling && !s.IsCalibration && !s.StarLost)
            .OrderBy(s => s.TimestampUtc)
            .ToArray();
        if (usable.Length < MinimumSamples) {
            var empty = EmptyMetrics(usable.Length);
            return new MountCharacterization(GuidingMountBehaviorClass.Unknown, 0, empty, empty,
                Array.Empty<double>(), new[] { $"Need at least {MinimumSamples} usable samples; got {usable.Length}." },
                guidePixelScaleArcsecPerPixel is > 0, guidePixelScaleArcsecPerPixel, fingerprint);
        }

        var times = usable.Select(s => (s.TimestampUtc - usable[0].TimestampUtc).TotalSeconds).ToArray();
        var ra = usable.Select(s => Scale(s.RaRawPixels, guidePixelScaleArcsecPerPixel)).ToArray();
        var dec = usable.Select(s => Scale(s.DecRawPixels, guidePixelScaleArcsecPerPixel)).ToArray();
        var raMetrics = AnalyzeAxis(times, ra);
        var decMetrics = AnalyzeAxis(times, dec);
        var periods = new[] { raMetrics.DominantPeriodSeconds, decMetrics.DominantPeriodSeconds }
            .Where(p => p is > 0).Select(p => p!.Value).Distinct().OrderBy(p => p).ToArray();
        var (behavior, confidence, reasons) = Classify(fingerprint, raMetrics, decMetrics, periods, window.DurationSeconds);
        return new MountCharacterization(behavior, confidence, raMetrics, decMetrics, periods, reasons,
            guidePixelScaleArcsecPerPixel is > 0, guidePixelScaleArcsecPerPixel, fingerprint);
    }

    public static GuideStarQualityMetrics AnalyzeStarQuality(
        IReadOnlyList<GuidingTelemetrySample> samples,
        double requestedExposureMilliseconds) {
        ArgumentNullException.ThrowIfNull(samples);
        var usable = samples.Where(s => !s.IsCalibration).ToArray();
        if (usable.Length == 0) {
            return new GuideStarQualityMetrics(0, 0, 0, 0, 1, 0, 0, 0, 0, false);
        }

        var snr = usable.Where(s => s.Snr is > 0).Select(s => s.Snr!.Value).ToArray();
        var intervals = usable.Zip(usable.Skip(1), (a, b) =>
                b.ActualFrameIntervalMilliseconds
                ?? (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds)
            .Where(v => v > 0 && double.IsFinite(v)).ToArray();
        var actual = intervals.Length == 0 || requestedExposureMilliseconds <= 0
            ? 0
            : intervals.Average() <= requestedExposureMilliseconds * 1.33 ? 1 : requestedExposureMilliseconds / intervals.Average();
        var starLossRate = usable.Count(s => s.StarLost || s.ErrorCodeIsStarLoss()) / (double)usable.Length;
        var multi = usable.Where(s => s.MultiStarCount is >= 0).Select(s => (double)s.MultiStarCount!.Value).ToArray();
        var rejected = usable.Where(s => s.RejectedStarCount is >= 0).Select(s => (double)s.RejectedStarCount!.Value).ToArray();
        var stable = usable.Count(s => !s.StarLost && (s.Snr is null || s.Snr > 0));
        var result = new GuideStarQualityMetrics(usable.Length, stable, Median(snr), Percentile(snr, .05), starLossRate,
            Math.Clamp(actual, 0, 1), Median(multi), Median(rejected), 0, false);
        var multiStarReliable = multi.Length == 0 || result.MedianMultiStarCount >= 4;
        return result with {
            MeetsMinimumQuality = stable >= 4 && result.StarLossRate < .02
                && result.ActualCadenceRatio >= .75 && multiStarReliable,
        };
    }

    public static int? SelectShortestReliableExposure(
        IReadOnlyList<GuidingExposureProbeResult> probes) {
        ArgumentNullException.ThrowIfNull(probes);
        return probes
            .Where(p => p.Supported && p.Quality.MeetsMinimumQuality)
            .OrderBy(p => p.ExposureMilliseconds)
            .Select(p => (int?)p.ExposureMilliseconds)
            .FirstOrDefault();
    }

    public static double EstimateMinimumMovePixels(IReadOnlyList<GuidingTelemetrySample> samples, GuideAxis axis) {
        var values = samples.Where(s => !s.IsDither && !s.IsSettling && !s.StarLost)
            .Select(s => axis == GuideAxis.Ra ? s.RaRawPixels : s.DecRawPixels).ToArray();
        if (values.Length < 3) return 0.15;
        var differences = values.Zip(values.Skip(1), (a, b) => b - a).ToArray();
        var noise = 1.4826 * Median(differences.Select(Math.Abs).ToArray()) / Math.Sqrt(2);
        return Math.Clamp(noise * 1.75, 0.05, 1.5);
    }

    private static AxisMotionMetrics AnalyzeAxis(double[] times, double[] values) {
        var drift = LinearSlope(times, values);
        var detrended = RemoveLinearTrend(times, values);
        var centered = detrended.Select(v => v - Median(detrended)).ToArray();
        var abs = centered.Select(Math.Abs).ToArray();
        var slopes = new List<double>();
        for (var i = 1; i < centered.Length; i++) {
            var dt = times[i] - times[i - 1];
            if (dt is > 0 and < 30) slopes.Add(Math.Abs((centered[i] - centered[i - 1]) / dt));
        }
        var differences = centered.Zip(centered.Skip(1), (a, b) => b - a).ToArray();
        var period = FindDominantPeriod(times, centered, out var power, out var entropy);
        var harmonic = period is > 0 ? HarmonicRatio(times, centered, period.Value, power) : 0;
        var segments = SplitCorrelation(centered);
        var signRuns = SameSignPersistence(centered);
        return new AxisMotionMetrics(
            RobustRms(centered), Median(abs), Percentile(abs, .95), Percentile(abs, .99),
            1.4826 * Median(differences.Select(Math.Abs).ToArray()) / Math.Sqrt(2),
            Percentile(slopes.ToArray(), .95), Percentile(slopes.ToArray(), .99), period, power, harmonic,
            entropy, segments, ZeroCrossingRate(centered, times), signRuns, values.Length, drift);
    }

    private static (GuidingMountBehaviorClass Behavior, double Confidence, IReadOnlyList<string> Reasons) Classify(
        MountFingerprint fingerprint, AxisMotionMetrics ra, AxisMotionMetrics dec, double[] periods,
        double durationSeconds) {
        var reasons = new List<string>();
        var harmonicEvidence = ra.HarmonicPowerRatio > .25 || ra.Slope99PerSecond > Math.Max(.25, ra.Slope95PerSecond * 1.7);
        var wormEvidence = ra.DominantPeriodSeconds is > 60 && ra.HarmonicPowerRatio < .25 && ra.StationarityCorrelation >= .6;
        var lowPe = ra.FundamentalPower < .01 && ra.Slope95PerSecond < .08;
        var nonStationary = ra.StationarityCorrelation < .35 || (periods.Length > 1 && durationSeconds < periods.Max() * 1.5);
        if (nonStationary) {
            reasons.Add("Periodic waveform is not stationary across analysis windows.");
            return (GuidingMountBehaviorClass.MixedOrNonStationary, .65, reasons);
        }
        if (fingerprint.EncoderPresent == true && lowPe) {
            reasons.Add("Encoder metadata agrees with low repeatable periodic motion.");
            return (GuidingMountBehaviorClass.EncoderControlled, .85, reasons);
        }
        if (lowPe) {
            reasons.Add("Repeatable RA periodic energy and slope are low.");
            return (GuidingMountBehaviorClass.LowPeriodicError, .76, reasons);
        }
        if (harmonicEvidence) {
            reasons.Add("High-order harmonic energy or a heavy derivative tail is present.");
            if (fingerprint.DeclaredDriveType.Contains("strain", StringComparison.OrdinalIgnoreCase)
                || fingerprint.DeclaredDriveType.Contains("harmonic", StringComparison.OrdinalIgnoreCase)) {
                reasons.Add("Mount metadata supplies a supporting harmonic-drive prior.");
            }
            return (GuidingMountBehaviorClass.HarmonicLike, .78, reasons);
        }
        if (wormEvidence) {
            reasons.Add("A smooth repeatable dominant period has relatively low harmonic energy.");
            return (GuidingMountBehaviorClass.WormLike, .78, reasons);
        }
        reasons.Add("Telemetry does not meet a behavior-class threshold.");
        return (GuidingMountBehaviorClass.Unknown, .4, reasons);
    }

    private static double[] RemoveLinearTrend(double[] times, double[] values) {
        var slope = LinearSlope(times, values);
        var mt = times.Average();
        var mv = values.Average();
        var intercept = mv - slope * mt;
        return times.Zip(values, (t, v) => v - (slope * t + intercept)).ToArray();
    }

    private static double LinearSlope(double[] times, double[] values) {
        var mt = times.Average();
        var mv = values.Average();
        var denom = times.Sum(t => (t - mt) * (t - mt));
        return denom <= double.Epsilon
            ? 0
            : times.Zip(values, (t, v) => (t - mt) * (v - mv)).Sum() / denom;
    }

    private static double? FindDominantPeriod(double[] times, double[] values, out double dominantPower, out double entropy) {
        dominantPower = 0;
        entropy = 1;
        var duration = times[^1] - times[0];
        if (duration < 20) return null;
        var minPeriod = 20d;
        var maxPeriod = Math.Min(7200, duration * .8);
        if (maxPeriod <= minPeriod) return null;
        var powers = new List<(double Period, double Power)>();
        for (var period = minPeriod; period <= maxPeriod; period *= 1.08) {
            var omega = 2 * Math.PI / period;
            var re = 0d;
            var im = 0d;
            for (var i = 0; i < values.Length; i++) {
                re += values[i] * Math.Cos(omega * times[i]);
                im += values[i] * Math.Sin(omega * times[i]);
            }
            powers.Add((period, re * re + im * im));
        }
        if (powers.Count == 0) return null;
        var total = powers.Sum(p => p.Power);
        var best = powers.OrderByDescending(p => p.Power).First();
        dominantPower = total <= double.Epsilon ? 0 : best.Power / total;
        if (total > double.Epsilon) {
            entropy = -powers.Sum(p => {
                var q = p.Power / total;
                return q <= double.Epsilon ? 0 : q * Math.Log(q);
            }) / Math.Log(powers.Count);
        }
        return dominantPower < .08 ? null : best.Period;
    }

    private static double HarmonicRatio(double[] times, double[] values, double period, double fundamentalPower) {
        if (fundamentalPower <= double.Epsilon) return 0;
        var harmonicPower = 0d;
        for (var harmonic = 2; harmonic <= 4; harmonic++) {
            var omega = 2 * Math.PI * harmonic / period;
            var re = 0d;
            var im = 0d;
            for (var i = 0; i < values.Length; i++) {
                re += values[i] * Math.Cos(omega * times[i]);
                im += values[i] * Math.Sin(omega * times[i]);
            }
            harmonicPower += re * re + im * im;
        }
        return harmonicPower / Math.Max(fundamentalPower, double.Epsilon);
    }

    private static double SplitCorrelation(double[] values) {
        if (values.Length < 8) return 0;
        var half = values.Length / 2;
        var a = values[..half];
        var b = values[^half..];
        var ma = a.Average();
        var mb = b.Average();
        var denom = Math.Sqrt(a.Sum(v => (v - ma) * (v - ma)) * b.Sum(v => (v - mb) * (v - mb)));
        return denom <= double.Epsilon ? 1 : Math.Clamp(a.Zip(b, (x, y) => (x - ma) * (y - mb)).Sum() / denom, -1, 1);
    }

    private static double ZeroCrossingRate(double[] values, double[] times) {
        var crossings = 0;
        for (var i = 1; i < values.Length; i++) if (Math.Sign(values[i]) != Math.Sign(values[i - 1])) crossings++;
        return crossings / Math.Max(times[^1] - times[0], 1);
    }

    private static double SameSignPersistence(double[] values) {
        if (values.Length < 2) return 0;
        var same = values.Zip(values.Skip(1), (a, b) => Math.Sign(a) == Math.Sign(b)).Count(x => x);
        return same / (double)(values.Length - 1);
    }

    private static AxisMotionMetrics EmptyMetrics(int count) => new(0, 0, 0, 0, 0, 0, 0, null, 0, 0, 1, 0, 0, 0, count);

    private static double Scale(double value, double? scale) => scale is > 0 ? value * scale.Value : value;
    private static double RobustRms(double[] values) => values.Length == 0 ? 0 : Math.Sqrt(values.Select(v => v * v).Average());
    private static double Median(double[] values) => Percentile(values, .5);
    private static double Percentile(double[] values, double percentile) {
        if (values.Length == 0) return 0;
        var ordered = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return ordered[low] + (ordered[high] - ordered[low]) * (position - low);
    }
}

internal static class GuidingTelemetrySampleExtensions {
    public static bool ErrorCodeIsStarLoss(this GuidingTelemetrySample sample) => sample.StarLost;
}
