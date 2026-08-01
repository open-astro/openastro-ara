#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using OpenAstroAra.Core.Guiding;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

public sealed record GuidingAutoTuneStartRequestDto(
    string Depth = "standard",
    double? GuidePixelScaleArcsecPerPixel = null,
    double? MainPixelScaleArcsecPerPixel = null,
    double GuideRateArcsecPerSecond = 7.5205,
    bool AllowGuideRateChanges = false,
    bool AllowAlgorithmChanges = false,
    bool ApplyAutomatically = false,
    bool DryRun = true,
    int MaximumSamples = 4000,
    int MaximumCharacterizationSeconds = 0,
    int EvaluationSeconds = 180,
    int StabilizationSeconds = 15,
    bool UseMainCameraValidation = false,
    double MainCameraValidationExposureSeconds = 1,
    int MainCameraValidationBinning = 1,
    int MainCameraValidationFrames = 3,
    int MaximumCandidates = 8,
    int MaximumSessionMinutes = 30,
    double MinimumAltitudeDegrees = 45);

public sealed record GuidingAutoTuneCapabilitiesDto(
    bool Enabled,
    bool Connected,
    bool HasTelemetry,
    bool CanAnalyze,
    bool CanApply,
    bool GuideRateChangesSupported,
    IReadOnlyList<string> LockedReasons);

public sealed record GuidingAutoTuneStatusDto(
    Guid SessionId,
    string State,
    double Progress,
    string CurrentStep,
    string? BehaviorClass,
    double? BehaviorConfidence,
    int TelemetrySamples,
    double? BaselineScore,
    double? BestScore,
    bool CanApply,
    bool CanRollback,
    IReadOnlyList<string> Warnings,
    GuidingTunePlan? Plan,
    GuidingCandidateResult? BestCandidate,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    GuidingCandidateResult? BaselineResult = null,
    IReadOnlyList<GuidingCandidateResult>? CandidateResults = null);

public sealed record GuidingAutoTuneReportDto(
    Guid SessionId,
    string ContentType,
    string Markdown,
    DateTimeOffset GeneratedAtUtc);
