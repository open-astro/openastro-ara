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
// PORT_PLAYBOOK.md §10.7 + §39 (calibration + matching flats + dark library)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>One imaging session that may need calibration frames. Per §39.</summary>
public sealed record CalibrationSessionDto(
    Guid Id,
    string TargetName,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset SessionEndUtc,
    int LightFrameCount,
    IReadOnlyList<CalibrationFilterSummaryDto> FiltersUsed,
    bool MatchingFlatsAvailable,
    bool MatchingDarksAvailable,
    string? ProfileId);

/// <summary>Per-filter summary inside a calibration session.</summary>
public sealed record CalibrationFilterSummaryDto(
    string FilterName,
    int LightFrameCount,
    double MeanExposureSeconds,
    int? RecommendedFlatFrames);

/// <summary>POST /api/v1/calibration/sessions/{id}/matching-flats body.</summary>
public sealed record MatchingFlatsRequestDto(
    int? OverrideFrameCount,
    int? OverrideTargetAdu,
    bool GenerateOnly);

/// <summary>Sequence the server generated to capture matching flats for the session.
/// <c>GeneratedSequenceId</c> is the persisted §38 sequence (runnable via
/// <c>POST /api/v1/sequences/{id}/start</c>); null when the caller asked for the plan
/// only (<c>GenerateOnly</c>).</summary>
public sealed record GeneratedFlatSequenceDto(
    Guid SourceSessionId,
    Guid? GeneratedSequenceId,
    string GeneratedSequenceName,
    int TotalFlatFrames,
    IReadOnlyList<GeneratedFlatStepDto> Steps);

public sealed record GeneratedFlatStepDto(
    string FilterName,
    int FrameCount,
    int? TargetAdu,
    int? PanelBrightness);

/// <summary>POST /api/v1/calibration/dark-library/build body per §39 / §63.</summary>
public sealed record DarkLibraryBuildRequestDto(
    IReadOnlyList<int> ExposureSecondsList,
    IReadOnlyList<int> GainList,
    IReadOnlyList<double> TargetTemperatureCList,
    int FramesPerCombination,
    bool ReuseExistingFrames);

/// <summary>GET /api/v1/calibration/dark-library/status body.</summary>
public sealed record DarkLibraryStateDto(
    string Status,
    int TotalCombinations,
    int CompletedCombinations,
    DateTimeOffset? BuildStartedUtc,
    DateTimeOffset? BuildCompletedUtc,
    string? FailureReason,
    IReadOnlyList<DarkLibraryEntryDto> Entries);

/// <summary>One entry in the dark library.</summary>
public sealed record DarkLibraryEntryDto(
    Guid Id,
    double ExposureSeconds,
    int Gain,
    double TemperatureC,
    int FrameCount,
    DateTimeOffset CapturedUtc,
    string FilePath,
    long FileSizeBytes);