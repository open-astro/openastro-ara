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

/// <summary>Start the by-hand rotation readout toward a sky position angle (degrees east of north).</summary>
public sealed record RotationAssistStartRequestDto(double PositionAngleDeg);

/// <summary>One solved frame of the readout: the sky position angle the solver measured and the signed
/// shortest rotation still needed to reach the target, folded into (−90°, +90°] because a frame rotated
/// by 180° is the same framing.</summary>
public sealed record RotationAssistSampleDto(
    long Seq,
    DateTimeOffset SolvedUtc,
    double SolvedPositionAngleDeg,
    double DeltaDeg,
    // The solve's geometry, so the client can draw north and the planned framing over the frame: where the
    // frame centre points (J2000 degrees), the pixel scale, whether the train mirrors the sky (a flip reverses
    // the on-screen sense of a turn), and the frame's size in pixels.
    double RaDeg = 0,
    double DecDeg = 0,
    double PixelScaleArcsec = 0,
    bool Flipped = false,
    int FrameWidth = 0,
    int FrameHeight = 0);

/// <summary>
/// The by-hand rotation readout (a run's Rotate camera by hand step, or a manual start). <c>State</c>:
/// <c>idle</c> | <c>running</c> | <c>stopped</c> | <c>error</c>. <c>WithinTolerance</c> is true once the latest
/// solve sits within the profile's rotation tolerance of the target. The client turns the delta history into
/// advice relative to the user's last move; the daemon only measures.
/// </summary>
public sealed record RotationAssistStatusDto(
    bool Active,
    string State,
    double TargetPositionAngleDeg,
    double ToleranceDeg,
    long Seq,
    DateTimeOffset? StartedUtc,
    RotationAssistSampleDto? Latest,
    IReadOnlyList<RotationAssistSampleDto> Recent,
    bool WithinTolerance,
    string? Error,
    int ConsecutiveFailures,
    // The latest solved frame, rendered, is at GET /api/v1/rotation-assist/frame once HasFrame; FrameSeq
    // matches the sample it belongs to.
    bool HasFrame = false,
    long FrameSeq = 0);
