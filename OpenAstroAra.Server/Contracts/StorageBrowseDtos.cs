#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §37.4/§29 — <c>GET /api/v1/storage/browse[?path=…]</c>: one directory level
/// of the SERVER's filesystem, so the client's save-directory picker can walk
/// to an internal folder or a mounted USB drive without the user typing paths
/// blind. No <c>path</c> → the curated roots (home, /media, /mnt, /) instead
/// of a raw <c>/</c> listing. Directories only — frames are saved into a
/// directory, never a file.
/// </summary>
public sealed record StorageBrowseDto(
    string Path,
    string? Parent,
    bool Writable,
    IReadOnlyList<StorageBrowseEntryDto> Dirs);

/// <summary>One child directory. <c>Removable</c> marks /media//mnt-style
/// mounts so the picker can badge USB drives (§29 recommends them).</summary>
public sealed record StorageBrowseEntryDto(
    string Name,
    string Path,
    bool Removable = false);

/// <summary><c>POST /api/v1/storage/mkdir</c> — create <c>Name</c> as a child
/// directory of <c>Path</c> (a plain folder name, no separators). Responds with
/// the refreshed listing of <c>Path</c> so the picker can show the new folder
/// without a second round-trip.</summary>
public sealed record StorageCreateFolderRequestDto(
    string Path,
    string Name);

/// <summary>One real mounted filesystem a capture target could live on
/// (§29.1). <c>IsRootDevice</c> means the mount lives on the same physical
/// disk as the OS root — the SD card on a Pi — which §29 warns against for
/// capture storage.</summary>
public sealed record StorageDriveDto(
    string Device,
    string MountPoint,
    string Filesystem,
    long TotalBytes,
    long FreeBytes,
    bool IsRootDevice,
    bool IsSaveTarget);

/// <summary>
/// §29.1 — <c>GET /api/v1/storage/status</c>: where the configured save
/// directory actually lives (device, filesystem, REAL free space) plus every
/// candidate drive, so the Storage panel can show truth instead of
/// placeholders and warn only when capture genuinely targets the SD card.
/// </summary>
public sealed record StorageStatusDto(
    string SaveDirectory,
    bool SaveDirectoryExists,
    string? MountPoint,
    string? Device,
    string? Filesystem,
    long TotalBytes,
    long FreeBytes,
    bool OnRootDevice,
    IReadOnlyList<StorageDriveDto> Drives);
