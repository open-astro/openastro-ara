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

    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app) {
        // ─── Frames (§40, §65) ───
        var frames = app.MapGroup("/api/v1/frames").WithTags("Frames");

        // Wired to IFrameRepository. SqliteFrameRepository reads from the
        // §28 catalog (seeded with three sample frames on first init for
        // dev/UI work; real frames arrive once §72 FITS storage + §38
        // sequence orchestrator land).
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

        // Wired to IFrameRepository. SqliteFrameRepository returns a 1×1
        // JPEG placeholder for now; §65 stretch pipeline replaces it with
        // a real OpenCvSharp4 render from the captured FITS.
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

        // §65.6 cache reset — flush all alt-stretch variants for a frame.
        // Useful when the user changes stretch_defaults and wants old cached
        // renders to roll forward, or when storage pressure makes them
        // suspect. Default-stretch preview + thumbnail are unaffected by the
        // §65.4 naming pattern this scans for.
        frames.MapDelete("/{id:guid}/preview/variants",
                async (Guid id, IFrameRepository repo, CancellationToken ct) => {
                    var deleted = await repo.DeletePreviewVariantsAsync(id, ct);
                    return deleted ? Results.NoContent() : Results.NotFound();
                })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DeleteFramePreviewVariants");

        frames.MapGet("/{id:guid}/thumbnail", async (Guid id, IFrameRepository repo, CancellationToken ct) => {
                var result = await repo.GetThumbnailAsync(id, ct);
                return result is null
                    ? Results.NotFound()
                    : Results.Bytes(result.Value.Bytes, result.Value.ContentType);
            })
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetFrameThumbnail");

        // §72 FITS download — serves the captured file from the catalog's
        // file_path column. 404 when the frame isn't in the catalog OR
        // the FITS file is missing on disk (deleted out-of-band, drive
        // unmounted, or the sample-seeded frames whose file_path values
        // point at non-existent paths). 200 with `application/fits`
        // content-type otherwise.
        frames.MapGet("/{id:guid}/download",
                async (Guid id, IFrameRepository repo, CancellationToken ct) => {
                    var result = await repo.OpenDownloadAsync(id, ct);
                    if (result is null) return Results.NotFound();
                    return Results.File(result.Value.FitsStream, "application/fits", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/fits")
            .ProducesProblem(StatusCodes.Status404NotFound)
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
                async (ISessionService svc, Guid id, [FromBody] ResumeTargetRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => {
                    // Verify the session exists before accepting the operation —
                    // matches §40 wire contract where resume-target on an unknown
                    // session is 404, not a 202 the operator will silently watch
                    // never make progress.
                    var session = await svc.GetAsync(id, ct);
                    return session is null
                        ? Results.NotFound()
                        : Results.Accepted(value: await svc.ResumeTargetAsync(id, request, idempotencyKey, ct));
                })
            .Accepts<ResumeTargetRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ResumeSessionTarget");

        sessions.MapPost("/{id:guid}/restretch",
                async (ISessionService svc, Guid id, [FromBody] SessionRestretchRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => {
                    var session = await svc.GetAsync(id, ct);
                    return session is null
                        ? Results.NotFound()
                        : Results.Accepted(value: await svc.RestretchAsync(id, request, idempotencyKey, ct));
                })
            .Accepts<SessionRestretchRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
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
