#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §29.1 real storage probe. Replaces the Storage panel's hard-coded
/// placeholders: resolves which mounted filesystem the save directory lives
/// on, its true total/free bytes, and whether that mount shares a physical
/// disk with the OS root (the Pi's SD card — §29 warns against capturing
/// there). Linux reads /proc/mounts for device paths; other OSes (macOS dev
/// runs) fall back to DriveInfo alone.
/// </summary>
public static class StorageStatusService {
    // Real data-bearing filesystems only — pseudo (proc/sys/tmpfs/overlay)
    // and network mounts are not capture targets.
    private static readonly HashSet<string> DataFilesystems = new(StringComparer.OrdinalIgnoreCase) {
        "ext4", "ext3", "ext2", "xfs", "btrfs", "f2fs", "vfat", "exfat", "ntfs", "apfs", "hfs",
    };

    public static StorageStatusDto Get(string saveDirectory) {
        var mounts = ListMounts();
        var dirExists = !string.IsNullOrEmpty(saveDirectory) && Directory.Exists(saveDirectory);

        // The save dir's mount = the longest mount-point prefix of its path.
        (string MountPoint, string Device, string Filesystem)? saveMount = null;
        if (dirExists) {
            var full = Path.GetFullPath(saveDirectory);
            saveMount = mounts
                .Where(m => full.Equals(m.MountPoint, StringComparison.Ordinal)
                    || full.StartsWith(m.MountPoint.EndsWith('/') ? m.MountPoint : m.MountPoint + "/", StringComparison.Ordinal))
                .OrderByDescending(m => m.MountPoint.Length)
                .Select(m => ((string, string, string)?)m)
                .FirstOrDefault();
        }

        var rootDisk = mounts.Where(m => m.MountPoint == "/")
            .Select(m => ParentDisk(m.Device)).FirstOrDefault();

        var drives = new List<StorageDriveDto>();
        foreach (var m in mounts) {
            // /boot partitions are the root disk's plumbing, not candidates.
            if (m.MountPoint.StartsWith("/boot", StringComparison.Ordinal)) continue;
            var (total, free) = ProbeSpace(m.MountPoint);
            if (total <= 0) continue;
            drives.Add(new StorageDriveDto(
                Device: m.Device,
                MountPoint: m.MountPoint,
                Filesystem: m.Filesystem,
                TotalBytes: total,
                FreeBytes: free,
                IsRootDevice: rootDisk is not null && ParentDisk(m.Device) == rootDisk,
                IsSaveTarget: saveMount is { } sm && sm.MountPoint == m.MountPoint));
        }

        var (saveTotal, saveFree) = dirExists ? ProbeSpace(saveDirectory) : (0L, 0L);
        return new StorageStatusDto(
            SaveDirectory: saveDirectory,
            SaveDirectoryExists: dirExists,
            MountPoint: saveMount?.MountPoint,
            Device: saveMount?.Device,
            Filesystem: saveMount?.Filesystem,
            TotalBytes: saveTotal,
            FreeBytes: saveFree,
            OnRootDevice: saveMount is { } s && rootDisk is not null && ParentDisk(s.Device) == rootDisk,
            Drives: drives);
    }

    private static (long Total, long Free) ProbeSpace(string path) {
        try {
            var info = new DriveInfo(path);
            return info.IsReady ? (info.TotalSize, info.AvailableFreeSpace) : (0, 0);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return (0, 0);
        }
    }

    /// <summary>/dev/mmcblk0p2 → mmcblk0; /dev/sda1 → sda; /dev/nvme0n1p3 →
    /// nvme0n1. Non-/dev sources (macOS DriveInfo names) pass through.</summary>
    internal static string ParentDisk(string device) {
        var name = device.StartsWith("/dev/", StringComparison.Ordinal) ? device[5..] : device;
        // mmcblk0p2 / nvme0n1p3 — strip a trailing pN partition suffix.
        var pIdx = name.LastIndexOf('p');
        if (pIdx > 0 && pIdx < name.Length - 1 && name[(pIdx + 1)..].All(char.IsAsciiDigit)
            && char.IsAsciiDigit(name[pIdx - 1])) {
            return name[..pIdx];
        }
        // sda1 / sdb12 — strip trailing digits when the stem ends in a letter.
        var end = name.Length;
        while (end > 0 && char.IsAsciiDigit(name[end - 1])) end--;
        return end > 0 && !char.IsAsciiDigit(name[end - 1]) ? name[..end] : name;
    }

    private static List<(string MountPoint, string Device, string Filesystem)> ListMounts() {
        var mounts = new List<(string, string, string)>();
        if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts")) {
            try {
                foreach (var line in File.ReadAllLines("/proc/mounts")) {
                    var parts = line.Split(' ');
                    if (parts.Length < 3) continue;
                    var device = parts[0];
                    var mountPoint = parts[1].Replace("\\040", " ", StringComparison.Ordinal);
                    var fs = parts[2];
                    if (!device.StartsWith("/dev/", StringComparison.Ordinal)) continue;
                    if (!DataFilesystems.Contains(fs)) continue;
                    mounts.Add((mountPoint, device, fs));
                }
                return mounts;
            } catch (IOException) {
                // fall through to DriveInfo
            }
        }
        // macOS/dev fallback: DriveInfo gives mount + fs but no device path.
        foreach (var d in DriveInfo.GetDrives()) {
            try {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!DataFilesystems.Contains(d.DriveFormat)) continue;
                mounts.Add((d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } p ? p : "/",
                    d.Name, d.DriveFormat));
            } catch (IOException) { /* transient mount — skip */ } catch (UnauthorizedAccessException) { /* skip */ }
        }
        return mounts;
    }
}
