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
    Uri? SourceUrl,
    // §37.6 — curator flag: the wizard's sky-data screen pre-checks recommended
    // (not-yet-installed) packages so a fresh profile starts with the useful set
    // ticked. Optional default so pre-slice serialized shapes still deserialize.
    bool Recommended = false);

/// <summary>POST /api/v1/data-manager/download body.</summary>
/// <param name="PackageId">Catalog id of the package to download.</param>
/// <param name="ForceReinstall">When false, a request for a package that is already fully installed is a no-op —
/// the endpoint returns 409 Conflict instead of re-downloading. When true, the package is re-downloaded regardless.</param>
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

/// <summary>
/// GET /api/v1/data-manager/{packageId}/catalog — one row of an installed §36 sky-data catalog, normalized across the
/// per-package on-disk formats for the Sky Atlas overlay. <see cref="RaDeg"/>/<see cref="DecDeg"/> are decimal degrees
/// (ICRS); <see cref="Magnitude"/> is null when the source row didn't carry one.
/// </summary>
public sealed record CatalogObjectDto(
    string Name,
    double RaDeg,
    double DecDeg,
    double? Magnitude);

/// <summary>§36 Catalogs — one selectable catalog/filter the planetarium can overlay
/// (Messier, NGC, IC, or a type filter like "Galaxies"). <see cref="Group"/> buckets them
/// in the UI ("Catalogs", "Types", later "AL Programs").</summary>
public sealed record CatalogInfoDto(
    string Id,
    string Name,
    string Group);

/// <summary>
/// §36.8 / §55.1 Tonight's Sky — one OpenNGC deep-sky object with the full set of fields the
/// planner needs (size, position angle, surface brightness) beyond the slim overlay
/// <see cref="CatalogObjectDto"/>. <see cref="Name"/> is the catalog id (e.g. "NGC0224");
/// <see cref="CommonName"/> is the friendly name when OpenNGC carries one. <see cref="RaDeg"/>/
/// <see cref="DecDeg"/> are decimal degrees (J2000). All measured fields are null when the source
/// row didn't record them: <see cref="MajAxArcmin"/>/<see cref="MinAxArcmin"/> are the major/minor
/// axes in arcminutes, <see cref="PosAngleDeg"/> the position angle (deg), and
/// <see cref="SurfaceBrightness"/> is mag/arcsec².
/// </summary>
public sealed record DsoEntryDto(
    string Name,
    string? CommonName,
    string Type,
    double RaDeg,
    double DecDeg,
    double? Magnitude,
    double? MajAxArcmin,
    double? MinAxArcmin,
    double? PosAngleDeg,
    double? SurfaceBrightness);

// ─── Bug report (§54) ───────────────────────────────────────────────────────

public sealed record BugReportPreparationDto(
    Guid PreparationId,
    string Status,
    long EstimatedSizeBytes,
    Uri? DownloadUrl,
    DateTimeOffset? CompletedUtc);

// ─── Full backup (ZIP / restore) (§43) ──────────────────────────────────────

public sealed record BackupZipDto(
    Guid BackupId,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string Sha256,
    Uri DownloadUrl,
    IReadOnlyList<string> IncludedAreas);

public sealed record RestoreRequestDto(
    Uri BackupSourceUrl,
    bool RestoreSequences,
    bool RestoreProfiles,
    bool RestoreFrameMetadata,
    bool RestoreLogs,
    // §43-2b(b) — REQUIRED when BackupSourceUrl is a remote (absolute http/https)
    // source: the expected SHA-256 (64 hex chars) of the archive, carried
    // out-of-band exactly as the manifest-bypass note prescribed. The client
    // knows it from the remote daemon's snapshot listing (BackupZipDto.Sha256).
    // Ignored for local snapshots (their .meta.json manifest is the gate).
    string? Sha256 = null);

// ─── Profile share export / import (§70) ────────────────────────────────────

public sealed record ProfileShareDto(
    Guid ProfileId,
    string ProfileName,
    System.Text.Json.JsonElement Manifest,
    long PayloadBytes,
    // Null in v0.0.1: the share JSON is returned inline in Manifest (the client
    // writes it straight to the chosen file), so there is no payload route to GET.
    Uri? DownloadUrl);

public sealed record ProfileShareImportPreviewDto(
    Guid ImportToken,
    string ProfileName,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DroppedFields,
    DateTimeOffset ExpiresUtc);

// Token travels in the request body (not the query string) so it can't leak into
// web-server / proxy access logs — it authorizes profile creation within its TTL.
public sealed record ProfileShareImportCommitRequest(
    Guid ImportToken);