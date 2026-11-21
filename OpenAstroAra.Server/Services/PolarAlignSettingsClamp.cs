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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>§45.12 — the polar-align turn and rough-exposure knobs, held in range wherever they are
/// read or written. The app has no control for them yet (#1736) and an older build stored any value,
/// so a stored 0° turn (the mount "did not rotate") or a 60° one (two of them toward a GEM's limit) must never reach
/// the routine, and a PUT of the whole document must never be refused over a field the user cannot
/// reach. One table, used by <c>PUT /profile/polar-align</c> and by every read in the service.</summary>
public static class PolarAlignSettingsClamp {
    public const double MinSeedRotationDeg = 5.0;
    public const double MaxSeedRotationDeg = 30.0;
    public const double DefaultSeedRotationDeg = 10.0;
    public const double MinCheckStepDeg = 5.0;
    public const double MaxCheckStepDeg = 30.0;
    public const double DefaultCheckStepDeg = 10.0;
    public const double MaxCoarseExposureSeconds = 60.0;
    public const double DefaultCoarseExposureSeconds = 3.0;
    public const int MinFramesPerPoint = 1;
    public const int MaxFramesPerPoint = 5;
    public const int DefaultFramesPerPoint = 3;
    // #2068 (ARA-8) — the counterweight limit: 0° = Home (counterweight down), 90° = horizontal.
    public const double MinMaxCwAngleDeg = 10.0;
    public const double MaxMaxCwAngleDeg = 75.0;
    public const double DefaultMaxCwAngleDeg = 60.0;
    // A confirmation names the limit it is for; this close is the same limit (a JSON round trip).
    private const double ConfirmedLimitToleranceDeg = 1e-6;

    /// <summary>The same settings with the knobs in range: a non-finite or out-of-range turn is
    /// clamped (a non-finite one takes the default), a rough exposure that is not positive takes
    /// the default — one meaning for "no value", never "use the guide exposure on the main camera" —, the frames
    /// per point are held to 1–5, an unknown camera is the guide camera (#2082), and each mount's counterweight limit is
    /// held to 10–75° (#2068).</summary>
    public static PolarAlignSettingsDto Apply(PolarAlignSettingsDto settings) {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with {
            SeedRotationDeg = double.IsFinite(settings.SeedRotationDeg)
                ? Math.Clamp(settings.SeedRotationDeg, MinSeedRotationDeg, MaxSeedRotationDeg)
                : DefaultSeedRotationDeg,
            CheckStepDeg = double.IsFinite(settings.CheckStepDeg)
                ? Math.Clamp(settings.CheckStepDeg, MinCheckStepDeg, MaxCheckStepDeg)
                : DefaultCheckStepDeg,
            CoarseExposureSeconds = double.IsFinite(settings.CoarseExposureSeconds) && settings.CoarseExposureSeconds > 0
                ? Math.Min(settings.CoarseExposureSeconds, MaxCoarseExposureSeconds)
                : DefaultCoarseExposureSeconds,
            // #2082 — 1–5 frames per point; a camera the daemon does not know is the guide camera (the PUT refuses one,
            // an older or hand-edited profile.json may still hold it).
            FramesPerPoint = Math.Clamp(settings.FramesPerPoint, MinFramesPerPoint, MaxFramesPerPoint),
            Camera = PolarAlignCameras.IsValid(settings.Camera) ? settings.Camera : PolarAlignCameras.Guide,
            MountClearance = ClampMountClearance(settings.MountClearance),
        };
    }

    /// <summary>#2068 — the per-mount clearance entries: each limit held at 10–75° (a non-finite one takes the default,
    /// 60°). An entry stays confirmed only while the limit it was confirmed for (<c>ConfirmedMaxCwAngleDeg</c>) is its
    /// limit: a limit changed without a new confirmation is not confirmed (Joey, 2026-11-21). An entry with an empty
    /// key, or a null one, is dropped: no mount has that identity. None (null) stays none: no mount has an entry, so
    /// every mount reads the defaults (60°, not confirmed).</summary>
    private static Dictionary<string, PolarAlignMountClearanceDto>? ClampMountClearance(
            IReadOnlyDictionary<string, PolarAlignMountClearanceDto>? entries) {
        if (entries is null) {
            return null;
        }
        var clamped = new Dictionary<string, PolarAlignMountClearanceDto>(StringComparer.Ordinal);
        foreach (var (key, entry) in entries) {
            if (string.IsNullOrWhiteSpace(key) || entry is null) {
                continue;
            }
            var limit = ClampMaxCwAngleDeg(entry.MaxCwAngleDeg);
            var confirmed = entry.ClearanceConfirmed && entry.ConfirmedMaxCwAngleDeg is { } at && Math.Abs(at - limit) < ConfirmedLimitToleranceDeg;
            clamped[key] = entry with {
                MaxCwAngleDeg = limit,
                ClearanceConfirmed = confirmed,
                ConfirmedMaxCwAngleDeg = confirmed ? limit : null,
            };
        }
        return clamped;
    }

    /// <summary>#2068 — a counterweight limit in range: clamped to 10–75°; a non-finite one is the default, 60°.</summary>
    public static double ClampMaxCwAngleDeg(double deg) =>
        double.IsFinite(deg) ? Math.Clamp(deg, MinMaxCwAngleDeg, MaxMaxCwAngleDeg) : DefaultMaxCwAngleDeg;
}
