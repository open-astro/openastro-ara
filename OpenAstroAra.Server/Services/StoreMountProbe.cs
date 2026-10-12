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
/// read from the daemon's OWN mount namespace (which is what its writes go
/// through).
/// <para>
/// A line at <c>/media/openastroara</c> is not proof of a disk (#1207): the
/// service unit lists the path under <c>ReadWritePaths=</c>, so when the
/// daemon starts while the directory exists but nothing is mounted there,
/// systemd bind-mounts the empty directory into the service namespace and
/// the daemon sees the ROOT filesystem at the store path. A real store is a
/// filesystem of its own.
/// </para>
/// <para>
/// <b>Preferred source: <c>/proc/self/mountinfo</c></b> (#1231). Its
/// <c>major:minor</c> field says "own filesystem" directly (a bind shares its
/// source's device number; a USB disk, or a btrfs subvolume, never does), and
/// its <c>root</c> field separates the systemd self-bind of the empty
/// directory (root = the store path inside the ancestor filesystem) from an
/// intentional <c>mount --bind /srv/frames /media/openastroara</c> (root =
/// <c>/srv/frames</c>), which IS a place to put frames and is honoured. A
/// device-number that no longer exists under <c>/sys/dev/block</c> is a disk
/// that was unplugged under a still-standing namespace-private bind: the
/// mount table says "mounted", the kernel says the disk is gone, and writes
/// would land on a detached filesystem — reported as ejected.
/// </para>
/// <para>
/// <b>Fallback: <c>/proc/self/mounts</c></b> (the #1207 rule): the store
/// line's source device name must differ from the nearest ancestor mount's
/// (<c>/media</c> if that is its own filesystem, else <c>/</c>). Used only
/// when mountinfo is unreadable.
/// </para>
/// </summary>
internal static class StoreMountProbe {

    public const string MountPoint = "/media/openastroara";

    /// <summary>Null when neither <c>/proc/self/mountinfo</c> nor
    /// <c>/proc/self/mounts</c> is readable — callers decide what "unknown"
    /// means for them (capture proceeds; the watcher skips the tick).</summary>
    public static bool? IsStoreMounted() {
        try {
            return IsStoreMountedFromMountInfo(File.ReadLines("/proc/self/mountinfo"), MountPoint, DeviceExists);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // fall through to the mounts table
        }
        try {
            return IsStoreMounted(File.ReadLines("/proc/self/mounts"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Does the kernel still have a block device with this
    /// <c>major:minor</c>? Null for device numbers that never appear under
    /// <c>/sys/dev/block</c> (major 0: tmpfs, overlay, btrfs subvolume
    /// anonymous devices) and when sysfs is unreadable — "unknown", which
    /// the rule treats as alive.</summary>
    internal static bool? DeviceExists(string majorMinor) {
        if (majorMinor.StartsWith("0:", StringComparison.Ordinal)) {
            return null;
        }
        try {
            // Directory.Exists answers false, not an exception, when sysfs is hidden or unreadable
            // (a container without /sys, a sandbox); without this check every real USB store would
            // read as ejected there. No /sys/dev/block at all = we cannot tell = alive.
            if (!Directory.Exists("/sys/dev/block")) {
                return null;
            }
            return Directory.Exists("/sys/dev/block/" + majorMinor);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>One parsed <c>mountinfo</c> line: <c>id parent major:minor root
    /// mountpoint options [optional…] - fstype source superopts</c>.</summary>
    internal readonly record struct MountInfoEntry(string MajorMinor, string Root, string MountPoint, string Source);

    /// <summary>Parse a <c>mountinfo</c> line; null for malformed lines. Paths
    /// carry octal escapes (<c>\040</c> for a space), which are decoded.</summary>
    internal static MountInfoEntry? ParseMountInfo(string line) {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 7) {
            return null;
        }
        var dash = Array.IndexOf(parts, "-", 6);
        var source = dash >= 0 && dash + 2 < parts.Length ? parts[dash + 2] : string.Empty;
        return new MountInfoEntry(parts[2], Unescape(parts[3]), Unescape(parts[4]), source);
    }

    private static string Unescape(string path) {
        if (!path.Contains('\\', StringComparison.Ordinal)) {
            return path;
        }
        var sb = new System.Text.StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++) {
            if (path[i] == '\\' && i + 3 < path.Length
                    && char.IsAsciiDigit(path[i + 1]) && char.IsAsciiDigit(path[i + 2]) && char.IsAsciiDigit(path[i + 3])) {
                sb.Append((char)Convert.ToInt32(path.Substring(i + 1, 3), 8));
                i += 3;
            } else {
                sb.Append(path[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Pure decision over <c>/proc/self/mountinfo</c> lines. The LAST
    /// entry for a path is the mount in effect (a replug stacks the disk over
    /// a stale bind), so later lines win. <paramref name="deviceExists"/>
    /// answers "is this major:minor still a block device?" (null = unknown).</summary>
    public static bool IsStoreMountedFromMountInfo(IEnumerable<string> mountInfoLines, string mountPoint = MountPoint,
            Func<string, bool?>? deviceExists = null) {
        MountInfoEntry? store = null;
        MountInfoEntry? ancestor = null;
        var ancestorLength = -1;
        foreach (var line in mountInfoLines) {
            if (ParseMountInfo(line) is not { } entry) {
                continue;
            }
            if (entry.MountPoint == mountPoint) {
                store = entry;
            } else if (IsAncestor(entry.MountPoint, mountPoint) && entry.MountPoint.Length >= ancestorLength) {
                ancestorLength = entry.MountPoint.Length;
                ancestor = entry;
            }
        }
        if (store is not { } s) {
            return false;
        }
        if (ancestor is not { } a) {
            // No ancestor in the table at all (not even "/") is not a shape a
            // Linux namespace produces; if it ever is, a line at the path is
            // the best evidence there is.
            return true;
        }
        if (s.MajorMinor == a.MajorMinor) {
            // Same filesystem as the ancestor: a bind. The systemd self-bind of
            // the empty directory has root = the store path as seen inside the
            // ancestor filesystem; any OTHER root is a bind of a different
            // directory, which is a deliberate place to keep frames.
            var relative = a.MountPoint == "/" ? mountPoint : mountPoint[a.MountPoint.Length..];
            var selfBindRoot = (a.Root == "/" ? string.Empty : a.Root.TrimEnd('/')) + relative;
            return s.Root != selfBindRoot;
        }
        // Its own filesystem — unless the disk behind it is gone (unplugged
        // under a namespace-private bind: the table still lists it).
        return deviceExists?.Invoke(s.MajorMinor) != false;
    }

    /// <summary>Pure decision over <c>/proc/self/mounts</c> lines
    /// (<c>source target fstype options dump pass</c>), the #1207 rule, kept as
    /// the fallback when mountinfo is unreadable. The LAST line for a path is
    /// the mount in effect, so later lines win.</summary>
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
        return ancestorSource is null || storeSource != ancestorSource;
    }

    private static bool IsAncestor(string target, string mountPoint) =>
        target == "/" ||
        (mountPoint.Length > target.Length &&
         mountPoint.StartsWith(target, StringComparison.Ordinal) &&
         mountPoint[target.Length] == '/');
}
