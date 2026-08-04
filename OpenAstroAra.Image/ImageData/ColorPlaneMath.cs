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
using System.IO;
using System.Threading;

namespace OpenAstroAra.Image.ImageData;

public static class ColorPlaneMath {
    /// <summary>Creates a weighted Rec. 709 luminance or luma plane from unsigned 16-bit RGB.</summary>
    public static ushort[] CreateLuminance(ushort[] red, ushort[] green, ushort[] blue,
            CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (red.Length != green.Length || red.Length != blue.Length) {
            throw new InvalidDataException("Color planes have mismatched lengths.");
        }

        var luminance = new ushort[red.Length];
        for (var index = 0; index < luminance.Length; index++) {
            if ((index & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            luminance[index] = (ushort)(((ulong)red[index] * 2126
                + (ulong)green[index] * 7152 + (ulong)blue[index] * 722 + 5000) / 10000);
        }
        return luminance;
    }
}