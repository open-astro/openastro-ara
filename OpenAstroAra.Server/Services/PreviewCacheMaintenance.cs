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
/// §65.4 preview-cache maintenance: measure or delete the sidecar JPEGs
/// (<c>*.thumb.jpg</c> thumbnails + <c>*.preview.*.jpg</c> stretch variants)
/// beside the FITS files under the save directory. Everything here is
/// recoverable cache — thumbnails and default previews re-render via the
/// boot-time warmer, anything else on demand — so deletion is always safe;
/// it just trades disk back for re-render time.
/// </summary>
public static class PreviewCacheMaintenance {
    // Same walk posture as the §28.8 scan: never die on a directory the
    // daemon can't read (every ext4 root has a root-owned lost+found).
    private static readonly EnumerationOptions Walk = new() {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static StorageCacheDto Measure(string saveDirectory) =>
        Sweep(saveDirectory, delete: false);

    public static StorageCacheDto Clear(string saveDirectory) =>
        Sweep(saveDirectory, delete: true);

    private static StorageCacheDto Sweep(string saveDirectory, bool delete) {
        if (string.IsNullOrEmpty(saveDirectory) || !Directory.Exists(saveDirectory)) {
            return new StorageCacheDto(0, 0);
        }
        var files = 0;
        var bytes = 0L;
        IEnumerable<string> candidates;
        try {
            candidates = Directory.EnumerateFiles(saveDirectory, "*.jpg", Walk);
        } catch (DirectoryNotFoundException) {
            return new StorageCacheDto(0, 0);
        }
        foreach (var path in candidates) {
            if (!IsCacheSidecar(path)) continue;
            try {
                var size = new FileInfo(path).Length;
                if (delete) File.Delete(path);
                files++;
                bytes += size;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Best-effort: an in-flight render holding a handle, or a
                // read-only file, must not fail the whole sweep.
            }
        }
        return new StorageCacheDto(files, bytes);
    }

    /// <summary>Only the cache naming from §65.4 — never a user's own JPEGs
    /// that happen to live in the captures tree.</summary>
    internal static bool IsCacheSidecar(string path) {
        var name = Path.GetFileName(path);
        return name.EndsWith(".thumb.jpg", StringComparison.OrdinalIgnoreCase)
            || (name.Contains(".preview.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
    }
}
