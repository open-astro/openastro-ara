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
/// Phase 8 image / session / backup-stream endpoint registration per PORT_PLAYBOOK.md §10.8.
/// Every route declares its intended request + response DTOs so the generated OpenAPI
/// surface lists real schemas for WILMA client codegen, even while handlers return 501.
/// </summary>
public static class ImageEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 8's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app) {
        // ─── Frames (§40, §65) ───
        var frames = app.MapGroup("/api/v1/frames").WithTags("Frames");

        frames.MapGet("",
                (int? limit, string? cursor, Guid? sessionId, string? targetName) =>
                    NotImplementedStub("GET /api/v1/frames", "§40"))
            .Produces<CursorPage<FrameListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ListFrames");

        frames.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/frames/{id}", "§40"))
              .Produces<FrameDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("GetFrame");

        frames.MapPost("/{id:guid}/preview", (Guid id, [FromBody] FramePreviewRequestDto request) =>
                NotImplementedStub("POST /api/v1/frames/{id}/preview", "§65"))
            .Accepts<FramePreviewRequestDto>("application/json")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetFramePreview");

        frames.MapGet("/{id:guid}/thumbnail", (Guid id) =>
                NotImplementedStub("GET /api/v1/frames/{id}/thumbnail", "§65"))
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetFrameThumbnail");

        frames.MapGet("/{id:guid}/download", (Guid id) =>
                NotImplementedStub("GET /api/v1/frames/{id}/download", "§72"))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/fits")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("DownloadFrame");

        frames.MapPost("/bulk/rate", ([FromBody] BulkRateRequestDto request) =>
                NotImplementedStub("POST /api/v1/frames/bulk/rate", "§40.8"))
            .Accepts<BulkRateRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("BulkRateFrames");

        frames.MapPost("/bulk/tag", ([FromBody] BulkTagRequestDto request) =>
                NotImplementedStub("POST /api/v1/frames/bulk/tag", "§40.8"))
            .Accepts<BulkTagRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("BulkTagFrames");

        frames.MapPost("/bulk/delete", ([FromBody] BulkDeleteRequestDto request) =>
                NotImplementedStub("POST /api/v1/frames/bulk/delete", "§40.8"))
            .Accepts<BulkDeleteRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("BulkDeleteFrames");

        // ─── Sessions (§40, §65) ───
        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Sessions");

        sessions.MapGet("",
                (int? limit, string? cursor) => NotImplementedStub("GET /api/v1/sessions", "§40"))
            .Produces<CursorPage<SessionDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ListSessions");

        sessions.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/sessions/{id}", "§40"))
                .Produces<SessionDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status501NotImplemented)
                .WithName("GetSession");

        sessions.MapGet("/{id:guid}/frames",
                (Guid id, int? limit, string? cursor) =>
                    NotImplementedStub("GET /api/v1/sessions/{id}/frames", "§40"))
            .Produces<CursorPage<FrameListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetSessionFrames");

        sessions.MapPost("/{id:guid}/resume-target",
                (Guid id, [FromBody] ResumeTargetRequestDto request) =>
                    NotImplementedStub("POST /api/v1/sessions/{id}/resume-target", "§40"))
            .Accepts<ResumeTargetRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ResumeSessionTarget");

        sessions.MapPost("/{id:guid}/restretch",
                (Guid id, [FromBody] SessionRestretchRequestDto request) =>
                    NotImplementedStub("POST /api/v1/sessions/{id}/restretch", "§65"))
            .Accepts<SessionRestretchRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("RestretchSession");

        sessions.MapGet("/{id:guid}/hfr-analysis", (Guid id) =>
                NotImplementedStub("GET /api/v1/sessions/{id}/hfr-analysis", "§40.7"))
            .Produces<HfrAnalysisDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("GetSessionHfrAnalysis");

        // ─── Backup stream (§44) ───
        var backup = app.MapGroup("/api/v1/backup/stream").WithTags("BackupStream");

        backup.MapPost("/subscribe", () => NotImplementedStub("POST /api/v1/backup/stream/subscribe", "§44"))
              .Produces<BackupSubscriptionDto>(StatusCodes.Status201Created)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("SubscribeBackupStream");

        backup.MapPost("/claim", ([FromBody] BackupClaimRequestDto request) =>
                NotImplementedStub("POST /api/v1/backup/stream/claim", "§44"))
            .Accepts<BackupClaimRequestDto>("application/json")
            .Produces<BackupFrameDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("ClaimBackupFrame");

        return app;
    }
}
