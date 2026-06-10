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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenAstroAra.Astrometry {

    /// <summary>
    /// §14e — maps the inherited Windows-named astrometry P/Invokes (<c>SOFAlib.dll</c> /
    /// <c>NOVAS31lib.dll</c>) to the cross-platform binaries built from the vendored C sources by
    /// <c>scripts/build-astrometry-natives.sh</c> (<c>libsofa</c> / <c>libnovas31</c>). Returning
    /// <see cref="IntPtr.Zero"/> for anything unmatched (or when the mapped library is absent)
    /// hands resolution back to the runtime's default probing, so behavior on Windows — and the
    /// DllNotFoundException surface on hosts without the natives — is unchanged.
    /// </summary>
    internal static class AstrometryNativeResolver {

        private static int _registered;

        /// <summary>
        /// Idempotent — called from the static constructors of the P/Invoke wrapper classes
        /// (<see cref="SOFA"/>, <see cref="NOVAS"/>) so it is guaranteed to run before their first
        /// native call. SetDllImportResolver throws on a second registration for the same
        /// assembly, hence the guard.
        /// </summary>
        internal static void Register() {
            if (Interlocked.Exchange(ref _registered, 1) == 1) {
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(AstrometryNativeResolver).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            if (OperatingSystem.IsWindows()) {
                return IntPtr.Zero; // Windows keeps the inherited SOFA/NOVAS DLL probing untouched
            }
            return libraryName switch {
                "SOFAlib.dll" => TryLoad("sofa", assembly),
                "NOVAS31lib.dll" => TryLoad("novas31", assembly),
                _ => IntPtr.Zero,
            };
        }

        private static IntPtr TryLoad(string name, Assembly assembly) =>
            NativeLibrary.TryLoad(name, assembly, searchPath: null, out var handle) ? handle : IntPtr.Zero;
    }
}
