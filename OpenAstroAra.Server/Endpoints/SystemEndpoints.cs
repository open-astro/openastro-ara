#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 9 bug-report, data manager, backup, share endpoints per PORT_PLAYBOOK.md §10.9.
/// </summary>
public static class SystemEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 9's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app) {
        // ─── Bug report (§54) ───
        var bug = app.MapGroup("/api/v1/bugreport").WithTags("BugReport");

        // Phase 13.9 — wired to IBugReportService (placeholder).
        bug.MapPost("/prepare",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBugReportService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.PrepareAsync(idempotencyKey, ct)))
           .Produces<BugReportPreparationDto>(StatusCodes.Status202Accepted)
           .WithName("PrepareBugReport");

        bug.MapGet("/download",
                async (Guid preparationId, IBugReportService svc, CancellationToken ct) => {
                    var result = await svc.OpenDownloadAsync(preparationId, ct);
                    if (result is null) return Results.NotFound();
                    return Results.Stream(result.Value.Stream, "application/zip", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DownloadBugReport");

        // ─── Data Manager (§36.2) — Phase 13.10 wired to IDataManagerService ───
        var data = app.MapGroup("/api/v1/data-manager").WithTags("DataManager");

        data.MapGet("/packages",
                async (IDataManagerService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListPackagesAsync(ct)))
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .WithName("ListDataPackages");

        data.MapPost("/download",
                async ([FromBody] DownloadRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IDataManagerService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.DownloadAsync(request, idempotencyKey, ct));
                    } catch (PackageNotFoundException ex) {
                        // §36: PackageId isn't in the curated catalog. A dedicated type (not a bare
                        // KeyNotFoundException) means an unrelated dictionary miss inside the §36-2 engine
                        // can't be silently turned into a 404 here.
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    } catch (PackageAlreadyInstalledException ex) {
                        // §36: a non-force request for an already-installed package — it did NOT re-download.
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                    }
                })
            .Accepts<DownloadRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("StartDataPackageDownload");

        data.MapPost("/cancel/{downloadId:guid}",
                async (Guid downloadId, IDataManagerService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.CancelAsync(downloadId, ct));
                    } catch (DownloadNotFoundException ex) {
                        // §36-2: the download id isn't an in-flight job (unknown or already finished).
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    }
                })
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CancelDataPackageDownload");

        data.MapDelete("/{packageId}",
                async (string packageId, IDataManagerService svc, CancellationToken ct) => {
                    var ok = await svc.DeleteAsync(packageId, ct);
                    return ok ? Results.NoContent() : Results.NotFound();
                })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DeleteDataPackage");

        data.MapGet("/state",
                async (IDataManagerService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetStateAsync(ct)))
            .Produces<DataManagerStateDto>(StatusCodes.Status200OK)
            .WithName("GetDataManagerState");

        // ─── Planning (§36/§25.5) — Tonight's Sky ───
        var planning = app.MapGroup("/api/v1/planning").WithTags("Planning");
        planning.MapGet("/tonight",
                (ITonightSkyService svc, [FromQuery] int? limit, [FromQuery(Name = "at")] string? at) => {
                    // `at` (ISO 8601) is optional — mainly for a deterministic query; default is "now".
                    var atUtc = System.DateTimeOffset.TryParse(at,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsed)
                        ? parsed
                        : System.DateTimeOffset.UtcNow;
                    return Results.Ok(svc.GetTonight(atUtc, limit ?? 10));
                })
            .Produces<IReadOnlyList<TonightSkyObjectDto>>(StatusCodes.Status200OK)
            .WithName("GetTonightSky");

        // ─── Backup (§43) — Phase 13.11 wired to IBackupService ───
        var backup = app.MapGroup("/api/v1/backup").WithTags("Backup");

        backup.MapPost("/create-zip",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBackupService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.CreateZipAsync(idempotencyKey, ct));
                    } catch (BackupNothingToArchiveException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                })
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("CreateBackupZip");

        backup.MapPost("/restore-zip",
                async ([FromBody] RestoreRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IBackupService svc, CancellationToken ct) => {
                    try {
                        return Results.Accepted(value: await svc.RestoreZipAsync(request, idempotencyKey, ct));
                    } catch (BackupSnapshotNotFoundException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                    } catch (BackupRestoreSourceUnsupportedException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupRestoreNoAreaSelectedException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupCorruptException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    } catch (BackupRestoreInProgressException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                    }
                })
            .Accepts<RestoreRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("RestoreBackupZip");

        backup.MapGet("/snapshots",
                async (IBackupService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListSnapshotsAsync(ct)))
              .Produces<IReadOnlyList<BackupZipDto>>(StatusCodes.Status200OK)
              .WithName("ListBackupSnapshots");

        backup.MapGet("/clone-status",
                async (IBackupService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetCloneStatusAsync(ct)))
              .Produces<System.Text.Json.JsonElement>(StatusCodes.Status200OK)
              .WithName("GetBackupCloneStatus");

        backup.MapGet("/snapshot/{id:guid}/download",
                async (Guid id, IBackupService svc, CancellationToken ct) => {
                    var snapshot = await svc.OpenSnapshotAsync(id, ct);
                    return snapshot is null
                        ? Results.NotFound()
                        : Results.File(snapshot.Value.Stream, "application/zip", snapshot.Value.FileName);
                })
              .Produces(StatusCodes.Status200OK, contentType: "application/zip")
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("DownloadBackupSnapshot");

        // ─── Profile sharing (§70) — Phase 13.10 wired to IProfileShareService ───
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("ProfileShare");

        profiles.MapPost("/{id:guid}/share-export",
                async (Guid id, IProfileShareService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ExportAsync(id, ct)))
            .Produces<ProfileShareDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ExportProfileShare");

        profiles.MapPost("/share-import",
                async ([FromBody] System.Text.Json.JsonElement manifest, IProfileShareService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ImportPreviewAsync(manifest, ct)))
            .Accepts<System.Text.Json.JsonElement>("application/json")
            .Produces<ProfileShareImportPreviewDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("PreviewProfileShareImport");

        profiles.MapPost("/share-import/commit",
                async (Guid importToken, IProfileShareService svc, CancellationToken ct) => {
                    // Body must be the new profile's GUID so WILMA can
                    // navigate to it; the import token has already done
                    // its job at this point.
                    var newProfileId = await svc.ImportCommitAsync(importToken, ct);
                    return Results.Created($"/api/v1/profiles/{newProfileId}", value: newProfileId);
                })
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CommitProfileShareImport");

        // Phase 13.16 — sky-data-recommendations wired to IDataManagerService.
        // Recommends the not-installed packages from the catalog; real impl
        // ranks them by relevance to the profile's site latitude + target
        // catalog. Placeholder returns the not-installed entries directly.
        profiles.MapGet("/{id:guid}/sky-data-recommendations",
                async (Guid id, IDataManagerService svc, CancellationToken ct) => {
                    var packages = await svc.ListPackagesAsync(ct);
                    var recommended = packages.Where(p => !p.IsInstalled).ToList();
                    return Results.Ok((IReadOnlyList<DataPackageDto>)recommended);
                })
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetProfileSkyDataRecommendations");

        return app;
    }
}