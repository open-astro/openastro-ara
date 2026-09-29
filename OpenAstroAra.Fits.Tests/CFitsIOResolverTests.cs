#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;
using OpenAstroAra.Fits;
using Xunit;

namespace OpenAstroAra.Fits.Tests;

/// <summary>
/// #1120: the CFITSIO resolver's candidate order and its report, driven through fake loaders so
/// the order is pinned on every platform whether or not the native library is installed.
/// </summary>
public class CFitsIOResolverTests {
    private static readonly IntPtr Fake = new(0x1234);

    // Records every name offered to a loader and succeeds only for the names in `loadable`.
    private sealed class FakeLoader(params string[] loadable) {
        public List<string> Calls { get; } = [];

        public bool TryLoad(string name, out IntPtr handle) {
            Calls.Add(name);
            handle = loadable.Contains(name) ? Fake : IntPtr.Zero;
            return handle != IntPtr.Zero;
        }
    }

    [Fact]
    public void Explicit_path_is_tried_first_and_wins_when_it_loads() {
        var dflt = new FakeLoader("cfitsio");
        var plain = new FakeLoader("/opt/cfitsio/libcfitsio.so");

        var (handle, report) = CFitsIOResolver.Resolve(
            "/opt/cfitsio/libcfitsio.so", dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10"]);

        Assert.Equal(Fake, handle);
        Assert.Equal(["/opt/cfitsio/libcfitsio.so"], plain.Calls);
        Assert.Empty(dflt.Calls);
        Assert.Equal("/opt/cfitsio/libcfitsio.so", report.LoadedFrom);
        Assert.Equal("/opt/cfitsio/libcfitsio.so", report.ExplicitPath);
        Assert.Equal(["/opt/cfitsio/libcfitsio.so"], report.Tried);
    }

    [Fact]
    public void Failed_explicit_path_falls_through_to_default_probing() {
        var dflt = new FakeLoader("cfitsio");
        var plain = new FakeLoader();

        var (handle, report) = CFitsIOResolver.Resolve(
            "/nope/libcfitsio.so", dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10"]);

        Assert.Equal(Fake, handle);
        Assert.Equal(["cfitsio"], dflt.Calls);
        Assert.Equal(CFitsIOResolver.DefaultProbeLabel, report.LoadedFrom);
        Assert.Equal(["/nope/libcfitsio.so", CFitsIOResolver.DefaultProbeLabel], report.Tried);
        Assert.False(report.ExplicitPathLoaded);
    }

    [Fact]
    public void Versioned_sonames_follow_default_probing_in_order() {
        var dflt = new FakeLoader();
        var plain = new FakeLoader("libcfitsio.so.10", "libcfitsio.so.11");

        var (handle, report) = CFitsIOResolver.Resolve(
            null, dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10", "libcfitsio.so.11"]);

        Assert.Equal(Fake, handle);
        Assert.Equal(["libcfitsio.so.10"], plain.Calls);
        Assert.Equal("libcfitsio.so.10", report.LoadedFrom);
        Assert.Equal([CFitsIOResolver.DefaultProbeLabel, "libcfitsio.so.10"], report.Tried);
        Assert.Null(report.ExplicitPath);
    }

    [Fact]
    public void Every_candidate_failing_reports_each_one_tried() {
        var dflt = new FakeLoader();
        var plain = new FakeLoader();

        var (handle, report) = CFitsIOResolver.Resolve(
            "/nope/cfitsio.dll", dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10"]);

        Assert.Equal(IntPtr.Zero, handle);
        Assert.Null(report.LoadedFrom);
        Assert.False(report.Loaded);
        Assert.Equal(["/nope/cfitsio.dll", CFitsIOResolver.DefaultProbeLabel, "libcfitsio.so.10"], report.Tried);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" /opt/lib/libcfitsio.so.10 ", "/opt/lib/libcfitsio.so.10")]
    public void Explicit_path_comes_from_OPENASTROARA_CFITSIO_PATH(string? raw, string? expected) {
        string? asked = null;
        var path = CFitsIOResolver.ReadExplicitPath(name => {
            asked = name;
            return raw;
        });

        Assert.Equal("OPENASTROARA_CFITSIO_PATH", asked);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Only_the_ABI_10_sonames_are_fallbacks() {
        // Debian 11 / Ubuntu 22.04 (ABI 9) are not targets; the .deb is arm64 Debian 13.
        Assert.Equal(["libcfitsio.so.10"], CFitsIOResolver.LinuxSonames);
        Assert.Equal(["libcfitsio.10.dylib"], CFitsIOResolver.MacSonames);
    }

    [Fact]
    public void Linux_runtime_package_soname_is_loadable() {
        // Pins the versioned-soname fallback to what libcfitsio10 (the .deb's Depends) installs,
        // independent of whether the runner also has the -dev package's unversioned symlink.
        // Same gate as FitsImageTests.MustLoadCfitsio: a Linux dev box without the package skips.
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return;
        Assert.True(NativeLibrary.TryLoad("libcfitsio.so.10", out var handle),
            "libcfitsio.so.10 did not load; install the runtime package: sudo apt-get install libcfitsio10");
        NativeLibrary.Free(handle);
    }

    [Fact]
    public void Probe_never_throws_and_reports_what_it_tried() {
        var result = FitsLibraryProbe.Probe();

        Assert.NotEmpty(result.Resolution.Tried);
        if (result.Loaded) {
            Assert.NotNull(result.Resolution.LoadedFrom);
            Assert.Null(result.Error);
        } else {
            Assert.NotNull(result.Error);
        }
    }
}
