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

/// <summary>One focus probe of the current/last autofocus run. <c>Phase</c> is <c>coarse</c> (the
/// software-binned search that centres the sweep), <c>fine</c> (a V-curve sample) or <c>smart</c>
/// (a §59.2 Smart Focus shot). <c>Kept</c> is false for a probe the sweep measured but dropped (too
/// few stars) — the chart still shows it, hollow, so the user sees where the sky gave nothing.</summary>
public sealed record AutofocusProbeDto(
    int Index,
    string Phase,
    int Position,
    double Hfr,
    int Stars,
    bool Kept);

/// <summary>A point of the fitted curve, sampled by the daemon so the client draws exactly the model
/// the sweep used (parabolic, hyperbolic or trendlines) without re-deriving it.</summary>
public sealed record AutofocusCurvePointDto(double Position, double Hfr);

/// <summary>The §59.8 curve fit of the last completed sweep attempt.</summary>
public sealed record AutofocusCurveFitDto(
    string Algorithm,
    double RSquared,
    double BestPosition,
    double PredictedHfr,
    bool WithinSampledRange,
    IReadOnlyList<AutofocusCurvePointDto> Curve);

/// <summary>
/// The daemon's current (or most recent) autofocus run as one snapshot — everything the Setup tab's
/// Smart Focus pane renders: the probes for the V-curve, the fit, the final measured focus, and whether a
/// rendered frame of the focused field is available at <c>GET /api/v1/autofocus/frame</c>.
/// <c>State</c>: <c>idle</c> (never ran since boot) | <c>running</c> | <c>complete</c> | <c>failed</c> |
/// <c>cancelled</c>. <c>Phase</c> while running: <c>smart</c> | <c>coarse</c> | <c>sweep</c> |
/// <c>fitting</c> | <c>moving</c> | <c>confirming</c>. <c>Trigger</c>: <c>manual</c> (the focuser's
/// autofocus endpoint) or <c>sequence</c> (a RunAutofocus instruction / trigger).
/// </summary>
public sealed record AutofocusRunDto(
    string State,
    string? Mode,
    string? Phase,
    string? Trigger,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    double? DurationSeconds,
    int? StartPosition,
    int? FinalPosition,
    double? FinalHfr,
    int? FinalStars,
    string? Filter,
    double? FocuserTemperatureC,
    int TotalSteps,
    int CompletedSteps,
    int SweepAttempt,
    IReadOnlyList<AutofocusProbeDto> Probes,
    AutofocusCurveFitDto? Fit,
    string? Reason,
    int? RestoredPosition,
    bool HasFrame,
    long FrameSeq,
    int? FramePosition,
    double? FrameHfr);
