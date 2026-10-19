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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §45.4 polar-align binning from the camera's capabilities. The PA frames come from the GUIDE
/// camera through the guider daemon (not the main camera over Alpaca), so the only capability the
/// daemon exposes is the frame size (<c>get_camera_frame_size</c>); sensor area is what drives
/// transfer and solve time anyway. A profile value of <see cref="Auto"/> (the default) picks the
/// tier for the sensor; any other value is the user's override (§45.4: "user can override … if
/// their camera misbehaves at higher bin"). Pure — unit-tested.
/// </summary>
public static class PolarAlignBinning {

    /// <summary>Profile value meaning "pick from the camera" (the §45.12 default).</summary>
    public const int Auto = 0;

    /// <summary>The §45.4 cap: 4×4 keeps a 62 MP QHY600 under ~8 MB per frame.</summary>
    public const int Max = 4;

    /// <summary>The tier for a sensor of <paramref name="width"/> × <paramref name="height"/>
    /// native pixels (§45.4 table): a guide camera (≤ 2.5 MP, ASI120 / ASI290 / ASI174) stays at
    /// 1×1 — already a small frame and the few stars in a pole field are not to be binned away;
    /// ≤ 8 MP 2×2; ≤ 16 MP (ASI294-class) 3×3; larger 4×4. An unknown size (0) is 1×1.</summary>
    public static int ForFrame(int width, int height) {
        if (width <= 0 || height <= 0) {
            return 1;
        }
        var megapixels = (long)width * height / 1_000_000.0;
        if (megapixels <= 2.5) {
            return 1;
        }
        if (megapixels <= 8.0) {
            return 2;
        }
        if (megapixels <= 16.0) {
            return 3;
        }
        return Max;
    }

    /// <summary>The binning to shoot with: the profile's own value when set, otherwise
    /// <see cref="ForFrame"/> of the camera's frame (1×1 when the daemon could not report it).</summary>
    public static int Resolve(int configured, (int Width, int Height)? frameSize) {
        if (configured > 0) {
            return Math.Min(configured, Max);
        }
        return frameSize is { } f ? ForFrame(f.Width, f.Height) : 1;
    }
}
