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

namespace OpenAstroAra.Server.Contracts;

/// <summary><c>POST /api/v1/storage/copy</c> — copy the store onto another drive.
/// <c>Folders</c> are paths on the store (relative to it, or absolute under it, as
/// the browse picker hands them out); null or empty copies the whole store.</summary>
public sealed record StorageCopyRequestDto(
    string TargetUuid,
    IReadOnlyList<string>? Folders = null);

/// <summary><c>GET /api/v1/storage/copy</c> — the current (or last) disk-to-disk copy.
/// <c>State</c>: idle · preparing (mounting the target, listing files) · copying ·
/// done · cancelled · failed. A file already on the target with the same size is
/// skipped; one with a different size is a conflict and is left alone (copy never
/// overwrites or deletes). <c>TargetReleased</c> is true once the target drive was
/// flushed and unmounted, i.e. it is safe to unplug.</summary>
public sealed record StorageCopyStatusDto(
    string State,
    string? TargetUuid,
    IReadOnlyList<string> Folders,
    int FilesToCopy,
    int FilesCopied,
    int FilesSkipped,
    int FilesConflicting,
    int FilesFailed,
    long BytesToCopy,
    long BytesCopied,
    string? CurrentFile,
    string? ErrorCode,
    string? ErrorDetail,
    string? FirstFailure,
    bool TargetReleased,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc);
