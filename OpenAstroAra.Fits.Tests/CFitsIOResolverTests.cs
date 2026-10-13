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
[CollectionDefinition(CFitsIOResolverSerialFixture.Name, DisableParallelization = true)]
public sealed class CFitsIOResolverSerialFixture {
    /// <summary>The real-entry-point tests set the process env var and reset the resolver's static
    /// handle; nothing else touching <see cref="CFitsIO"/> may run beside them.</summary>
    public const string Name = "cfitsio-resolver-state";
}

[Collection(CFitsIOResolverSerialFixture.Name)]
public class CFitsIOResolverTests {
    private static readonly IntPtr Fake = new(0x1234);

    // Records every name offered to a loader and succeeds only for the names in `loadable`.
    private sealed class FakeLoader(params string[] loadable) {
        public List<string> Calls { get; } = [];

        /// <summary>Per-name handle, so a test can tell a decoy library from the real one.</summary>
        public Func<string, IntPtr> HandleFor { get; set; } = _ => Fake;

        public bool TryLoad(string name, out IntPtr handle) {
            Calls.Add(name);
            handle = loadable.Contains(name) ? HandleFor(name) : IntPtr.Zero;
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
    public void A_loadable_library_without_the_cfitsio_export_is_rejected_and_the_walk_continues() {
        // #1214: OPENASTROARA_CFITSIO_PATH=libm.so.6 loads fine but is not CFITSIO. It used to be
        // cached and win over a good libcfitsio.so.10; now it is released and the real one is used.
        var wrong = new IntPtr(0x999);
        var plain = new FakeLoader("/usr/lib/libm.so.6", "libcfitsio.so.10") {
            HandleFor = name => name == "/usr/lib/libm.so.6" ? wrong : Fake,
        };
        var dflt = new FakeLoader();
        var (handle, report) = CFitsIOResolver.Resolve(
            "/usr/lib/libm.so.6", dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10"],
            hasRequiredExport: h => h == Fake);
        Assert.Equal(Fake, handle);
        Assert.Equal("libcfitsio.so.10", report.LoadedFrom);
        Assert.False(report.ExplicitPathLoaded);
        Assert.Equal(
            ["/usr/lib/libm.so.6" + CFitsIOResolver.WrongLibrarySuffix, CFitsIOResolver.DefaultProbeLabel, "libcfitsio.so.10"],
            report.Tried);
    }

    [Fact]
    public void Every_candidate_wrong_reports_each_as_not_cfitsio() {
        var plain = new FakeLoader("libcfitsio.so.10");
        var dflt = new FakeLoader("cfitsio");
        var (handle, report) = CFitsIOResolver.Resolve(
            null, dflt.TryLoad, plain.TryLoad, ["libcfitsio.so.10"], hasRequiredExport: _ => false);
        Assert.Equal(IntPtr.Zero, handle);
        Assert.False(report.Loaded);
        Assert.Equal(
            [CFitsIOResolver.DefaultProbeLabel + CFitsIOResolver.WrongLibrarySuffix, "libcfitsio.so.10" + CFitsIOResolver.WrongLibrarySuffix],
            report.Tried);
    }

    [Fact]
    public void The_real_entry_point_reads_OPENASTROARA_CFITSIO_PATH_from_the_environment() {
        // #1214: the pure tests inject getEnv; this one goes through ResolveForRuntime, so swapping
        // Environment.GetEnvironmentVariable for `_ => null` in the resolver fails here.
        const string bogus = "/nonexistent/openastroara-cfitsio-probe/libcfitsio.so.10";
        var previous = Environment.GetEnvironmentVariable(CFitsIOResolver.PathEnvVar);
        try {
            Environment.SetEnvironmentVariable(CFitsIOResolver.PathEnvVar, bogus);
            CFitsIOResolver.ResetForTests();
            CFitsIOResolver.ResolveForRuntime(CFitsIOResolver.LibraryName, typeof(CFitsIO).Assembly, null);
            var report = CFitsIOResolver.LastResolution;
            Assert.NotNull(report);
            Assert.Equal(bogus, report!.ExplicitPath);
            Assert.Equal(bogus, report.Tried[0]);
            Assert.False(report.ExplicitPathLoaded);
            // Unrelated library names are not ours to resolve.
            Assert.Equal(IntPtr.Zero, CFitsIOResolver.ResolveForRuntime("something-else", typeof(CFitsIO).Assembly, null));
        } finally {
            Environment.SetEnvironmentVariable(CFitsIOResolver.PathEnvVar, previous);
            CFitsIOResolver.ResetForTests();
        }
    }

    [Fact]
    public void The_real_entry_point_rejects_a_loadable_non_cfitsio_library() {
        // libm (Linux) / libSystem (macOS) load anywhere, carry no ffgerr, and must not be cached as CFITSIO.
        string? decoy = OperatingSystem.IsLinux() ? "libm.so.6" : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : null;
        if (decoy is null) return; // Windows: no portable decoy library name (xunit 2.9 has no runtime Skip)
        var previous = Environment.GetEnvironmentVariable(CFitsIOResolver.PathEnvVar);
        try {
            Environment.SetEnvironmentVariable(CFitsIOResolver.PathEnvVar, decoy);
            CFitsIOResolver.ResetForTests();
            CFitsIOResolver.ResolveForRuntime(CFitsIOResolver.LibraryName, typeof(CFitsIO).Assembly, null);
            var report = CFitsIOResolver.LastResolution;
            Assert.NotNull(report);
            Assert.Equal(decoy + CFitsIOResolver.WrongLibrarySuffix, report!.Tried[0]);
            Assert.NotEqual(decoy, report.LoadedFrom);
        } finally {
            Environment.SetEnvironmentVariable(CFitsIOResolver.PathEnvVar, previous);
            CFitsIOResolver.ResetForTests();
        }
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
