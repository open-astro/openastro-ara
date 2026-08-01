#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Core.Guiding;

public static class GuidingPlanner {
    private static readonly string[] ImageScaleLock = { "Arcsecond image scale" };
    private static readonly string[] ImageScaleReason = { "Unknown guide image scale; diagnostics-only plan." };
    private static readonly string[] ExposureLock = { "Supported exposure durations" };
    private static readonly string[] ExposureReason = { "Guider returned no supported exposure durations." };
    private static readonly double[] DefaultGuideRate = { 0 };

    public static double GuideScaleArcsecPerPixel(double pixelSizeMicrons, int binning, double focalLengthMillimeters) {
        if (pixelSizeMicrons <= 0 || binning <= 0 || focalLengthMillimeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelSizeMicrons), "Optical values must be positive.");
        return 206.265 * pixelSizeMicrons * binning / focalLengthMillimeters;
    }

    public static double ExpectedPeriodicErrorPulseMilliseconds(
        double slopeArcsecPerSecond, double frameIntervalSeconds, double guideRateArcsecPerSecond) {
        if (slopeArcsecPerSecond < 0 || frameIntervalSeconds <= 0 || guideRateArcsecPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(slopeArcsecPerSecond), "Slope, frame interval, and guide rate must be valid.");
        return 1000 * slopeArcsecPerSecond * frameIntervalSeconds / guideRateArcsecPerSecond;
    }

    public static GuidingTunePlan CreatePlan(GuidingPlanningContext context) {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GuidePixelScaleArcsecPerPixel <= 0 && context.RequireKnownImageScale)
            return new GuidingTunePlan(context.CurrentSettings, Array.Empty<GuidingParameterSet>(),
                ImageScaleLock, ImageScaleReason, false, 0);
        if (context.SupportedExposureMilliseconds.Count == 0)
            return new GuidingTunePlan(context.CurrentSettings, Array.Empty<GuidingParameterSet>(),
                ExposureLock, ExposureReason, false, 0);

        var characterization = context.Characterization;
        var exposures = SupportedExposures(context).ToArray();
        if (exposures.Length == 0)
            return new GuidingTunePlan(context.CurrentSettings, Array.Empty<GuidingParameterSet>(),
                ExposureLock, ExposureReason, false, 0);
        var minRa = GuidingSignalAnalyzer.EstimateMinimumMovePixels(
            Array.Empty<GuidingTelemetrySample>(), GuideAxis.Ra);
        // The noise estimate is in arcseconds. Convert it to pixels before applying the 1.75 noise rule.
        minRa = NoiseToPixels(characterization.RightAscension.HighFrequencyNoise, context.GuidePixelScaleArcsecPerPixel);
        var minDec = NoiseToPixels(characterization.Declination.HighFrequencyNoise, context.GuidePixelScaleArcsecPerPixel);
        minRa = Math.Clamp(Math.Max(minRa * 1.75, .05), .05, 1.5);
        minDec = Math.Clamp(Math.Max(minDec * 1.75, .05), .05, 1.5);
        var slope = Math.Max(characterization.RightAscension.Slope95PerSecond,
            characterization.RightAscension.Slope99PerSecond * .75);
        var guideRateArcsecPerSecond = context.GuideRateArcsecPerSecond > 0
            ? context.GuideRateArcsecPerSecond : 7.52;
        var pulse = ExpectedPeriodicErrorPulseMilliseconds(slope, context.ActualFrameIntervalSeconds,
            guideRateArcsecPerSecond);
        var decSlope = Math.Max(characterization.Declination.Slope95PerSecond,
            characterization.Declination.Slope99PerSecond * .75);
        var decPulse = ExpectedPeriodicErrorPulseMilliseconds(decSlope, context.ActualFrameIntervalSeconds,
            guideRateArcsecPerSecond);
        var baseMaxRa = Math.Clamp(Math.Max(3 * pulse, context.CurrentSettings.RaMaximumPulseMilliseconds * .25), 40, 2000);
        var baseMaxDec = Math.Clamp(Math.Max(3 * decPulse, context.CurrentSettings.DecMaximumPulseMilliseconds * .25), 40, 2000);
        var maxCandidates = new[] { baseMaxRa, baseMaxRa * 1.5, baseMaxRa * 2 }
            .Select(v => Math.Clamp(v, 40, 2000)).Distinct().ToArray();
        var classBounds = AggressionBounds(characterization.BehaviorClass);
        var aggression = Math.Clamp(context.CurrentSettings.RaAggressiveness, classBounds.RaMin, classBounds.RaMax);
        var decAggression = Math.Clamp(context.CurrentSettings.DecAggressiveness, classBounds.DecMin, classBounds.DecMax);
        var reversalDelay = context.DeclinationReversalDelayMilliseconds ?? 0;
        var modes = reversalDelay > 3000
            ? new[] { "north", "south" }
            : characterization.Declination.SameSignPersistence > .9 && characterization.Declination.Slope95PerSecond < .05
            ? new[] { "off", context.CurrentSettings.DecGuideMode }
            : new[] { context.CurrentSettings.DecGuideMode };
        var guideRatePairs = context.AllowGuideRateChanges
            ? (context.GuideRateCandidatePairs ?? Array.Empty<GuidingGuideRateCandidate>()).Where(pair =>
                    double.IsFinite(pair.RightAscensionDegreesPerSecond)
                    && double.IsFinite(pair.DeclinationDegreesPerSecond)
                    && pair.RightAscensionDegreesPerSecond > 0
                    && pair.DeclinationDegreesPerSecond > 0)
                .Distinct().ToArray()
            : Array.Empty<GuidingGuideRateCandidate>();
        var guideRates = context.AllowGuideRateChanges && guideRatePairs.Length == 0
            ? context.GuideRateCandidatesDegreesPerSecond?.Where(rate => rate > 0).Distinct().ToArray() ?? DefaultGuideRate
            : DefaultGuideRate;

        var candidates = new List<GuidingParameterSet>();
        var selectedExposures = exposures.Take(context.Depth == GuidingTuneDepth.Quick ? 1 : 4).ToArray();
        var selectedRatePairs = guideRatePairs.Take(context.Depth == GuidingTuneDepth.Quick ? 1 : 3).ToArray();
        var selectedRates = guideRates.Take(context.Depth == GuidingTuneDepth.Quick ? 1 : 3).ToArray();
        var selectedMode = modes.FirstOrDefault() ?? context.CurrentSettings.DecGuideMode;
        GuidingParameterSet Build(int exposure, double maxRa, double? minRaValue = null,
            double? minDecValue = null, double? raAggressionValue = null,
            double? decAggressionValue = null, double? guideRate = null,
            GuidingGuideRateCandidate? guideRatePair = null,
            string? decMode = null) => context.CurrentSettings with {
                ExposureMilliseconds = exposure,
                RaMinimumMovePixels = minRaValue ?? minRa,
                DecMinimumMovePixels = minDecValue ?? minDec,
                RaAggressiveness = raAggressionValue ?? aggression,
                DecAggressiveness = decAggressionValue ?? decAggression,
                RaMaximumPulseMilliseconds = maxRa,
                DecMaximumPulseMilliseconds = baseMaxDec,
                DecGuideMode = decMode ?? selectedMode,
                RaAlgorithm = context.AllowAlgorithmChanges ? "Hysteresis" : context.CurrentSettings.RaAlgorithm,
                DecAlgorithm = context.AllowAlgorithmChanges ? "ResistSwitch" : context.CurrentSettings.DecAlgorithm,
                GuideRateRightAscensionDegreesPerSecond = guideRatePair?.RightAscensionDegreesPerSecond
                    ?? (guideRate is > 0 ? guideRate : context.CurrentSettings.GuideRateRightAscensionDegreesPerSecond),
                GuideRateDeclinationDegreesPerSecond = guideRatePair?.DeclinationDegreesPerSecond
                    ?? (guideRate is > 0 ? guideRate : context.CurrentSettings.GuideRateDeclinationDegreesPerSecond),
            };

        void Add(GuidingParameterSet candidate) {
            if (!candidates.Contains(candidate)) candidates.Add(candidate);
        }

        // Stage 1: establish one center candidate, then expose each cadence/rate
        // family without consuming the complete experiment budget on a grid.
        var centerPair = selectedRatePairs.FirstOrDefault();
        var hasCenterPair = selectedRatePairs.Length > 0;
        var centerRate = selectedRates.FirstOrDefault() > 0 ? (double?)selectedRates[0] : null;
        Add(Build(selectedExposures[0], baseMaxRa,
            guideRate: hasCenterPair ? null : centerRate,
            guideRatePair: hasCenterPair ? centerPair : null));
        foreach (var exposure in selectedExposures.Skip(1))
            Add(Build(exposure, baseMaxRa, guideRate: hasCenterPair ? null : centerRate,
                guideRatePair: hasCenterPair ? centerPair : null));
        foreach (var pair in selectedRatePairs.Skip(1))
            Add(Build(selectedExposures[0], baseMaxRa, guideRatePair: pair));
        foreach (var guideRate in selectedRates.Skip(1))
            Add(Build(selectedExposures[0], baseMaxRa, guideRate: guideRate));

        // Stage 2: one-axis-at-a-time coordinate candidates expose MinMo, aggression,
        // pulse-cap, and DEC-mode effects without a combinatorial experiment grid.
        var centerExposure = selectedExposures[0];
        foreach (var maxPulse in maxCandidates.Skip(1)) Add(Build(centerExposure, maxPulse,
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, minRaValue: Math.Clamp(minRa * .75, .05, 1.5),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, minRaValue: Math.Clamp(minRa * 1.25, .05, 1.5),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, raAggressionValue: Math.Clamp(aggression - .1, classBounds.RaMin, classBounds.RaMax),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, raAggressionValue: Math.Clamp(aggression + .1, classBounds.RaMin, classBounds.RaMax),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, minDecValue: Math.Clamp(minDec * .75, .05, 1.5),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        Add(Build(centerExposure, baseMaxRa, decAggressionValue: Math.Clamp(decAggression - .1, classBounds.DecMin, classBounds.DecMax),
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null));
        if (modes.Length > 1) Add(Build(centerExposure, baseMaxRa,
            guideRate: hasCenterPair ? null : centerRate, guideRatePair: hasCenterPair ? centerPair : null,
            decMode: modes[1]));
        var limit = context.Depth switch { GuidingTuneDepth.Quick => 3, GuidingTuneDepth.Standard => 8, _ => 12 };
        var selected = candidates.Distinct().Take(limit).ToArray();
        var reasons = new List<string> {
            $"Shortest reliable exposure candidates: {string.Join(", ", exposures)} ms.",
            $"RA slope-based pulse estimate: {pulse:F1} ms at {context.GuideRateArcsecPerSecond:F2} arcsec/s.",
            $"DEC slope-based pulse estimate: {decPulse:F1} ms at {context.GuideRateArcsecPerSecond:F2} arcsec/s.",
            $"RA/DEC minimum move derived from measured high-frequency noise: {minRa:F3}/{minDec:F3} px.",
            $"Observed behavior: {characterization.BehaviorClass} ({characterization.Confidence:P0}).",
        };
        if (reversalDelay > 3000)
            reasons.Add($"DEC reversal delay measured at {reversalDelay:F0} ms; unidirectional DEC candidates selected.");
        var locked = new List<string>();
        if (!context.AllowGuideRateChanges) locked.Add("guide rate");
        if (!context.AllowAlgorithmChanges) locked.Add("algorithm");
        var expectedPeriod = characterization.DominantPeriodsSeconds.Count == 0
            ? 0 : characterization.DominantPeriodsSeconds[0];
        var evaluation = Math.Max(180, expectedPeriod > 0 ? 1.25 * expectedPeriod : 180);
        return new GuidingTunePlan(context.CurrentSettings, selected, locked, reasons,
            characterization.Confidence >= .8 && context.StarQuality.MeetsMinimumQuality, evaluation);
    }

    private static IEnumerable<int> SupportedExposures(GuidingPlanningContext context) {
        var ordered = context.SupportedExposureMilliseconds.Where(x => x > 0).Distinct().OrderBy(x => x);
        var priors = context.Characterization.BehaviorClass switch {
            GuidingMountBehaviorClass.HarmonicLike => new[] { 250, 500, 750, 1000 },
            GuidingMountBehaviorClass.WormLike => new[] { 1000, 1500, 2000, 3000 },
            GuidingMountBehaviorClass.EncoderControlled or GuidingMountBehaviorClass.LowPeriodicError => new[] { 1000, 2000, 3000 },
            _ => new[] { 500, 1000, 2000 },
        };
        var chosen = ordered.Where(x => priors.Contains(x)).Take(context.Depth == GuidingTuneDepth.Quick ? 1 : 4).ToArray();
        return chosen.Length > 0 ? chosen : ordered.Take(context.Depth == GuidingTuneDepth.Quick ? 1 : 3);
    }

    private static double NoiseToPixels(double noiseArcsec, double scale) => scale > 0 ? noiseArcsec / scale : .15;

    private static (double RaMin, double RaMax, double DecMin, double DecMax) AggressionBounds(GuidingMountBehaviorClass behavior) => behavior switch {
        GuidingMountBehaviorClass.HarmonicLike => (.15, .60, .15, .50),
        GuidingMountBehaviorClass.WormLike => (.35, .85, .25, .70),
        GuidingMountBehaviorClass.EncoderControlled or GuidingMountBehaviorClass.LowPeriodicError => (.20, .65, .15, .50),
        _ => (.25, .70, .20, .60),
    };
}
