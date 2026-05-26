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

        bug.MapPost("/prepare", () => NotImplementedStub("POST /api/v1/bugreport/prepare", "§54"))
           .Produces<BugReportPreparationDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("PrepareBugReport");

        bug.MapGet("/download",
                (Guid preparationId) => NotImplementedStub("GET /api/v1/bugreport/download", "§54"))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("DownloadBugReport");

        // ─── Data Manager (§36.2) ───
        var data = app.MapGroup("/api/v1/data-manager").WithTags("DataManager");

        data.MapGet("/packages", () => NotImplementedStub("GET /api/v1/data-manager/packages", "§36.2"))
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ListDataPackages");

        data.MapPost("/download", ([FromBody] DownloadRequestDto request) =>
                NotImplementedStub("POST /api/v1/data-manager/download", "§36.2"))
            .Accepts<DownloadRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("StartDataPackageDownload");

        data.MapPost("/cancel/{downloadId:guid}", (Guid downloadId) =>
                NotImplementedStub("POST /api/v1/data-manager/cancel/{downloadId}", "§36.2"))
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("CancelDataPackageDownload");

        data.MapDelete("/{packageId}", (string packageId) =>
                NotImplementedStub("DELETE /api/v1/data-manager/{packageId}", "§36.2"))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("DeleteDataPackage");

        data.MapGet("/state", () => NotImplementedStub("GET /api/v1/data-manager/state", "§36.2"))
            .Produces<DataManagerStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetDataManagerState");

        // ─── Backup (§43) ───
        var backup = app.MapGroup("/api/v1/backup").WithTags("Backup");

        backup.MapPost("/create-zip", () => NotImplementedStub("POST /api/v1/backup/create-zip", "§43"))
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("CreateBackupZip");

        backup.MapPost("/restore-zip", ([FromBody] RestoreRequestDto request) =>
                NotImplementedStub("POST /api/v1/backup/restore-zip", "§43"))
            .Accepts<RestoreRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("RestoreBackupZip");

        backup.MapGet("/snapshots", () => NotImplementedStub("GET /api/v1/backup/snapshots", "§43"))
              .Produces<IReadOnlyList<BackupZipDto>>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("ListBackupSnapshots");

        backup.MapGet("/clone-status", () => NotImplementedStub("GET /api/v1/backup/clone-status", "§43"))
              .Produces<System.Text.Json.JsonElement>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("GetBackupCloneStatus");

        // ─── Profile sharing (§70) ───
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("ProfileShare");

        profiles.MapPost("/{id:guid}/share-export", (Guid id) =>
                NotImplementedStub("POST /api/v1/profiles/{id}/share-export", "§70"))
            .Produces<ProfileShareDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ExportProfileShare");

        profiles.MapPost("/share-import",
                ([FromBody] System.Text.Json.JsonElement manifest) =>
                    NotImplementedStub("POST /api/v1/profiles/share-import", "§70"))
            .Accepts<System.Text.Json.JsonElement>("application/json")
            .Produces<ProfileShareImportPreviewDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("PreviewProfileShareImport");

        profiles.MapPost("/share-import/commit",
                (Guid importToken) =>
                    NotImplementedStub("POST /api/v1/profiles/share-import/commit", "§70"))
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("CommitProfileShareImport");

        profiles.MapGet("/{id:guid}/sky-data-recommendations", (Guid id) =>
                NotImplementedStub("GET /api/v1/profiles/{id}/sky-data-recommendations", "§36.2"))
            .Produces<IReadOnlyList<DataPackageDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetProfileSkyDataRecommendations");

        return app;
    }
}
