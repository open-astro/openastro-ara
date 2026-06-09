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

/// <summary>POST /api/v1/mosaics body.</summary>
public sealed record MosaicCreateRequestDto(
    string Name,
    double CenterRaDegrees,
    double CenterDecDegrees,
    int PanelCountX,
    int PanelCountY,
    double OverlapPercent,
    double? PositionAngleDegrees,
    string? SequenceTemplateName);

/// <summary>One panel within a mosaic.</summary>
public sealed record MosaicPanelDto(
    Guid MosaicId,
    int PanelIndex,
    int PanelX,
    int PanelY,
    double CenterRaDegrees,
    double CenterDecDegrees,
    bool CrossesRaWrap,
    string Status,
    Guid? TargetFrameId);

/// <summary>Progress of a mosaic run.</summary>
public sealed record MosaicProgressDto(
    Guid MosaicId,
    int CompletedPanels,
    int TotalPanels,
    int? CurrentPanelIndex,
    DateTimeOffset? CurrentPanelStartedUtc,
    int FramesPerPanelTarget,
    int FramesCapturedTotal);