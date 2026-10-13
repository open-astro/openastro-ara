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
// PORT_PLAYBOOK.md §10.7 + §47 (mosaic planning, includes §47.3 RA-wrap)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Mosaic plan. Per §47.</summary>
public sealed record MosaicDto(
    Guid Id,
    string Name,
    double CenterRaDegrees,
    double CenterDecDegrees,
    int PanelCountX,
    int PanelCountY,
    double OverlapPercent,
    double PositionAngleDegrees,
    int TotalPanels,
    DateTimeOffset CreatedUtc,
    Guid? GeneratedSequenceId);

/// <summary>POST /api/v1/mosaics body. #1146: <paramref name="Panels"/> are the per-panel J2000
/// centres the planetarium drew (row-major, <c>PanelCountX × PanelCountY</c> of them); when absent
/// the daemon spreads a grid from the profile optics. <paramref name="FramesPerPanel"/> is how many
/// light frames a panel owes before it counts as done (the run's planned frame count; default 20).</summary>
public sealed record MosaicCreateRequestDto(
    string Name,
    double CenterRaDegrees,
    double CenterDecDegrees,
    int PanelCountX,
    int PanelCountY,
    double OverlapPercent,
    double? PositionAngleDegrees,
    string? SequenceTemplateName,
    IReadOnlyList<MosaicPanelCenterDto>? Panels = null,
    int? FramesPerPanel = null);

/// <summary>One panel centre supplied by the client (J2000 degrees).</summary>
public sealed record MosaicPanelCenterDto(double RaDegrees, double DecDegrees);

/// <summary>One panel within a mosaic. <c>Status</c> is derived from the frame catalog (#1146):
/// <c>pending</c> (no light frames yet), <c>in_progress</c> (some, fewer than owed), <c>done</c>
/// (at least the owed count). <c>TargetFrameId</c> is the panel's newest light frame, if any;
/// <c>FramesCaptured</c> its light-frame count.</summary>
public sealed record MosaicPanelDto(
    Guid MosaicId,
    int PanelIndex,
    int PanelX,
    int PanelY,
    double CenterRaDegrees,
    double CenterDecDegrees,
    bool CrossesRaWrap,
    string Status,
    Guid? TargetFrameId,
    int FramesCaptured = 0);

/// <summary>Progress of a mosaic run.</summary>
public sealed record MosaicProgressDto(
    Guid MosaicId,
    int CompletedPanels,
    int TotalPanels,
    int? CurrentPanelIndex,
    DateTimeOffset? CurrentPanelStartedUtc,
    int FramesPerPanelTarget,
    int FramesCapturedTotal);