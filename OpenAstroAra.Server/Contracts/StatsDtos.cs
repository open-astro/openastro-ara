#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// PORT_PLAYBOOK.md §10.9 + §50 (Stats)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>GET /api/v1/stats/overview — landing dashboard summary.</summary>
public sealed record StatsOverviewDto(
    int TotalSessions,
    int TotalFrames,
    int TotalLightFrames,
    double TotalIntegrationHours,
    int UniqueTargetsImaged,
    DateTimeOffset? FirstImageUtc,
    DateTimeOffset? LastImageUtc,
    double? LastSessionScore,
    StatsSparklineDto<DateTimeOffset, double>? Last30DaysIntegrationHours);

/// <summary>Generic sparkline series.</summary>
public sealed record StatsSparklineDto<TX, TY>(
    string Label,
    IReadOnlyList<TX> X,
    IReadOnlyList<TY> Y);

/// <summary>GET /api/v1/stats/targets per §50.</summary>
public sealed record StatsTargetsDto(
    IReadOnlyList<StatsTargetSummaryDto> Targets);

public sealed record StatsTargetSummaryDto(
    string TargetName,
    int FrameCount,
    double IntegrationHours,
    double? CompositeQualityScore,
    DateTimeOffset LastImagedUtc);

/// <summary>GET /api/v1/stats/focus-temp — focuser-temp correlation chart (§50).</summary>
public sealed record StatsFocusTempDto(
    IReadOnlyList<FocusTempPointDto> Samples,
    double? CorrelationR2);

public sealed record FocusTempPointDto(
    double TemperatureC,
    int FocuserPosition,
    DateTimeOffset Timestamp);

/// <summary>GET /api/v1/stats/guiding — RMS over time (§50).</summary>
public sealed record StatsGuidingDto(
    IReadOnlyList<GuidingRmsPointDto> Samples,
    double? MeanRmsArcsec,
    double? P95RmsArcsec);

public sealed record GuidingRmsPointDto(
    DateTimeOffset Timestamp,
    double RmsArcsec,
    double? RaRms,
    double? DecRms);

/// <summary>GET /api/v1/stats/frame-quality — quality score distribution.</summary>
public sealed record StatsFrameQualityDto(
    IReadOnlyList<FrameQualityBucketDto> Distribution,
    double MeanScore,
    double? StdDev);

public sealed record FrameQualityBucketDto(
    double RangeLow,
    double RangeHigh,
    int Count);

/// <summary>GET /api/v1/stats/best-frames — top-rated frames across the library.</summary>
public sealed record StatsBestFramesDto(
    IReadOnlyList<BestFrameDto> Frames);

public sealed record BestFrameDto(
    Guid FrameId,
    string TargetName,
    DateTimeOffset CapturedUtc,
    double CompositeScore,
    string? FilterName);

/// <summary>GET /api/v1/stats/calendar — per-day session calendar heatmap.</summary>
public sealed record StatsCalendarDto(
    IReadOnlyList<StatsCalendarDayDto> Days);

public sealed record StatsCalendarDayDto(
    DateOnly Date,
    int FrameCount,
    double IntegrationHours,
    IReadOnlyList<string> TargetsImaged);

/// <summary>GET /api/v1/stats/achievements (§50.19) — light-gamification headline
/// records + milestone badges, aggregated from the existing light-frame catalog
/// (no extra instrumentation). "Nights" are distinct local-capture calendar days.</summary>
public sealed record StatsAchievementsDto(
    int TotalNightsImaged,
    int LongestStreakNights,
    int CurrentStreakNights,
    double LongestNightHours,
    double TotalIntegrationHours,
    int UniqueTargetsImaged,
    int TotalLightFrames,
    DateTimeOffset? FirstLightUtc,
    IReadOnlyList<StatsMilestoneDto> Milestones);

/// <summary>One milestone badge: a fixed threshold against a cumulative metric.
/// <see cref="Current"/> is the live value, <see cref="Achieved"/> = it met
/// <see cref="Threshold"/>.</summary>
public sealed record StatsMilestoneDto(
    string Id,
    string Title,
    string Description,
    bool Achieved,
    double Threshold,
    double Current);