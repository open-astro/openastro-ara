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
    bool Recommended = false,
    // #1136 — the directory the installed files sit in (null when not installed). For a solver
    // package this can be an earlier index path the profile no longer points at: the files are
    // still listed and removable, but the solver does not read them until the path matches.
    string? InstallPath = null);

/// <summary>POST /api/v1/data-manager/solver-recommendation body (§36.10, #1136): the rig's optics as the
/// setup wizard holds them before the profile is saved. Every field is optional; a missing main sensor or
/// pixel size falls back to the active profile's optics, a missing guide setup to its guider settings. The
/// guide sensor size is known only to the guider (its frame size), so the client passes it when it has it.</summary>
public sealed record SolverRecommendationRequestDto(
    double? FocalLengthMm = null,
    double? ReducerFactor = null,
    int? SensorWidthPx = null,
    int? SensorHeightPx = null,
    double? PixelSizeUm = null,
    double? GuideFocalLengthMm = null,
    double? GuidePixelSizeUm = null,
    int? GuideSensorWidthPx = null,
    int? GuideSensorHeightPx = null,
    // "oag" | "guide_scope" (§63.19). With an OAG the guide camera sits behind the main optics, so its
    // field uses the telescope's focal length × reducer and GuideFocalLengthMm is ignored; null falls
    // back to the profile's guider setup type.
    string? GuideSetupType = null);

/// <summary>One camera's place on the §36.10 ladder: its field, the band, and the database that covers it
/// (null <c>PackageId</c> below the solver's floor, <c>TooNarrow</c>). Sizes are the catalog's advertised
/// on-disk bytes; <c>IsInstalled</c> says whether the Data Manager already holds it (another profile may
/// have fetched it).</summary>
public sealed record SolverDatabasePickDto(
    string Role,            // "camera" | "guide camera"
    double FovDeg,
    string Band,
    string? PackageId,
    string? PackageName,
    long SizeBytes,
    bool IsInstalled,
    bool TooNarrow,
    string Reason);

/// <summary>The §36.10 ladder's answer for a rig: one pick per camera whose field is known (the guide
/// camera solves polar-alignment frames, so it needs a database too), deduplicated when both fall in the
/// same band. <c>Packages</c> is empty when no field is known. The narrowest field and its band are the
/// headline the wizard shows.</summary>
public sealed record SolverRecommendationDto(
    double? MainFovDeg,
    double? GuideFovDeg,
    double? NarrowestFovDeg,
    string Band,
    System.Collections.Generic.IReadOnlyList<SolverDatabasePickDto> Packages,
    bool TooNarrow,
    string Reason);

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
    DateTimeOffset? CompletedUtc,
    // §54.3 — the sharing mode the bundle was built with: "full" | "coarse_gps" | "redacted".
    // Optional default keeps the pre-#1185 shape deserializing.
    string SharingMode = "full");

/// <summary>POST /api/v1/bugreport/prepare body (§54.3): the sharing mode. Absent body or mode = "full".</summary>
public sealed record BugReportPrepareRequestDto(string? Mode = null);

// ─── Full backup (ZIP / restore) (§43) ──────────────────────────────────────

public sealed record BackupZipDto(
    Guid BackupId,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string Sha256,
    Uri DownloadUrl,
    IReadOnlyList<string> IncludedAreas,
    // §43.7 — frame records inside the snapshot's frames_metadata area; null on a
    // config-only backup (or a pre-§43-2b(c) manifest). Additive-optional-last.
    long? FramesMetadataRows = null);

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