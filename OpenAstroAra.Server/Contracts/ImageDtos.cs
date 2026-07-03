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
// PORT_PLAYBOOK.md §10.8 + §40 (image library) + §65 (stretching) + §72 (FITS)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Frame type per FITS IMAGETYP. Mirrors NINA's FrameType enum.</summary>
public enum FrameType {
    Light,
    Dark,
    Flat,
    Bias,
    DarkFlat,
    Snapshot
}

/// <summary>Per-frame composite quality score breakdown (§50.10).</summary>
public sealed record QualityScoreBreakdownDto(
    double Composite,
    double HfrComponent,
    double StarCountComponent,
    double EccentricityComponent,
    double GuidingRmsComponent,
    double SnrComponent,
    string? Explanation);

/// <summary>Image frame — fully detailed (single GET).</summary>
public sealed record FrameDto(
    Guid Id,
    Guid SessionId,
    string TargetName,
    FrameType FrameType,
    string? FilterName,
    // §28 widening: REAL seconds (sub-second bias/darks) + nullable gain (a
    // camera that doesn't report gain records null, not a -1 sentinel).
    double ExposureSeconds,
    int? Gain,
    int? Offset,
    // Null = the camera reported no CCD temperature (no more 0.0 sentinel);
    // legacy rows may still hold an ambiguous 0.0 — see the §39 COALESCE note.
    double? TemperatureC,
    DateTimeOffset CapturedUtc,
    string FilePath,
    long FileSizeBytes,
    int Width,
    int Height,
    int BitDepth,
    double? Hfr,
    int? StarCount,
    double? Eccentricity,
    double? GuidingRmsArcsec,
    double? SnrEstimate,
    QualityScoreBreakdownDto? QualityScore,
    int Rating,
    IReadOnlyList<string> Tags,
    // §38: focuser step position at capture (for the §50.4 focus-vs-temperature
    // view). Optional + last so existing constructions stay source-compatible.
    int? FocuserPosition = null);

/// <summary>List item used by /api/v1/frames (paginated). Excludes the heavy quality breakdown.</summary>
public sealed record FrameListItemDto(
    Guid Id,
    Guid SessionId,
    string TargetName,
    FrameType FrameType,
    string? FilterName,
    double ExposureSeconds,
    DateTimeOffset CapturedUtc,
    double? Hfr,
    int? StarCount,
    double? CompositeQualityScore,
    int Rating);

/// <summary>POST /api/v1/frames/{id}/preview body. Stretch knobs per §65.</summary>
public sealed record FramePreviewRequestDto(
    string StretchPalette,
    double? BlackPoint,
    double? MidtonePoint,
    double? WhitePoint,
    int? MaxDimensionPx,
    bool ApplyDebayer);

/// <summary>Session — full detail.</summary>
public sealed record SessionDto(
    Guid Id,
    string Name,
    string TargetName,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset? SessionEndUtc,
    int TotalFrames,
    int LightFrames,
    int CalibrationFrames,
    IReadOnlyList<string> FiltersUsed,
    string? ProfileId,
    string? StretchPaletteUsed);

/// <summary>POST /api/v1/sessions/{id}/resume-target body per §40.</summary>
public sealed record ResumeTargetRequestDto(
    bool RecreateSequence,
    Guid? OverrideSequenceId);

/// <summary>POST /api/v1/sessions/{id}/resume-target result (§40.6): the runnable §38
/// sequence to continue the target with. <c>Origin</c> says where it came from —
/// "original-sequence" (the session's recorded sequence body, re-persisted),
/// "synthesized-from-catalog" (per-filter capture plan rebuilt from the session's
/// lights), or "override" (the caller's own sequence id, echoed back).</summary>
public sealed record ResumeTargetResultDto(
    Guid SessionId,
    Guid SequenceId,
    string SequenceName,
    string Origin);

/// <summary>POST /api/v1/sessions/{id}/restretch body per §65.</summary>
public sealed record SessionRestretchRequestDto(
    string StretchPalette,
    double? BlackPoint,
    double? MidtonePoint,
    double? WhitePoint,
    bool ApplyToAllFrames);

/// <summary>POST /api/v1/frames/bulk/rate body per §40.8.</summary>
public sealed record BulkRateRequestDto(
    IReadOnlyList<Guid> FrameIds,
    int Rating);

/// <summary>POST /api/v1/frames/bulk/tag body per §40.8.</summary>
public sealed record BulkTagRequestDto(
    IReadOnlyList<Guid> FrameIds,
    IReadOnlyList<string> AddTags,
    IReadOnlyList<string> RemoveTags);

/// <summary>POST /api/v1/frames/bulk/delete body per §40.8.</summary>
public sealed record BulkDeleteRequestDto(
    IReadOnlyList<Guid> FrameIds,
    bool DeleteFromDisk);

/// <summary>HFR drift analysis result per §40.7 / §51.</summary>
public sealed record HfrAnalysisDto(
    Guid SessionId,
    int FrameCount,
    double MeanHfr,
    double StandardDeviation,
    double TrendSlopePerHour,
    string Trend,
    IReadOnlyList<HfrTimeSeriesPointDto> TimeSeries);

public sealed record HfrTimeSeriesPointDto(
    DateTimeOffset Timestamp,
    double Hfr,
    int? StarCount,
    Guid FrameId);

/// <summary>Backup stream subscription handle per §44.</summary>
public sealed record BackupSubscriptionDto(
    Guid SubscriptionId,
    DateTimeOffset CreatedUtc,
    string AckTopic);

/// <summary>One frame surfaced through the backup stream (§44).</summary>
public sealed record BackupFrameDto(
    Guid FrameId,
    Guid SessionId,
    DateTimeOffset CapturedUtc,
    string FilePath,
    long FileSizeBytes,
    string Sha256);

/// <summary>POST /api/v1/backup/stream/claim body (§44). Claims a frame for an active subscription.</summary>
public sealed record BackupClaimRequestDto(
    Guid SubscriptionId,
    Guid FrameId);