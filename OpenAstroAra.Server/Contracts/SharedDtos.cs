#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// Cross-cutting DTOs used by multiple endpoint groups. Created in Phase 7
// (sequence + calibration + mosaic scaffold) and reused from Phase 8 onward.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Cursor-paginated response wrapper per §60.2. <c>NextCursor</c> is null when there are no more pages.
/// </summary>
public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Long-running operation acceptance envelope per §60.5. Server returns 202 + this
/// payload; client subscribes to <c>operation.{op_id}</c> events on the WebSocket
/// channel for progress + terminal status.
/// </summary>
public sealed record OperationAcceptedDto(
    Guid OperationId,
    string OperationType,
    DateTimeOffset AcceptedUtc,
    string? IdempotencyKey);

/// <summary>
/// §65.5 batch-job state. Returned by GET /api/v1/jobs/{id}.
/// </summary>
public sealed record BatchJobDto(
    Guid JobId,
    string JobType,
    string State,
    int Done,
    int Total,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string? ErrorMessage);
/// <summary>§29 — real numbers for the storage panel: the volume behind the save
/// directory. Nulls when the volume is unreachable (unmounted USB store).</summary>
public sealed record StorageSpaceDto(
    string SaveDirectory,
    bool IsFallback,
    long? FreeBytes,
    long? TotalBytes);

/// <summary>§29.1.1 — a block device the daemon could use for ARA data.
/// System disks are listed but flagged so the UI can grey them out.</summary>
public sealed record StorageDeviceDto(
    string Path,
    string? Uuid,
    string? Label,
    string? Model,
    string? FileSystem,
    long? SizeBytes,
    string? MountPoint,
    bool Removable,
    string? Transport,
    bool IsSystemDisk,
    bool IsAraStore);

/// <summary>§29.1.1/§29.1.3 — mount a drive as the ARA store, optionally
/// reformatting it as ext4 first (destructive; the caller must echo the
/// drive's current label as confirmation).</summary>
public sealed record StorageConfigureRequestDto(
    string Uuid,
    bool Format = false,
    string? ConfirmLabel = null);

/// <summary>Result of a storage configure attempt. <c>code</c> is the helper's
/// machine-readable outcome (ok, not_ext4, label_mismatch, device_busy,
/// refused/system_disk, uuid_not_found, …) so the client can branch.</summary>
public sealed record StorageConfigureResultDto(
    bool Success,
    string Code,
    string? Detail,
    string? MountPoint,
    string? SaveDirectory);

/// <summary>Result of POST /api/v1/storage/rescan (§28.8 on demand).
/// <c>ran</c> is false when the save directory was missing or unwritable —
/// <c>skip_reason</c> then says which, so the client can explain rather than
/// report "0 frames found".</summary>
public sealed record StorageRescanResultDto(
    bool Ran,
    string? SkipReason,
    string SavePath,
    int TempFilesSwept,
    int FramesRecovered);
