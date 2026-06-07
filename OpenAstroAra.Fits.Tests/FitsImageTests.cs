#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Fits;
using Xunit;

namespace OpenAstroAra.Fits.Tests;

public class FitsImageTests {
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"oara-fits-{Guid.NewGuid():N}-{name}");

    // Phase 0.5p2 net10.0 conversion: CFITSIO is installed via apt
    // (libcfitsio10) on the Linux CI runner per playbook §72.7. On macOS /
    // Windows dev machines without the native we early-return so each test
    // passes-through; xunit 2.x lacks Assert.Skip so no skipped marker.
    private static readonly bool CfitsioAvailable = CheckCfitsio();
    private static bool CheckCfitsio() {
        try { FitsLibraryProbe.EnsureLoadable(); return true; } catch { return false; }
    }

    [Fact]
    public void CFitsIO_loads_on_startup() {
        if (!CfitsioAvailable) return;
        // §72.7 first test case: confirm the native library is resolvable.
        FitsLibraryProbe.EnsureLoadable();
    }

    [Fact]
    public void Write_read_round_trip_preserves_ushort_pixels() {
        if (!CfitsioAvailable) return;
        var path = TempPath("roundtrip.fits");
        try {
            const int w = 16;
            const int h = 12;
            var input = new ushort[w * h];
            for (var i = 0; i < input.Length; i++) input[i] = (ushort)(i * 7 + 13);

            using (var fits = FitsImage.Create(path, w, h, FitsBitDepth.UnsignedShort)) {
                fits.SetHeader("EXPOSURE", 180.0, "Exposure time in seconds");
                fits.SetHeader("CCD-TEMP", -10.5, "CCD temperature (C)");
                fits.SetHeader("INSTRUME", "Test Camera", "Camera model");
                fits.WriteImageData(input);
            }
            // After Dispose, the .tmp file must be gone and the real file present
            // — this is the §28.7 atomic-write guarantee.
            Assert.False(File.Exists(path + ".tmp"), "Temp file should be renamed away on Dispose");
            Assert.True(File.Exists(path), "Final file should exist after Dispose");

            using (var fits = FitsImage.Open(path)) {
                var (rw, rh) = fits.GetDimensions();
                Assert.Equal(w, rw);
                Assert.Equal(h, rh);

                var output = fits.ReadImageData16();
                Assert.Equal(input.Length, output.Length);
                Assert.Equal(input, output);

                var headers = fits.ReadHeaders();
                Assert.Contains("EXPOSURE", headers.Keys);
                Assert.Contains("INSTRUME", headers.Keys);
                Assert.Equal("Test Camera", headers["INSTRUME"]);
            }
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Write_read_round_trip_preserves_float_pixels() {
        if (!CfitsioAvailable) return;
        var path = TempPath("float-roundtrip.fits");
        try {
            const int w = 8;
            const int h = 6;
            var input = new float[w * h];
            for (var i = 0; i < input.Length; i++) input[i] = i * 0.5f - 3.14f;

            using (var fits = FitsImage.Create(path, w, h, FitsBitDepth.Float)) {
                fits.WriteImageData(input);
            }

            using (var fits = FitsImage.Open(path)) {
                var output = fits.ReadImageDataFloat();
                Assert.Equal(input, output);
            }
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Buffer_length_mismatch_throws_argument_exception() {
        if (!CfitsioAvailable) return;
        var path = TempPath("mismatch.fits");
        try {
            using var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort);
            // 4×4 = 16 pixels; pass 10
            var ex = Assert.Throws<ArgumentException>(() =>
                fits.WriteImageData(new ushort[10]));
            Assert.Contains("doesn't match", ex.Message);
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Stale_temp_file_is_purged_on_create() {
        if (!CfitsioAvailable) return;
        // §28.7 atomic-write defense: if a prior write crashed mid-flight
        // leaving a .tmp behind, Create() should purge it rather than fail
        // with EEXIST.
        var path = TempPath("purge.fits");
        try {
            File.WriteAllBytes(path + ".tmp", new byte[] { 0xff, 0xfe, 0xfd });
            using (var fits = FitsImage.Create(path, 4, 4, FitsBitDepth.UnsignedShort)) {
                fits.WriteImageData(new ushort[16]);
            }
            Assert.True(File.Exists(path));
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}