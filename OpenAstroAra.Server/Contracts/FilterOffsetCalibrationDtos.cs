#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>#1675 — <c>POST /equipment/filterwheel/offsets/calibrate</c>. <c>Positions</c> are the picked
/// slots; null picks every named slot except a dark. The reference is always measured.</summary>
public sealed record FilterOffsetCalibrationRequestDto(IReadOnlyList<int>? Positions = null);

/// <summary>One filter of a calibration. <c>Status</c> is <c>pending</c> (not reached yet, only while
/// running) | <c>measured</c> | <c>failed</c> (<c>FailureReason</c> says why) | <c>skipped</c> (the run
/// ended first). <c>Offset</c> is steps from the reference baseline; null until a baseline exists.</summary>
public sealed record FilterOffsetResultDto(
    int Position,
    string Name,
    string Status,
    int? BestPosition,
    int? Offset,
    double? RSquared,
    int PreviousOffset,
    string? FailureReason);

/// <summary>#1675 — <c>GET /equipment/filterwheel/offsets/calibration</c>. <c>State</c> is <c>running</c> |
/// <c>complete</c> | <c>failed</c> (<c>Reason</c> says why) | <c>cancelled</c>. <c>RunIndex</c> of
/// <c>RunCount</c> is the autofocus in progress (reference, each filter, reference again) on
/// <c>CurrentPosition</c>. <c>DriftChecked</c> is false when the second reference run failed or the CFZ is
/// unknown; <c>DriftWarning</c> is true when the two reference positions differ by more than
/// <c>CfzSteps</c>.</summary>
public sealed record FilterOffsetCalibrationDto(
    string State,
    Guid JobId,
    int ReferencePosition,
    string ReferenceName,
    int? ReferenceFirstPosition,
    int? ReferenceSecondPosition,
    int? CfzSteps,
    bool DriftChecked,
    bool DriftWarning,
    int? CurrentPosition,
    int RunIndex,
    int RunCount,
    string? Reason,
    IReadOnlyList<FilterOffsetResultDto> Filters);
