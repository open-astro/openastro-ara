#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §29 — the one answer to "is a frames disk mounted at the store path?",
/// read from <c>/proc/self/mounts</c> (the daemon's OWN namespace, which is
/// what its writes go through).
/// <para>
/// A line at <c>/media/openastroara</c> is not proof of a disk (#1207): the
/// service unit lists the path under <c>ReadWritePaths=</c>, so when the
/// daemon starts while the directory exists but nothing is mounted there,
/// systemd bind-mounts the empty directory into the service namespace and
/// the daemon sees e.g. <c>/dev/vda1 /media/openastroara ext4</c> — the
/// ROOT filesystem's device. A real store is a filesystem of its own, so the
/// rule is: the store line's source device must differ from the source of
/// the nearest ancestor mount (<c>/media</c> if that is its own filesystem,
/// else <c>/</c>). A bind shares its source's device name; a USB disk never
/// does. This needs no fstab/blkid lookup (the fstab entry pins a UUID, the
/// mounts table shows a device node) and holds for exFAT and ext4 alike —
/// fstype cannot separate an ext4 store from an ext4 root bind.
/// </para>
/// </summary>
internal static class StoreMountProbe {

    public const string MountPoint = "/media/openastroara";

    /// <summary>Null when <c>/proc/self/mounts</c> is unreadable — callers
    /// decide what "unknown" means for them (capture proceeds; the watcher
    /// skips the tick).</summary>
    public static bool? IsStoreMounted() {
        try {
            return IsStoreMounted(File.ReadLines("/proc/self/mounts"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Pure decision over <c>/proc/self/mounts</c> lines
    /// (<c>source target fstype options dump pass</c>). The LAST line for a
    /// path is the mount in effect (a replug stacks the disk over a stale
    /// bind), so later lines win.</summary>
    public static bool IsStoreMounted(IEnumerable<string> mountLines, string mountPoint = MountPoint) {
        string? storeSource = null;
        string? ancestorSource = null;
        var ancestorLength = -1;
        foreach (var line in mountLines) {
            var parts = line.Split(' ');
            if (parts.Length < 2) {
                continue;
            }
            var source = parts[0];
            var target = parts[1];
            if (target == mountPoint) {
                storeSource = source;
            } else if (IsAncestor(target, mountPoint) && target.Length >= ancestorLength) {
                ancestorLength = target.Length;
                ancestorSource = source;
            }
        }
        if (storeSource is null) {
            return false;
        }
        // No ancestor in the table at all (not even "/") is not a shape a
        // Linux namespace produces; if it ever is, a line at the path is the
        // best evidence there is.
        return ancestorSource is null || storeSource != ancestorSource;
    }

    private static bool IsAncestor(string target, string mountPoint) =>
        target == "/" ||
        (mountPoint.Length > target.Length &&
         mountPoint.StartsWith(target, StringComparison.Ordinal) &&
         mountPoint[target.Length] == '/');
}
