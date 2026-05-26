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
using Microsoft.AspNetCore.Routing;

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
        bug.MapPost("/prepare", () => NotImplementedStub("POST /api/v1/bugreport/prepare", "§54"));
        bug.MapGet("/download", () => NotImplementedStub("GET /api/v1/bugreport/download", "§54"));

        // ─── Data Manager (§36.2) ───
        var data = app.MapGroup("/api/v1/data-manager").WithTags("DataManager");
        data.MapGet("/packages", () => NotImplementedStub("GET /api/v1/data-manager/packages", "§36.2"));
        data.MapPost("/download", () => NotImplementedStub("POST /api/v1/data-manager/download", "§36.2"));
        data.MapPost("/cancel/{downloadId:guid}", (Guid downloadId) =>
            NotImplementedStub("POST /api/v1/data-manager/cancel/{downloadId}", "§36.2"));
        data.MapDelete("/{packageId}", (string packageId) =>
            NotImplementedStub("DELETE /api/v1/data-manager/{packageId}", "§36.2"));
        data.MapGet("/state", () => NotImplementedStub("GET /api/v1/data-manager/state", "§36.2"));

        // ─── Backup (§43) ───
        var backup = app.MapGroup("/api/v1/backup").WithTags("Backup");
        backup.MapPost("/create-zip", () => NotImplementedStub("POST /api/v1/backup/create-zip", "§43"));
        backup.MapPost("/restore-zip", () => NotImplementedStub("POST /api/v1/backup/restore-zip", "§43"));
        backup.MapGet("/snapshots", () => NotImplementedStub("GET /api/v1/backup/snapshots", "§43"));
        backup.MapGet("/clone-status", () => NotImplementedStub("GET /api/v1/backup/clone-status", "§43"));

        // ─── Profile sharing (§70) ───
        var profiles = app.MapGroup("/api/v1/profiles").WithTags("ProfileShare");
        profiles.MapPost("/{id:guid}/share-export", (Guid id) =>
            NotImplementedStub("POST /api/v1/profiles/{id}/share-export", "§70"));
        profiles.MapPost("/share-import", () => NotImplementedStub("POST /api/v1/profiles/share-import", "§70"));
        profiles.MapPost("/share-import/commit", () => NotImplementedStub("POST /api/v1/profiles/share-import/commit", "§70"));
        profiles.MapGet("/{id:guid}/sky-data-recommendations", (Guid id) =>
            NotImplementedStub("GET /api/v1/profiles/{id}/sky-data-recommendations", "§36.2"));

        return app;
    }
}
