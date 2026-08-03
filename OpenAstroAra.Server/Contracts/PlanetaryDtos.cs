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

namespace OpenAstroAra.Server.Contracts;

/// <summary>§77.2 enter-planetary-mode request: which SDK camera to take over.</summary>
public sealed record PlanetaryEnterRequestDto(int CameraId, int? UsbfsOverrideMb);

/// <summary>§77.4 start-recording request. Geometry is SDK-ready (ZWO: width % 8,
/// height % 2 after binning). Output path is derived server-side when omitted.</summary>
public sealed record PlanetaryRecordRequestDto(
    int StartX,
    int StartY,
    int Width,
    int Height,
    string Format,          // mono8|mono16|bayer_rggb8|... (VideoPixelFormat, snake_case)
    long Gain,
    int ExposureMs,
    string? OutputPath,
    int Bin = 1);           // JSON-omitted bin gets the documented default, not 0

/// <summary>Live §77.1 honest-accounting counters, WS + REST shared shape.</summary>
public sealed record PlanetaryRecordingStatsDto(
    bool Recording,
    ulong FramesCaptured,
    ulong FramesWritten,
    ulong RingDroppedFrames,
    ulong AbandonedFrames,
    ulong SdkDroppedFrames,
    ulong BytesWritten,
    double AchievedFps,
    string Error);

/// <summary>GET /planetary/status.</summary>
public sealed record PlanetaryStatusDto(
    string Mode,                       // idle | planetary
    int? CameraId,
    string? OutputPath,
    long? DiskFreeBytes,
    int? UsbfsMemoryMb,
    bool UsesDirectIo,
    PlanetaryRecordingStatsDto? Recording);
