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

/// <summary>Start the Bahtinov focus readout. <c>ExposureSec</c> 0.05–30 s (a bright star through the mask
/// wants well under a second to a few seconds); <c>Binning</c> 1–4, default 1 (the offset is sub-pixel, so
/// binning costs precision — it is there for cameras whose full frames download slowly).</summary>
public sealed record BahtinovFocusStartRequestDto(double ExposureSec = 1.0, int? Binning = null);

/// <summary>One fitted spike over the star crop, in crop pixel coordinates (0…<c>CropSize</c>, the picture
/// at <c>GET …/frame</c>). <c>Role</c> is <c>outer</c> (the X) or <c>central</c>.</summary>
public sealed record BahtinovLineDto(string Role, double X1, double Y1, double X2, double Y2, double AngleDeg);

/// <summary>What the client draws over the star crop: the three lines and the X's crossing.</summary>
public sealed record BahtinovOverlayDto(
    int CropSize,
    IReadOnlyList<BahtinovLineDto> Lines,
    double IntersectionX,
    double IntersectionY,
    double SpreadDeg);

/// <summary>
/// One measured frame. <c>Detected</c> false means no measurement — <c>Problem</c> says why
/// (<c>no_star</c> | <c>near_edge</c> | <c>no_pattern</c>). <c>OffsetPx</c> is the central spike's signed
/// distance from the X's crossing in (binned) camera pixels: zero at best focus, the sign flips through it,
/// and the sign convention holds for the whole session. <c>DefocusUm</c> is the estimated focuser error in
/// micrometres (null without the focal ratio and pixel size in the profile). <c>Overlay</c> is on the
/// latest sample only.
/// </summary>
public sealed record BahtinovSampleDto(
    long Seq,
    DateTimeOffset CapturedUtc,
    bool Detected,
    string? Problem,
    double? OffsetPx,
    double? DefocusUm,
    bool WithinZone,
    int StarX,
    int StarY,
    double PeakAdu,
    BahtinovOverlayDto? Overlay = null);

/// <summary>
/// The Bahtinov focus readout's snapshot (<c>GET /api/v1/bahtinov-focus/state</c>). <c>State</c>:
/// <c>idle</c> | <c>running</c> | <c>stopped</c> | <c>error</c> (the camera failed repeatedly —
/// <c>Error</c> says why). <c>ZonePx</c> is the in-focus limit on the offset: the critical focus zone from
/// the optics when <c>ZoneFromOptics</c>, else a fixed half pixel. <c>BestOffsetPx</c> is the
/// smallest-magnitude offset this session (signed). <c>Recent</c> is the trend window, newest last. The star
/// crop is at <c>GET …/frame</c> when <c>HasFrame</c>.
/// </summary>
public sealed record BahtinovFocusStatusDto(
    bool Active,
    string State,
    double ExposureSec,
    int Binning,
    long Seq,
    DateTimeOffset? StartedUtc,
    BahtinovSampleDto? Latest,
    IReadOnlyList<BahtinovSampleDto> Recent,
    double? BestOffsetPx,
    double ZonePx,
    bool ZoneFromOptics,
    double? ZoneUm,
    double? FocalRatio,
    bool WithinZone,
    string? Error,
    int ConsecutiveFailures,
    bool HasFrame,
    long FrameSeq);
