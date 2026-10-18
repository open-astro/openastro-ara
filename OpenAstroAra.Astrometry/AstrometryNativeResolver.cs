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
    /// §14e — loads the astrometry natives (<c>liberfa</c>, <c>libsupernovas</c>) from the
    /// assembly's own directory, where the build stages them. The P/Invokes carry
    /// <c>DllImportSearchPath.SafeDirectories</c> (CA5393), which on Linux does NOT include the
    /// assembly directory: without this resolver the runtime probed
    /// <c>runtimes/linux-x64/native/supernovas.so</c> and the shared-framework directory, found
    /// nothing, and every transform threw DllNotFoundException while the boot probe (which
    /// loads with the default search path) said the libraries were present (PR #1390 CI).
    /// Returning <see cref="IntPtr.Zero"/> for anything else hands resolution back to the runtime.
    /// </summary>
    internal static class AstrometryNativeResolver {

        private static int _registered;

        /// <summary>Idempotent — called from the static constructors of <see cref="SOFA"/> and
        /// <see cref="NOVAS"/>; SetDllImportResolver throws on a second registration.</summary>
        internal static void Register() {
            if (Interlocked.Exchange(ref _registered, 1) == 1) {
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(AstrometryNativeResolver).Assembly, Resolve);
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            return libraryName switch {
                // searchPath null = the assembly's default, which includes its own directory —
                // the same call AstrometryNatives.Probe makes, so probe and P/Invoke agree.
                "erfa" or "supernovas" =>
                    NativeLibrary.TryLoad(libraryName, assembly, searchPath: null, out var handle) ? handle : IntPtr.Zero,
                _ => IntPtr.Zero,
            };
        }
    }
}
