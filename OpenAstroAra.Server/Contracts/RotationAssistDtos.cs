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

/// <summary>The readout's capture modes: <c>loop</c> solves frame after frame until stopped; <c>single</c>
/// takes one frame, solves it and ends in <c>stopped</c> with the result kept.</summary>
public static class RotationAssistModes {
    public const string Loop = "loop";
    public const string SingleShot = "single";
}

/// <summary>Start the by-hand rotation readout toward a sky position angle (degrees east of north).
/// <c>ExposureSeconds</c> (0.01–60; default: the profile's plate-solve exposure) is the main camera's
/// exposure per frame; <c>Mode</c> is <c>loop</c> (default) or <c>single</c>.
/// <c>Binning</c> (1–4, never above the camera's maximum; default: the camera's maximum capped at 4) is the
/// square binning of the loop's frames — a protractor needs no resolution, and binned frames download and
/// solve several times faster.</summary>
public sealed record RotationAssistStartRequestDto(double PositionAngleDeg, double? ExposureSeconds = null, string? Mode = null, int? Binning = null);

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
/// The by-hand rotation readout, started from the Plan screen's framing. <c>State</c>:
/// <c>idle</c> | <c>running</c> | <c>stopped</c> | <c>confirming</c> | <c>confirmed</c> | <c>not_confirmed</c> | <c>error</c>.
/// <c>Confirmation</c> is the 1×1 full-exposure solve a <c>confirm</c> took once the user said Done:
/// <c>confirmed</c> when it sits within the tolerance (the framing is approved), <c>not_confirmed</c> when it
/// does not (keep adjusting). <c>WithinTolerance</c> is true once the latest
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
    long FrameSeq = 0,
    // The capture settings of the current (or last) readout, and the profile's plate-solve exposure the
    // client pre-fills its exposure field with.
    string Mode = RotationAssistModes.Loop,
    double ExposureSeconds = 0,
    double DefaultExposureSeconds = 0,
    // The loop's binning: in use (or last used), what a start without one would pick, and the camera's
    // ceiling (0 = unknown).
    int Binning = 1,
    int AutoBinning = 1,
    int MaxBinning = 0,
    RotationAssistSampleDto? Confirmation = null);
