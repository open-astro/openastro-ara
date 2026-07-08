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
/// <remarks><c>Flavor</c> picks the §48 capture style: "panel" (default — FlatPanelFlats sets,
/// run whenever a panel is lit) or "sky" (§48.4 — the sequence waits for twilight, slews to the
/// profile's sky-flat patch, and captures SkyFlats sets with drift re-probing). Additive-optional
/// so pre-§48.4 callers keep their shape.</remarks>
public sealed record MatchingFlatsRequestDto(
    int? OverrideFrameCount,
    int? OverrideTargetAdu,
    bool GenerateOnly,
    string Flavor = "panel");

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

/// <summary>POST /api/v1/calibration/dark-library/build body per §39.8 / §63. Exposures are
/// real seconds (§28); an empty gain list means "camera default gain" (one combination, no Gain
/// on the generated TakeExposure); an empty temperature list means "capture at ambient" (no
/// CoolCamera step). An empty exposure list is rejected — there would be nothing to capture.</summary>
public sealed record DarkLibraryBuildRequestDto(
    IReadOnlyList<double> ExposureSecondsList,
    IReadOnlyList<int> GainList,
    IReadOnlyList<double> TargetTemperatureCList,
    int FramesPerCombination,
    bool ReuseExistingFrames);

/// <summary>GET /api/v1/calibration/dark-library/status body. <c>GeneratedSequenceId</c> is the
/// runnable §38 sequence the last build request produced (null before any build this daemon
/// lifetime). Combination progress is coverage-based: a combination counts as completed when the
/// catalog holds at least FramesPerCombination darks matching it. ReuseExistingFrames governs
/// which frames count, symmetrically with capture: a reuse build counts every matching
/// catalogued dark; a non-reuse build asked for fresh frames, so only captures made at or after
/// the build request count.</summary>
public sealed record DarkLibraryStateDto(
    string Status,
    int TotalCombinations,
    int CompletedCombinations,
    DateTimeOffset? BuildStartedUtc,
    DateTimeOffset? BuildCompletedUtc,
    string? FailureReason,
    IReadOnlyList<DarkLibraryEntryDto> Entries,
    Guid? GeneratedSequenceId);

/// <summary>One entry in the dark library: a distinct (exposure, gain, whole-degree temperature)
/// group of catalogued DARK frames. <c>Id</c> is a stable hash of that key (same group ⇒ same id
/// across calls). <c>FilePath</c> is the newest frame's file as a representative;
/// <c>FileSizeBytes</c> is the group total. Null <c>Gain</c> = frames that recorded no gain.</summary>
public sealed record DarkLibraryEntryDto(
    Guid Id,
    double ExposureSeconds,
    int? Gain,
    double TemperatureC,
    int FrameCount,
    DateTimeOffset CapturedUtc,
    string FilePath,
    long FileSizeBytes);