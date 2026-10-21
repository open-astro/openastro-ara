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

/// <summary>One imaging session that may need calibration frames. Per §39.
/// <c>CoolerSetpointC</c> is the set-point most of the session's lights were taken at with the
/// cooler on (null = taken warm, or before the catalog recorded it) — what matching flats cool to.
/// <c>MatchingBiasAvailable</c>: every (gain, offset, binning) the lights used has bias frames.</summary>
public sealed record CalibrationSessionDto(
    Guid Id,
    string TargetName,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset SessionEndUtc,
    int LightFrameCount,
    IReadOnlyList<CalibrationFilterSummaryDto> FiltersUsed,
    bool MatchingFlatsAvailable,
    bool MatchingDarksAvailable,
    string? ProfileId,
    double? CoolerSetpointC = null,
    bool MatchingBiasAvailable = false);

/// <summary>Per-filter summary inside a calibration session. <c>MatchingFlatFrames</c> counts the
/// catalogued flats that calibrate this filter's lights — same filter, taken at the same rotator
/// angle (flats are reused across nights while the optical train is unchanged; 0 = this filter
/// needs flats), the newest of them at <c>MatchingFlatsNewestUtc</c>. <c>FlatsNote</c> says why
/// flats that exist don't count (another rotator angle), or that the match is assumed because an
/// angle wasn't recorded.</summary>
public sealed record CalibrationFilterSummaryDto(
    string FilterName,
    int LightFrameCount,
    double MeanExposureSeconds,
    int? RecommendedFlatFrames,
    int MatchingFlatFrames = 0,
    DateTimeOffset? MatchingFlatsNewestUtc = null,
    string? FlatsNote = null,
    // True when the covering flats were checked against the lights' rotator angle; false when the
    // match is assumed because one side has no recorded angle (a rig without a rotator).
    bool FlatsVerified = false);

/// <summary>POST /api/v1/calibration/sessions/{id}/matching-flats body.</summary>
/// <remarks><c>Flavor</c> picks the §48 capture style: "panel" (default — FlatPanelFlats sets,
/// run whenever a panel is lit) or "sky" (§48.4 — the sequence waits for twilight, slews to the
/// profile's sky-flat patch, and captures SkyFlats sets with drift re-probing). Additive-optional
/// so pre-§48.4 callers keep their shape.
/// <c>CoolCamera</c> null = cool to the lights' set-point when they were cooled; false = leave the
/// cooler alone; true = cool to <c>CoolToTemperatureC</c>, else the lights' set-point, else the
/// profile's cooler target. A <c>CoolToTemperatureC</c> with <c>CoolCamera</c> null also cools.
/// <c>ParkMountAfter</c> is the user's answer for THIS run (the flats dialog always sends it);
/// null — the unattended end-of-night path — follows the profile's post_flat_park_mount.
/// Filters whose lights already have matching flats are skipped unless
/// <c>IncludeCoveredFilters</c>; with every filter covered the plan is empty and nothing is stored.
/// <c>IncludeBias</c> adds bias sets after the flats (panel off, cover still closed) for each
/// (gain, offset, binning) of the lights that has no bias frames yet, <c>BiasFrameCount</c> each
/// (null = the default). <c>IncludeDarks</c> adds dark sets for each (exposure, gain, temperature)
/// of the lights with no darks yet, <c>DarkFrameCount</c> each. <c>PauseForLensCap</c> opens the
/// panel's cover and waits for the user to put the lens cap on before those bias and darks.
/// <c>ReshootAssumedCoverage</c> skips only filters whose flats were verified at the lights'
/// rotator angle, so a rig without a rotator still gets the flats it asked for each night (the
/// end-of-session auto path sets it).
/// <c>IncludeFlats</c> false shoots no flats at all — a bias-and-darks-only run.</remarks>
public sealed record MatchingFlatsRequestDto(
    int? OverrideFrameCount,
    int? OverrideTargetAdu,
    bool GenerateOnly,
    string Flavor = "panel",
    bool? CoolCamera = null,
    double? CoolToTemperatureC = null,
    bool? ParkMountAfter = null,
    bool IncludeCoveredFilters = false,
    bool IncludeBias = false,
    int? BiasFrameCount = null,
    bool IncludeDarks = false,
    int? DarkFrameCount = null,
    bool PauseForLensCap = false,
    bool IncludeFlats = true,
    bool ReshootAssumedCoverage = false);

/// <summary>Sequence the server generated to capture matching flats for the session.
/// <c>GeneratedSequenceId</c> is the persisted §38 sequence (runnable via
/// <c>POST /api/v1/sequences/{id}/start</c>); null when the caller asked for the plan
/// only (<c>GenerateOnly</c>). <c>TotalBiasFrames</c> / <c>TotalDarkFrames</c> count the bias and
/// dark frames planned after the flats.</summary>
public sealed record GeneratedFlatSequenceDto(
    Guid SourceSessionId,
    Guid? GeneratedSequenceId,
    string GeneratedSequenceName,
    int TotalFlatFrames,
    IReadOnlyList<GeneratedFlatStepDto> Steps,
    int TotalBiasFrames = 0,
    int TotalDarkFrames = 0);

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
/// the build request count. <c>BiasEntries</c> lists the catalogued bias groups.</summary>
public sealed record DarkLibraryStateDto(
    string Status,
    int TotalCombinations,
    int CompletedCombinations,
    DateTimeOffset? BuildStartedUtc,
    DateTimeOffset? BuildCompletedUtc,
    string? FailureReason,
    IReadOnlyList<DarkLibraryEntryDto> Entries,
    Guid? GeneratedSequenceId,
    IReadOnlyList<BiasLibraryEntryDto>? BiasEntries = null);

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

/// <summary>One group of catalogued BIAS frames: a distinct (gain, offset, binning) — the key bias
/// frames match lights by. <c>Id</c> is a stable hash of that key; <c>FileSizeBytes</c> is the
/// group total. Null <c>Gain</c>/<c>Offset</c> = frames that recorded none.</summary>
public sealed record BiasLibraryEntryDto(
    Guid Id,
    int? Gain,
    int? Offset,
    int Bin,
    int FrameCount,
    DateTimeOffset CapturedUtc,
    long FileSizeBytes);
