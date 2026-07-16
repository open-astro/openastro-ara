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
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Server.Services;

public interface IStorageBrowseService {
    /// <summary>One directory level at <paramref name="path"/> (null/blank → the
    /// curated roots). Throws <see cref="DirectoryNotFoundException"/> for a
    /// missing/non-directory path and <see cref="UnauthorizedAccessException"/>
    /// when the daemon user can't list it — the endpoint maps both to Problems.</summary>
    Contracts.StorageBrowseDto Browse(string? path);
}

/// <summary>
/// §37.4/§29 — directory listing for the client's save-folder picker. The
/// daemon runs on the user's own Pi/SBC and its REST surface is LAN-trusted
/// (same trust level as the file-saving path the directory is FOR), so this
/// lists directories only, never file contents. Hidden (dot) directories and
/// virtual filesystems (/proc, /sys, /dev, /run) are skipped — nothing there
/// is a sane place for FITS frames.
/// </summary>
public sealed class StorageBrowseService : IStorageBrowseService {

    private static readonly string[] VirtualRoots = ["/proc", "/sys", "/dev", "/run"];

    public Contracts.StorageBrowseDto Browse(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return Roots();
        }
        var full = Path.GetFullPath(path);
        if (VirtualRoots.Any(v => full == v || full.StartsWith(v + '/', StringComparison.Ordinal))) {
            throw new DirectoryNotFoundException($"'{full}' is a virtual filesystem, not storage.");
        }
        if (!Directory.Exists(full)) {
            throw new DirectoryNotFoundException($"'{full}' does not exist or is not a directory.");
        }
        var dirs = new List<Contracts.StorageBrowseEntryDto>();
        // EnumerateDirectories throws UnauthorizedAccessException for the listing
        // itself (endpoint maps it); per-child probes below are best-effort.
        foreach (var child in Directory.EnumerateDirectories(full).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.')) {
                continue; // hidden — never a sane save target
            }
            dirs.Add(new Contracts.StorageBrowseEntryDto(name, child, Removable: IsUnderMediaRoot(child)));
        }
        var parent = Path.GetDirectoryName(full);
        return new Contracts.StorageBrowseDto(full, parent, Writable: IsWritable(full), dirs);
    }

    private static Contracts.StorageBrowseDto Roots() {
        // Curated entry points, existing ones only: the user's home (frames on
        // the system disk), the removable-mount roots (§29 recommends USB), and
        // finally / for anything else. macOS dev boxes get /Volumes.
        var candidates = new (string Name, string Path, bool Removable)[] {
            ("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false),
            ("USB / removable (media)", "/media", true),
            ("Mounts (mnt)", "/mnt", true),
            ("Volumes", "/Volumes", true),
            ("System root", "/", false),
        };
        var dirs = candidates
            .Where(c => !string.IsNullOrEmpty(c.Path) && Directory.Exists(c.Path))
            .Select(c => new Contracts.StorageBrowseEntryDto(c.Name, c.Path, c.Removable))
            .ToList();
        return new Contracts.StorageBrowseDto(Path: "", Parent: null, Writable: false, Dirs: dirs);
    }

    private static bool IsUnderMediaRoot(string path) =>
        path.StartsWith("/media/", StringComparison.Ordinal) ||
        path.StartsWith("/mnt/", StringComparison.Ordinal) ||
        path.StartsWith("/Volumes/", StringComparison.Ordinal) ||
        path is "/media" or "/mnt" or "/Volumes";

    private static bool IsWritable(string dir) {
        // A real probe beats permission-bit archaeology (ACLs, mount ro flags):
        // create-and-delete a zero-byte temp file. Failure of any kind = not
        // writable for our purposes.
        try {
            var probe = Path.Combine(dir, $".ara-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            return false;
        }
    }
}
