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

        // Phase 13.2 — wired to IFrameRepository. PlaceholderFrameRepository
        // returns three sample frames (two Lights + one Dark, all M31 in the
        // same fake session) so the WILMA Library + frame-detail UI has real
        // wire shapes to render. Phase 13.3+ swaps in the §28 DB-backed impl.
        frames.MapGet("",
                async (int? limit, string? cursor, Guid? sessionId, string? targetName, IFrameRepository repo, CancellationToken ct) =>
                    Results.Ok(await repo.ListAsync(limit ?? 50, cursor, sessionId, targetName, ct)))
            .Produces<CursorPage<FrameListItemDto>>(StatusCodes.Status200OK)
            .WithName("ListFrames");

        frames.MapGet("/{id:guid}", async (Guid id, IFrameRepository repo, CancellationToken ct) => {
                var frame = await repo.GetAsync(id, ct);
                return frame is null ? Results.NotFound() : Results.Ok(frame);
            })
              .Produces<FrameDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("GetFrame");

        // Phase 13.1 — wired to IFrameRepository (PlaceholderFrameRepository
        // returns a small precomputed JPEG so the wire is testable end-to-end).
        // Real OpenCvSharp4 + §28 frame catalog DB lands in Phase 13.2+.
        frames.MapPost("/{id:guid}/preview", async (Guid id, [FromBody] FramePreviewRequestDto request, IFrameRepository repo, CancellationToken ct) => {
                var result = await repo.GetPreviewAsync(id, request, ct);
                return result is null
                    ? Results.NotFound()
                    : Results.Bytes(result.Value.Bytes, result.Value.ContentType);
            })
            .Accepts<FramePreviewRequestDto>("application/json")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetFramePreview");

        frames.MapGet("/{id:guid}/thumbnail", async (Guid id, IFrameRepository repo, CancellationToken ct) => {
                var result = await repo.GetThumbnailAsync(id, ct);
                return result is null
                    ? Results.NotFound()
                    : Results.Bytes(result.Value.Bytes, result.Value.ContentType);
            })
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetFrameThumbnail");

        frames.MapGet("/{id:guid}/download", (Guid id) =>
                NotImplementedStub("GET /api/v1/frames/{id}/download", "§72"))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/fits")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .WithName("DownloadFrame");

        frames.MapPost("/bulk/rate",
                async (IFrameRepository repo, [FromBody] BulkRateRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) =>
                    Results.Accepted(value: await repo.BulkRateAsync(request, idempotencyKey, ct)))
            .Accepts<BulkRateRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("BulkRateFrames");

        frames.MapPost("/bulk/tag",
                async (IFrameRepository repo, [FromBody] BulkTagRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) =>
                    Results.Accepted(value: await repo.BulkTagAsync(request, idempotencyKey, ct)))
            .Accepts<BulkTagRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("BulkTagFrames");

        frames.MapPost("/bulk/delete",
                async (IFrameRepository repo, [FromBody] BulkDeleteRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) =>
                    Results.Accepted(value: await repo.BulkDeleteAsync(request, idempotencyKey, ct)))
            .Accepts<BulkDeleteRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("BulkDeleteFrames");

        // ─── Sessions (§40, §65) ───
        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Sessions");

        // Phase 13.3 — wired to ISessionService. Placeholder returns one
        // fake session matching the §13.2 sample frames so list/get/frames
        // all join up. §28 DB-backed impl lands in Phase 13.4+.
        sessions.MapGet("",
                async (int? limit, string? cursor, ISessionService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(limit ?? 50, cursor, ct)))
            .Produces<CursorPage<SessionDto>>(StatusCodes.Status200OK)
            .WithName("ListSessions");

        sessions.MapGet("/{id:guid}", async (Guid id, ISessionService svc, CancellationToken ct) => {
                var session = await svc.GetAsync(id, ct);
                return session is null ? Results.NotFound() : Results.Ok(session);
            })
                .Produces<SessionDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .WithName("GetSession");

        sessions.MapGet("/{id:guid}/frames",
                async (Guid id, int? limit, string? cursor, ISessionService svc, CancellationToken ct) => {
                    // Existence check first — without it, unknown session IDs
                    // would return 200 + empty list (the frame repo silently
                    // filters to no matches), which is semantically wrong:
                    // "no frames in a non-existent session" ≠ "this session
                    // had no frames yet". §40 expects 404 here.
                    var session = await svc.GetAsync(id, ct);
                    if (session is null) return Results.NotFound();
                    return Results.Ok(await svc.GetFramesAsync(id, limit ?? 50, cursor, ct));
                })
            .Produces<CursorPage<FrameListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
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

        sessions.MapGet("/{id:guid}/hfr-analysis",
                async (ISessionService svc, Guid id, CancellationToken ct) => {
                    var analysis = await svc.GetHfrAnalysisAsync(id, ct);
                    return analysis is null
                        ? Results.NotFound()
                        : Results.Ok(analysis);
                })
            .Produces<HfrAnalysisDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetSessionHfrAnalysis");

        // ─── Backup stream (§44) — Phase 13.10 wired to IBackupStreamService ───
        var backup = app.MapGroup("/api/v1/backup/stream").WithTags("BackupStream");

        backup.MapPost("/subscribe",
                async (IBackupStreamService svc, CancellationToken ct) => {
                    var sub = await svc.SubscribeAsync(ct);
                    return Results.Created($"/api/v1/backup/stream/sub/{sub.SubscriptionId}", value: sub);
                })
              .Produces<BackupSubscriptionDto>(StatusCodes.Status201Created)
              .WithName("SubscribeBackupStream");

        backup.MapPost("/claim",
                async ([FromBody] BackupClaimRequestDto request, IBackupStreamService svc, CancellationToken ct) => {
                    var frame = await svc.ClaimAsync(request, ct);
                    return frame is null ? Results.NotFound() : Results.Ok(frame);
                })
            .Accepts<BackupClaimRequestDto>("application/json")
            .Produces<BackupFrameDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ClaimBackupFrame");

        return app;
    }
}
