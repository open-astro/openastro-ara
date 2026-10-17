#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Reflection;
using System.Runtime.InteropServices;
using OpenAstroAra.Image.FileFormat.FITS;
using Xunit;

namespace OpenAstroAra.Image.Tests;

/// <summary>
/// Pins the NINA-era CFITSIO binding to the literal-name ("disk file") entry points. A target
/// name lands in the frame path ("Lion Nebula (Sh2-132)/…"), and ffinit/ffopen parse CFITSIO's
/// extended filename syntax, reading "(…)" as a template and "[…]" as an HDU filter. A round
/// trip cannot run here: this binding loads <c>cfitsionative.dll</c>, which has no resolver and
/// is not shipped off Windows (it throws DllNotFoundException on the CI runners), so the binding
/// itself is what gets checked. The daemon's own writer is covered by FitsImageTests.
/// </summary>
public sealed class CfitsioNativeBindingTests {
    [Theory]
    [InlineData("fits_create_file", "ffdkinit")]
    [InlineData("fits_open_file", "ffdkopn")]
    public void File_entry_points_take_the_name_literally(string method, string entryPoint) {
        var native = typeof(CFitsioFITS).Assembly.GetType("OpenAstroAra.Image.FileFormat.FITS.CfitsioNative", throwOnError: true)!;
        var m = native.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(m);
        var import = m!.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        Assert.Equal(entryPoint, import!.EntryPoint);
    }
}
