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
// PORT_PLAYBOOK.md §10.9 + §36.2 (Data Manager sky data) + §43 (backup) +
// §54 (bug report) + §70 (sharing)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>GET /api/v1/data-manager/packages — available + installed sky-data packages.</summary>
public sealed record DataPackageDto(
    string Id,
    string Name,
    string Description,
    string Category,
    long SizeBytes,
    string Version,
    bool IsInstalled,
    DateTimeOffset? InstalledUtc,
    string? SourceUrl);

/// <summary>POST /api/v1/data-manager/download body.</summary>
public sealed record DownloadRequestDto(
    string PackageId,
    bool ForceReinstall);

/// <summary>GET /api/v1/data-manager/state — global state.</summary>
public sealed record DataManagerStateDto(
    int InstalledPackageCount,
    long TotalInstalledBytes,
    IReadOnlyList<DataManagerActiveDownloadDto> ActiveDownloads,
    DateTimeOffset? LastSyncUtc);

public sealed record DataManagerActiveDownloadDto(
    Guid DownloadId,
    string PackageId,
    long DownloadedBytes,
    long TotalBytes,
    double PercentComplete);

// ─── Bug report (§54) ───────────────────────────────────────────────────────

public sealed record BugReportPreparationDto(
    Guid PreparationId,
    string Status,
    long EstimatedSizeBytes,
    string? DownloadUrl,
    DateTimeOffset? CompletedUtc);

// ─── Full backup (ZIP / restore) (§43) ──────────────────────────────────────

public sealed record BackupZipDto(
    Guid BackupId,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string Sha256,
    string DownloadUrl,
    IReadOnlyList<string> IncludedAreas);

public sealed record RestoreRequestDto(
    string BackupSourceUrl,
    bool RestoreSequences,
    bool RestoreProfiles,
    bool RestoreFrameMetadata,
    bool RestoreLogs);

// ─── Profile share export / import (§70) ────────────────────────────────────

public sealed record ProfileShareDto(
    Guid ProfileId,
    string ProfileName,
    System.Text.Json.JsonElement Manifest,
    long PayloadBytes,
    string DownloadUrl);

public sealed record ProfileShareImportPreviewDto(
    Guid ImportToken,
    string ProfileName,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DroppedFields,
    DateTimeOffset ExpiresUtc);