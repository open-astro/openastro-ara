#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Core.Guiding;

public enum GuidingMountBehaviorClass {
    HarmonicLike,
    WormLike,
    EncoderControlled,
    LowPeriodicError,
    MixedOrNonStationary,
    Unknown,
}

public enum GuidingTuneDepth {
    Quick,
    Standard,
    Deep,
}

public enum GuidingAutoTuneState {
    Idle,
    Preflight,
    Snapshotting,
    Calibrating,
    MeasuringBaseline,
    CharacterizingUnguided,
    AnalyzingMount,
    PlanningCandidates,
    ApplyingCandidate,
    SettlingCandidate,
    EvaluatingCandidate,
    RestoringBaseline,
    ValidatingWinner,
    Proposed,
    ApplyingWinner,
    Completed,
    Cancelling,
    RolledBack,
    Failed,
}

public enum GuideAxis {
    Ra,
    Dec,
}

/// <summary>One accepted guide frame. All values state units in their names.</summary>
public sealed record GuidingTelemetrySample(
    long Sequence,
    DateTimeOffset TimestampUtc,
    double RaRawPixels,
    double DecRawPixels,
    double RaGuidePixels,
    double DecGuidePixels,
    double RaPulseMilliseconds,
    double DecPulseMilliseconds,
    string? RaPulseDirection,
    string? DecPulseDirection,
    double? ExposureMilliseconds,
    double? ActualFrameIntervalMilliseconds,
    double? Snr,
    double? HfdPixels,
    double? StarMass,
    int? MultiStarCount,
    int? RejectedStarCount,
    bool RaPulseLimited,
    bool DecPulseLimited,
    bool StarLost,
    bool IsDither,
    bool IsSettling,
    bool IsCalibration,
    bool GuideOutputEnabled,
    double? GuidePixelScaleArcsecPerPixel = null,
    long MonotonicTimestampTicks = 0,
    double? LockPositionXPixels = null,
    double? LockPositionYPixels = null,
    double? MountRightAscensionHours = null,
    double? MountDeclinationDegrees = null,
    double? MountAzimuthDegrees = null,
    string? ParameterSnapshotHash = null);

public sealed record GuidingTelemetryWindow(
    IReadOnlyList<GuidingTelemetrySample> Samples,
    string Source,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc) {

    public double DurationSeconds => Math.Max(0, (EndedAtUtc - StartedAtUtc).TotalSeconds);
}

public sealed record AxisMotionMetrics(
    double RobustRms,
    double MedianAbsoluteError,
    double Percentile95AbsoluteError,
    double Percentile99AbsoluteError,
    double HighFrequencyNoise,
    double Slope95PerSecond,
    double Slope99PerSecond,
    double? DominantPeriodSeconds,
    double FundamentalPower,
    double HarmonicPowerRatio,
    double SpectralEntropy,
    double StationarityCorrelation,
    double ZeroCrossingRate,
    double SameSignPersistence,
    int SampleCount,
    double DriftArcsecPerSecond = 0);

public sealed record MountFingerprint(
    string Manufacturer,
    string Model,
    string DriverName,
    string DriverVersion,
    string DeclaredDriveType,
    bool? EncoderPresent,
    IReadOnlyList<double> ExpectedPeriodicPeriodsSeconds,
    double? RightAscensionGuideRateArcsecPerSecond,
    double? DeclinationGuideRateArcsecPerSecond,
    double? AltitudeDegrees,
    double? DeclinationDegrees,
    string? SideOfPier,
    string? Description = null,
    string? DriverInfo = null,
    string? InterfaceVersion = null,
    IReadOnlyList<string>? SupportedActions = null,
    double? AzimuthDegrees = null);

public sealed record MountCharacterization(
    GuidingMountBehaviorClass BehaviorClass,
    double Confidence,
    AxisMotionMetrics RightAscension,
    AxisMotionMetrics Declination,
    IReadOnlyList<double> DominantPeriodsSeconds,
    IReadOnlyList<string> Reasons,
    bool ImageScaleKnown,
    double? GuidePixelScaleArcsecPerPixel,
    MountFingerprint Fingerprint);

public sealed record GuideStarQualityMetrics(
    int SampleCount,
    int StableStarFrames,
    double MedianSnr,
    double SnrFifthPercentile,
    double StarLossRate,
    double ActualCadenceRatio,
    double MedianMultiStarCount,
    double MedianRejectedStarCount,
    double SaturatedStarRate,
    bool MeetsMinimumQuality);

public sealed record GuidingExposureProbeResult(
    int ExposureMilliseconds,
    GuideStarQualityMetrics Quality,
    double MedianFrameIntervalMilliseconds,
    bool Supported,
    string? RejectionReason = null);

