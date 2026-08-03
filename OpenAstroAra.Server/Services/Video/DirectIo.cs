#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// The §77.1 direct-I/O shim: opens the SER output O_DIRECT on Linux (page-cache
    /// writeback would eat all free RAM on a 2 GB box mid-capture) and clears the flag
    /// for the unaligned finalize tail. Filesystems that refuse O_DIRECT (tmpfs) fall
    /// back to buffered I/O — honestly reported, never silent. On macOS dev hosts
    /// F_NOCACHE is the equivalent hint; on Windows plain buffered I/O is used (the
    /// daemon's deployment target is the Linux capture box).
    /// </summary>
    internal static partial class DirectIo {
        private const int O_WRONLY = 0x0001;
        private const int O_CREAT_LINUX = 0x0040;
        private const int O_TRUNC_LINUX = 0x0200;
        private const int O_DIRECT_LINUX = 0x4000;      // x86-64/arm64 generic ABI value
        private const int F_GETFL = 3;
        private const int F_SETFL = 4;
        private const int F_NOCACHE_MACOS = 48;

        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int Open(string path, int flags, int mode);

        [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static partial int Fcntl(int fd, int cmd, int arg);

        /// <summary>
        /// Open <paramref name="path"/> for writing, preferring O_DIRECT on Linux.
        /// <paramref name="directIo"/> reports whether direct I/O is actually active.
        /// </summary>
        public static SafeFileHandle OpenForWrite(string path, out bool directIo) {
            directIo = false;
            if (OperatingSystem.IsLinux()) {
                var fd = Open(path, O_WRONLY | O_CREAT_LINUX | O_TRUNC_LINUX | O_DIRECT_LINUX, 0x1A4 /* 0644 */);
                if (fd >= 0) {
                    directIo = true;
                    return new SafeFileHandle(fd, ownsHandle: true);
                }
                // EINVAL = filesystem refuses O_DIRECT (tmpfs); fall through to buffered.
            }
            var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None);
            if (OperatingSystem.IsMacOS()) {
                _ = Fcntl((int)handle.DangerousGetHandle(), F_NOCACHE_MACOS, 1);
            }
            return handle;
        }

        /// <summary>Drop O_DIRECT so unaligned tail writes (trailer, header patch) work.</summary>
        public static void ClearDirect(SafeFileHandle handle) {
            if (!OperatingSystem.IsLinux()) {
                return;
            }
            var fd = (int)handle.DangerousGetHandle();
            var flags = Fcntl(fd, F_GETFL, 0);
            if (flags >= 0) {
                _ = Fcntl(fd, F_SETFL, flags & ~O_DIRECT_LINUX);
            }
        }
    }
}
