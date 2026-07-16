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
