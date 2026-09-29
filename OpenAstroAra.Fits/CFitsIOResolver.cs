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

namespace OpenAstroAra.Fits;

/// <summary>
/// What the CFITSIO resolver tried and which candidate loaded (§72.3, #1120).
/// </summary>
/// <param name="ExplicitPath">The <c>OPENASTROARA_CFITSIO_PATH</c> value, or null when unset.</param>
/// <param name="Tried">Every candidate offered to the loader, in order.</param>
/// <param name="LoadedFrom">The candidate that loaded, or null when every one failed.</param>
public sealed record CFitsIOResolution(string? ExplicitPath, IReadOnlyList<string> Tried, string? LoadedFrom) {
    /// <summary>True when some candidate loaded.</summary>
    public bool Loaded => LoadedFrom is not null;

    /// <summary>True when <c>OPENASTROARA_CFITSIO_PATH</c> was set and is what loaded.</summary>
    public bool ExplicitPathLoaded => ExplicitPath is not null && LoadedFrom == ExplicitPath;
}

/// <summary>
/// §72.3 <see cref="NativeLibrary"/> resolver for CFITSIO. Candidate order:
/// <list type="number">
/// <item><c>OPENASTROARA_CFITSIO_PATH</c>, the full path to the library file, when set.</item>
/// <item>The runtime's default probing of the bare name (application directory per the
/// assembly-level <see cref="DllImportSearchPath.SafeDirectories"/>, then the system loader).</item>
/// <item>The ABI 10 versioned soname (CFITSIO 4.1+: Debian 12/13, Ubuntu 24.04+, Homebrew).
/// ABI 9 (Debian 11, Ubuntu 22.04) is not a target and is not tried.</item>
/// </list>
/// The outcome is kept in <see cref="LastResolution"/>; the daemon's boot probe
/// (<see cref="FitsLibraryProbe.Probe"/>) triggers the first resolution and logs it.
/// </summary>
internal static class CFitsIOResolver {
    internal const string LibraryName = "cfitsio";
    internal const string PathEnvVar = "OPENASTROARA_CFITSIO_PATH";
    internal const string DefaultProbeLabel = "cfitsio (runtime default probing)";

    // A bare soname goes straight to dlopen, which resolves it through the loader cache
    // (ld.so.cache / DYLD fallback paths); no directory of ours is involved, so DLL planting
    // is not a concern. On macOS a bare dlopen does not search /opt/homebrew/lib, so the
    // dylib name mostly matters for a system-wide install; Homebrew is handled by the
    // CopyLibCfitsioMacOS build target instead.
    internal static readonly string[] LinuxSonames = ["libcfitsio.so.10"];
    internal static readonly string[] MacSonames = ["libcfitsio.10.dylib"];

    internal delegate bool TryLoadLibrary(string name, out IntPtr handle);

    private static readonly Lock s_gate = new();
    private static IntPtr s_handle;
    private static CFitsIOResolution? s_last;

    /// <summary>The most recent resolution, or null when CFITSIO has not been resolved yet.</summary>
    internal static CFitsIOResolution? LastResolution {
        get {
            lock (s_gate) {
                return s_last;
            }
        }
    }

    /// <summary>
    /// Reads <see cref="PathEnvVar"/> through <paramref name="getEnv"/>; blank means unset.
    /// </summary>
    internal static string? ReadExplicitPath(Func<string, string?> getEnv) {
        var raw = getEnv(PathEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>
    /// The <see cref="NativeLibrary.SetDllImportResolver"/> callback. Caches a successful handle so
    /// later P/Invoke binds reuse it. Returning zero hands resolution back to the runtime, whose own
    /// failure message lists the paths it probed.
    /// </summary>
    internal static IntPtr ResolveForRuntime(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (libraryName != LibraryName) {
            return IntPtr.Zero;
        }
        lock (s_gate) {
            if (s_handle != IntPtr.Zero) {
                return s_handle;
            }
            string[] sonames = OperatingSystem.IsLinux() ? LinuxSonames
                : OperatingSystem.IsMacOS() ? MacSonames
                : [];
            var (handle, report) = Resolve(
                ReadExplicitPath(Environment.GetEnvironmentVariable),
                (string name, out IntPtr h) => NativeLibrary.TryLoad(name, assembly, searchPath, out h),
                NativeLibrary.TryLoad,
                sonames);
            s_handle = handle;
            s_last = report;
            return handle;
        }
    }

    /// <summary>
    /// Pure candidate walk: <paramref name="explicitPath"/> through <paramref name="tryLoad"/>, then
    /// <see cref="LibraryName"/> through <paramref name="tryDefault"/>, then each of
    /// <paramref name="versionedSonames"/> through <paramref name="tryLoad"/>. Stops at the first load.
    /// </summary>
    internal static (IntPtr Handle, CFitsIOResolution Report) Resolve(
        string? explicitPath, TryLoadLibrary tryDefault, TryLoadLibrary tryLoad, IReadOnlyList<string> versionedSonames) {
        var tried = new List<string>();
        IntPtr handle;
        if (explicitPath is not null) {
            tried.Add(explicitPath);
            if (tryLoad(explicitPath, out handle)) {
                return (handle, new CFitsIOResolution(explicitPath, tried, explicitPath));
            }
        }
        tried.Add(DefaultProbeLabel);
        if (tryDefault(LibraryName, out handle)) {
            return (handle, new CFitsIOResolution(explicitPath, tried, DefaultProbeLabel));
        }
        foreach (var soname in versionedSonames) {
            tried.Add(soname);
            if (tryLoad(soname, out handle)) {
                return (handle, new CFitsIOResolution(explicitPath, tried, soname));
            }
        }
        return (IntPtr.Zero, new CFitsIOResolution(explicitPath, tried, null));
    }
}