public sealed record GuidingParameterSet(
    int ExposureMilliseconds,
    double RaMinimumMovePixels,
    double DecMinimumMovePixels,
    double RaAggressiveness,
    double DecAggressiveness,
    double RaMaximumPulseMilliseconds,
    double DecMaximumPulseMilliseconds,
    string RaAlgorithm,
    string DecAlgorithm,
    string DecGuideMode,
    double? Hysteresis = null,
    IReadOnlyDictionary<string, double>? AdditionalParameters = null,
    double? GuideRateRightAscensionDegreesPerSecond = null,
    double? GuideRateDeclinationDegreesPerSecond = null);

public sealed record GuidingGuideRateCandidate(
    double RightAscensionDegreesPerSecond,
    double DeclinationDegreesPerSecond);

public sealed record GuidingPlanningContext(
    MountCharacterization Characterization,
    IReadOnlyList<int> SupportedExposureMilliseconds,
    GuideStarQualityMetrics StarQuality,
    double GuidePixelScaleArcsecPerPixel,
    double? MainPixelScaleArcsecPerPixel,
    double ActualFrameIntervalSeconds,
    double GuideRateArcsecPerSecond,
    GuidingTuneDepth Depth,
    GuidingParameterSet CurrentSettings,
    bool AllowGuideRateChanges,
    bool AllowAlgorithmChanges,
    bool RequireKnownImageScale = true,
    IReadOnlyList<double>? GuideRateCandidatesDegreesPerSecond = null,
    double? DeclinationReversalDelayMilliseconds = null,
    IReadOnlyList<GuidingGuideRateCandidate>? GuideRateCandidatePairs = null);

public sealed record GuidingTunePlan(
    GuidingParameterSet Baseline,
    IReadOnlyList<GuidingParameterSet> Candidates,
    IReadOnlyList<string> LockedParameters,
    IReadOnlyList<string> Reasons,
    bool CanAutoApply,
    double ExpectedEvaluationSeconds);

public sealed record GuidingRunMetrics(
    int SampleCount,
    double RobustTotalRms,
    double RobustRaRms,
    double RobustDecRms,
    double Percentile95TotalError,
    double Percentile99TotalError,
    double OscillationRate,
    double PulseSaturationRate,
    double SameSignCappedResidualRate,
    double StarLossRate,
    double DecReversalRate,
    double SettleSeconds,
    double? MainCameraEccentricity,
    bool CriticalRegression,
    double? DeclinationReversalDelayMilliseconds = null,
    double? ObservedResponseLatencyMilliseconds = null,
    double? EffectiveCorrectionAuthorityArcsecPerSecond = null);

public sealed record GuidingScoreBreakdown(
    double Total,
    double ResidualError,
    double TailError,
    double Oscillation,
    double Saturation,
    double StarReliability,
    double Declination,
    double MainImage,
    IReadOnlyList<string> Warnings);

public sealed record GuidingCandidateResult(
    GuidingParameterSet Settings,
    GuidingRunMetrics Metrics,
    GuidingScoreBreakdown Score,
    bool Accepted,
    string RejectionReason,
    double ImprovementConfidence = 0);

public sealed record GuidingSettingsSnapshot(
    GuidingParameterSet Settings,
    bool GuideOutputEnabled,
    bool TrackingEnabled,
    string TrackingRate,
    string ProfileId,
    DateTimeOffset CapturedAtUtc,
    string SnapshotHash,
    string RightAscensionAlgorithm = "Hysteresis",
    string DeclinationAlgorithm = "ResistSwitch",
    IReadOnlyDictionary<string, double>? AlgorithmParameters = null,
    string CalibrationJson = "null",
    bool GuidingActive = false,
    double? MountGuideRateRightAscensionDegreesPerSecond = null,
    double? MountGuideRateDeclinationDegreesPerSecond = null,
    GuidingCalibrationQuality? CalibrationQuality = null);

public sealed record GuidingCalibrationQuality(
    bool IsValid,
    double? RightAscensionRatePixelsPerSecond,
    double? DeclinationRatePixelsPerSecond,
    double? OrthogonalityErrorDegrees,
    double? CalibrationDeclinationDegrees,
    string? RightAscensionParity,
    string? DeclinationParity,
    IReadOnlyList<string> Reasons);

public sealed record GuidingAutoTuneSession(
    Guid Id,
    GuidingAutoTuneState State,
    double Progress,
    string CurrentStep,
    GuidingMountBehaviorClass? BehaviorClass,
    double? BehaviorConfidence,
    GuidingTunePlan? Plan,
    GuidingCandidateResult? BestCandidate,
    GuidingSettingsSnapshot? Snapshot,
    IReadOnlyList<string> Warnings,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    GuidingCandidateResult? BaselineResult = null,
    IReadOnlyList<GuidingCandidateResult>? CandidateResults = null,
    GuidingTelemetryWindow? CharacterizationTelemetry = null,
    MountCharacterization? Characterization = null,
    IReadOnlyList<GuidingExposureProbeResult>? ExposureProbes = null);
