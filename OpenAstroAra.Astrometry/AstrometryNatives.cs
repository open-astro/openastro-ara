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
using System.Runtime.InteropServices;

namespace OpenAstroAra.Astrometry {

    /// <summary>
    /// Boot-time presence check for the SOFA + NOVAS31 natives the cross-epoch transforms,
    /// rise/set bodies and Julian-date helpers P/Invoke. The daemon logs the result at startup so
    /// a package that shipped without them (they are built by <c>scripts/build-astrometry-natives.sh</c>
    /// and staged next to the binary by CI) is obvious in the first lines of the log rather than
    /// as a <c>TypeInitializationException</c> the first time a sequence's altitude condition
    /// or a polar-align solve runs. Probing loads the libraries, which is exactly what the first
    /// real call would do, so a true here means that call will not fault on load.
    /// </summary>
    public static class AstrometryNatives {

        /// <summary>The on-disk names the runtime probes, per OS, for log messages.</summary>
        public static (string Sofa, string Novas) ExpectedFileNames =>
            OperatingSystem.IsWindows() ? ("SOFAlib.dll", "NOVAS31lib.dll")
            : OperatingSystem.IsMacOS() ? ("libsofa.dylib", "libnovas31.dylib")
            : ("libsofa.so", "libnovas31.so");

        /// <summary>True for each library that loads from the assembly's directory or the
        /// platform's default probe path AND exports the entry point the managed side calls first
        /// (a library built from a partial source set would dlopen fine and fail on first use).
        /// Never throws.</summary>
        public static (bool Sofa, bool Novas) Probe() {
            var (sofaName, novasName) = OperatingSystem.IsWindows()
                ? ("SOFAlib.dll", "NOVAS31lib.dll")
                : ("sofa", "novas31");
            // iauAtci13: the J2000→JNOW transform (Coordinates.TransformToJNOW); julian_date: what
            // every transform's TT date goes through (AstroUtil.GetJulianDate). Names match the
            // DllImport EntryPoints in SOFA.cs / NOVAS.cs.
            return (TryLoad(sofaName, "iauAtci13"), TryLoad(novasName, "julian_date"));
        }

        private static bool TryLoad(string name, string export) {
            try {
                if (NativeLibrary.TryLoad(name, typeof(AstrometryNatives).Assembly, searchPath: null, out var handle)) {
                    try {
                        return NativeLibrary.TryGetExport(handle, export, out _);
                    } finally {
                        NativeLibrary.Free(handle);
                    }
                }
            } catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException) {
                // fall through — "absent" is the answer
            }
            return false;
        }
    }
}
