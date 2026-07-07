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

/// <summary>POST /api/v1/frames/bulk/move body per §40.8 — reassign frames to
/// another session (fixing e.g. recovered orphans filed under a recovery
/// session, or §14e manual snapshots that belong with a real run).</summary>
public sealed record BulkMoveRequestDto(
    IReadOnlyList<Guid> FrameIds,
    Guid TargetSessionId);

/// <summary>POST /api/v1/frames/bulk/export body per §39.10/§40.8 — download a
/// tarball of the selected frames' FITS files for external post-processing.</summary>
public sealed record BulkExportRequestDto(
    IReadOnlyList<Guid> FrameIds);

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
public sealed record BackupStreamStatusDto(
    bool Enabled,
    string? ActiveTarget,
    int PendingCount,
    int SyncedCount,
    long QueueSizeBytes);

/// <summary>POST /backup-stream/claim body — the desktop WILMA identifies itself by hostname (§44.3 single-target).</summary>
public sealed record BackupStreamClaimRequestDto(string Hostname);

/// <summary>Claim outcome — the stored active-target hostname (original casing preserved
/// on idempotent re-claims). No slot token: per the §67 trusted-LAN posture the surface is
/// hostname-identified like the rest of the API, and a capability-looking Guid that nothing
/// enforced would be worse than none (#734 review). A 409 (another target active) carries
/// the holder's hostname in the problem detail instead.</summary>
public sealed record BackupStreamClaimResultDto(
    string ActiveTarget);

/// <summary>One pending frame in the §44.5 queue (oldest first). Sha256 is computed lazily and cached on first serve.</summary>
public sealed record BackupStreamQueueEntryDto(
    Guid Id,
    string? Sha256,
    long SizeBytes,
    DateTimeOffset CapturedAt,
    Guid SessionId);

/// <summary>POST /backup-stream/ack body — §44.5: WILMA confirms it stored + sha-verified the frame.</summary>
public sealed record BackupStreamAckRequestDto(
    Guid FrameId,
    bool Sha256Verified);