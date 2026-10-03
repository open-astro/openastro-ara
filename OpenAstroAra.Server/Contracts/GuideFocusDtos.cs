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

/// <summary>Start the guide-camera focus loop. <c>ExposureSec</c> 0.05–30 s (guide cameras are fast; a
/// focus loop wants ~1–3 s). <c>Binning</c> null = the guider's own.</summary>
public sealed record GuideFocusStartRequestDto(double ExposureSec = 2.0, int? Binning = null);

/// <summary>One measured guide frame. <c>Hfr</c> is 0 when the frame had no measurable star (see
/// <c>Stars</c>); <c>PeakAdu</c> is the brightest star's peak so the user sees saturation coming as the
/// focus tightens; <c>Fwhm</c> is the mean full-width-half-maximum in pixels.</summary>
public sealed record GuideFocusSampleDto(
    long Seq,
    DateTimeOffset CapturedUtc,
    double Hfr,
    int Stars,
    double PeakAdu,
    double Fwhm);

/// <summary>
/// The guide-camera focus loop's snapshot (<c>GET /api/v1/equipment/guider/focus</c>). <c>State</c>:
/// <c>idle</c> | <c>running</c> | <c>stopped</c> | <c>error</c> (the loop gave up after repeated capture
/// failures — <c>Error</c> says why). <c>BestHfr</c> is the lowest HFR seen this session with at least two
/// stars, the number to beat while turning the focuser; <c>Recent</c> is the trailing window the HFR trend
/// chart draws (newest last). A frame is at <c>GET …/focus/frame</c> when <c>HasFrame</c>.
/// </summary>
public sealed record GuideFocusStatusDto(
    bool Active,
    string State,
    double ExposureSec,
    long Seq,
    DateTimeOffset? StartedUtc,
    GuideFocusSampleDto? Latest,
    double? BestHfr,
    long? BestSeq,
    IReadOnlyList<GuideFocusSampleDto> Recent,
    string? Error,
    int ConsecutiveFailures,
    bool HasFrame);
