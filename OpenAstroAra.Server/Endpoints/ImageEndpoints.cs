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
using System.Globalization;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 8 image / session / backup-stream endpoint registration per PORT_PLAYBOOK.md §10.8.
/// Every route declares its intended request + response DTOs so the generated OpenAPI
/// surface lists real schemas for WILMA client codegen, even while handlers return 501.
/// </summary>
public static class ImageEndpoints {
    private static readonly FramePreviewRequestDto DefaultPreviewRequest = new(
        StretchPalette: "", BlackPoint: null, MidtonePoint: null,
        WhitePoint: null, MaxDimensionPx: null, ApplyDebayer: true);

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

        frames.MapGet("/{id:guid}/metadata", GetFrameMetadataAsync)
            .Produces<FrameMetadataResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetFrameMetadata");

        frames.MapGet("/{id:guid}/preview", GetDefaultFramePreviewAsync)
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("GetFramePreview");

        // Real FITS preview renderer with profile/default or request-specific
        // stretch, debayer, dimension cap, and on-disk variant cache.
        frames.MapPost("/{id:guid}/preview", RenderFramePreviewAsync)
            .Accepts<FramePreviewRequestDto>("application/json")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("RenderFramePreview");

        frames.MapPost("/{id:guid}/rebuild-preview", RebuildFramePreviewAsync)
            .Accepts<FramePreviewRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RebuildFramePreview");

        frames.MapPost("/{id:guid}/reanalyze", ReanalyzeFrameAsync)
            .Accepts<FrameReanalysisRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("ReanalyzeFrame");

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
                       CancellationToken ct) => await BulkMutationAsync(
                           () => repo.BulkRateAsync(request, idempotencyKey, ct)))
            .Accepts<BulkRateRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("BulkRateFrames");

        frames.MapPost("/bulk/tag",
                async (IFrameRepository repo, [FromBody] BulkTagRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => await BulkMutationAsync(
                           () => repo.BulkTagAsync(request, idempotencyKey, ct)))
            .Accepts<BulkTagRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("BulkTagFrames");

        frames.MapPost("/bulk/move",
                async (IFrameRepository repo, [FromBody] BulkMoveRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => await BulkMutationAsync(
                           () => repo.BulkMoveAsync(request, idempotencyKey, ct)))
            .Accepts<BulkMoveRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("BulkMoveFrames");

        frames.MapPost("/bulk/quarantine",
                async (IFrameRepository repo, [FromBody] BulkQuarantineRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => await BulkMutationAsync(
                           () => repo.BulkQuarantineAsync(request, idempotencyKey, ct)))
            .Accepts<BulkQuarantineRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("BulkQuarantineFrames");

        frames.MapPost("/bulk/export",
                async (HttpContext http, IFrameRepository repo, [FromBody] BulkExportRequestDto request, CancellationToken ct) => {
                    var prep = await repo.PrepareExportAsync(request, ct);
                    if (prep is null) return Results.NotFound();
                    // Export is partial-success by design (missing files skip).
                    // BEST-EFFORT count: planned entries whose files existed at
                    // plan time (headers must precede the streamed body; a file
                    // vanishing before its turn still skips below).
                    http.Response.Headers["X-Ara-Exported-Count"] =
                        prep.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // Stream entries with ONE file handle open at a time (the r1
                    // FD-exhaustion fix for Pi-class ulimits) and no in-memory tar.
                    // A file vanishing between planning and its turn skips at open
                    // — before its entry writes — so the tar stays aligned; the
                    // count header is best-effort by that same token.
                    return Results.Stream(async output => {
                        await using var tar = new System.Formats.Tar.TarWriter(output, leaveOpen: true);
                        foreach (var (path, name) in prep.Entries) {
                            System.IO.FileStream fs;
                            try {
                                fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                            } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
                                continue;
                            }
                            await using (fs) {
                                var entry = new System.Formats.Tar.PaxTarEntry(
                                    System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = fs };
                                await tar.WriteEntryAsync(entry, http.RequestAborted);
                            }
                        }
                    }, "application/x-tar", prep.FileName);
                })
            .Accepts<BulkExportRequestDto>("application/json")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/x-tar")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("BulkExportFrames");

        frames.MapPost("/bulk/delete",
                async (IFrameRepository repo, [FromBody] BulkDeleteRequestDto request,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) => await BulkMutationAsync(
                           () => repo.BulkDeleteAsync(request, idempotencyKey, ct)))
            .Accepts<BulkDeleteRequestDto>("application/json")
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
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
                           if (session is null) return Results.NotFound();
                           try {
                               // §40.6 result carries the runnable sequence — 201 + Location,
                               // like the §39.5 matching-flats endpoint.
                               var result = await svc.ResumeTargetAsync(id, request, idempotencyKey, ct);
                               return Results.Created($"/api/v1/sequences/{result.SequenceId}", result);
                           } catch (ArgumentException ex) when (ex.ParamName == "request") {
                               return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                           }
                       })
            .Accepts<ResumeTargetRequestDto>("application/json")
            .Produces<ResumeTargetResultDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
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
        // §44.5 — the backup-stream control surface. The FITS bytes themselves ride the
        // existing GET /api/v1/frames/{id}/download; these routes manage the single
        // target slot + the pending queue + acks.
        var backup = app.MapGroup("/api/v1/server/backup-stream").WithTags("BackupStream");

        backup.MapGet("/status",
                async (IBackupStreamService svc, CancellationToken ct) => Results.Ok(await svc.GetStatusAsync(ct)))
              .Produces<BackupStreamStatusDto>(StatusCodes.Status200OK)
              .WithName("GetBackupStreamStatus");

        backup.MapPost("/claim",
                async ([FromBody] BackupStreamClaimRequestDto request, IBackupStreamService svc, CancellationToken ct) => {
                    if (string.IsNullOrWhiteSpace(request?.Hostname)) {
                        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                            title: "hostname required", type: "https://openastro.net/problems/backup-stream-hostname-required");
                    }
                    var result = await svc.ClaimAsync(request, ct);
                    if (result is not null) {
                        return Results.Ok(result);
                    }
                    var holder = (svc as BackupStreamService)?.ActiveTargetSnapshot;
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "another WILMA is already streaming",
                        detail: holder,
                        type: "https://openastro.net/problems/backup-stream-slot-held");
                })
              .Accepts<BackupStreamClaimRequestDto>("application/json")
              .Produces<BackupStreamClaimResultDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status409Conflict)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("ClaimBackupStream");

        backup.MapPost("/release",
                async ([FromBody] BackupStreamClaimRequestDto request, IBackupStreamService svc, CancellationToken ct) =>
                    await svc.ReleaseAsync(request, ct) ? Results.NoContent() : Results.NotFound())
              .Accepts<BackupStreamClaimRequestDto>("application/json")
              .Produces(StatusCodes.Status204NoContent)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .WithName("ReleaseBackupStream");

        backup.MapGet("/queue",
                async (string hostname, int? limit, IBackupStreamService svc, CancellationToken ct) => {
                    var queue = await svc.GetQueueAsync(hostname, limit ?? 100, ct);
                    return queue is null
                        ? Results.Problem(statusCode: StatusCodes.Status409Conflict,
                            title: "caller does not hold the backup-stream slot",
                            type: "https://openastro.net/problems/backup-stream-not-holder")
                        : Results.Ok(queue);
                })
              .Produces<System.Collections.Generic.IReadOnlyList<BackupStreamQueueEntryDto>>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status409Conflict)
              .WithName("GetBackupStreamQueue");

        backup.MapPost("/ack",
                async (string hostname, [FromBody] BackupStreamAckRequestDto request, IBackupStreamService svc, CancellationToken ct) =>
                    await svc.AckAsync(hostname, request, ct) switch {
                        BackupStreamAckResult.Acked => Results.NoContent(),
                        BackupStreamAckResult.NotHolder => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                            title: "caller does not hold the backup-stream slot",
                            type: "https://openastro.net/problems/backup-stream-not-holder"),
                        BackupStreamAckResult.UnverifiedRefused => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                            title: "unverified ack refused — re-download and verify the sha256, then ack",
                            type: "https://openastro.net/problems/backup-stream-unverified-ack"),
                        _ => Results.NotFound(),
                    })
              .Accepts<BackupStreamAckRequestDto>("application/json")
              .Produces(StatusCodes.Status204NoContent)
              .ProducesProblem(StatusCodes.Status404NotFound)
              .ProducesProblem(StatusCodes.Status409Conflict)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("AckBackupStreamFrame");

        return app;
    }

    internal static async Task<IResult> GetFrameMetadataAsync(Guid id,
            IFrameRepository repo, CancellationToken ct) {
        var metadata = await repo.GetMetadataAsync(id, ct).ConfigureAwait(false);
        return metadata is null ? FrameNotFound() : Results.Ok(metadata);
    }

    internal static Task<IResult> GetDefaultFramePreviewAsync(Guid id,
            HttpContext http, IFrameRepository repo, CancellationToken ct) =>
        RenderFramePreviewAsync(id, DefaultPreviewRequest, http, repo, ct);

    internal static async Task<IResult> RenderFramePreviewAsync(Guid id,
            [FromBody] FramePreviewRequestDto request, HttpContext http,
            IFrameRepository repo, CancellationToken ct) {
        try {
            var result = await repo.GetPreviewAsync(id, request, ct).ConfigureAwait(false);
            return result is null ? FrameNotFound() : PreviewResponse(http, result.Value);
        } catch (ArgumentException ex) {
            return InvalidRequest(ex.Message);
        } catch (Exception ex) when (ex is NotSupportedException or InvalidDataException
                                     or OpenAstroAra.Fits.FitsException) {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Unsupported or invalid frame source",
                detail: "The frame source could not be decoded.",
                type: "https://openastro.net/problems/frame-source-invalid");
        }
    }

    internal static async Task<IResult> RebuildFramePreviewAsync(Guid id,
            [FromBody] FramePreviewRequestDto request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IFrameOperationService operations, CancellationToken ct) {
        try {
            var accepted = await operations.RebuildPreviewAsync(id, request,
                idempotencyKey, ct).ConfigureAwait(false);
            return accepted is null
                ? FrameNotFound()
                : Results.Accepted($"/api/v1/jobs/{accepted.OperationId:D}", accepted);
        } catch (ArgumentException ex) {
            return InvalidRequest(ex.Message);
        } catch (Exception ex) when (ex is IdempotencyKeyConflictException
                                     or FrameOperationInProgressException
                                     or FrameSourceUnavailableException) {
            return OperationConflict(ex.Message);
        }
    }

    internal static async Task<IResult> ReanalyzeFrameAsync(Guid id,
            [FromBody] FrameReanalysisRequestDto request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IFrameOperationService operations, CancellationToken ct) {
        try {
            var accepted = await operations.ReanalyzeAsync(id, request,
                idempotencyKey, ct).ConfigureAwait(false);
            return accepted is null
                ? FrameNotFound()
                : Results.Accepted($"/api/v1/jobs/{accepted.OperationId:D}", accepted);
        } catch (ArgumentException ex) {
            return InvalidRequest(ex.Message);
        } catch (Exception ex) when (ex is IdempotencyKeyConflictException
                                     or FrameOperationInProgressException
                                     or FrameSourceUnavailableException) {
            return OperationConflict(ex.Message);
        }
    }

    internal static async Task<IResult> BulkMutationAsync(
            Func<Task<OperationAcceptedDto>> mutation) {
        ArgumentNullException.ThrowIfNull(mutation);
        try {
            return Results.Accepted(value: await mutation().ConfigureAwait(false));
        } catch (IdempotencyKeyConflictException ex) {
            return OperationConflict(ex.Message);
        } catch (ArgumentException ex) {
            return InvalidRequest(ex.Message);
        }
    }

    private static IResult InvalidRequest(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid frame request",
        detail: detail,
        type: "https://openastro.net/problems/invalid-frame-request");

    private static IResult FrameNotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Frame not found",
        detail: "The requested frame was not found.",
        type: "https://openastro.net/problems/frame-not-found");

    private static IResult OperationConflict(string detail) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Frame operation conflict",
        detail: detail,
        type: "https://openastro.net/problems/frame-operation-conflict");

    private static IResult PreviewResponse(HttpContext http, FramePreviewResult preview) {
        var etag = $"\"{preview.Metadata.CacheKey}\"";
        if (http.Request.Headers.IfNoneMatch.Any(value =>
                string.Equals(value, etag, StringComparison.Ordinal))) {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers["X-OpenAstro-Preview-Width"] =
            preview.Metadata.Width.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Preview-Height"] =
            preview.Metadata.Height.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Preview-Cache"] = preview.CacheHit ? "hit" : "miss";
        http.Response.Headers["X-OpenAstro-Stretch"] = preview.Metadata.Algorithm;
        http.Response.Headers["X-OpenAstro-Black-Point"] =
            preview.Metadata.AppliedParameters.Blackpoint.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Mid-Point"] =
            preview.Metadata.AppliedParameters.Midpoint.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-White-Point"] =
            preview.Metadata.AppliedParameters.Whitepoint.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Asinh-Beta"] =
            preview.Metadata.AppliedParameters.Beta.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Clip-Low"] =
            preview.Metadata.AppliedParameters.LinearClipLow.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Clip-High"] =
            preview.Metadata.AppliedParameters.LinearClipHigh.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Debayer"] = preview.Metadata.DebayerMode;
        http.Response.Headers["X-OpenAstro-Channel"] = preview.Metadata.ChannelMode;
        http.Response.Headers["X-OpenAstro-Inverted"] = preview.Metadata.Inverted ? "true" : "false";
        http.Response.Headers["X-OpenAstro-Saturation"] =
            preview.Metadata.Saturation.ToString("R", CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Annotated"] = preview.Metadata.Annotated ? "true" : "false";
        http.Response.Headers["X-OpenAstro-Annotation-Count"] =
            preview.Metadata.AnnotationCount.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-OpenAstro-Annotation-Rejected"] =
            preview.Metadata.RejectedAnnotationCount.ToString(CultureInfo.InvariantCulture);
        if (preview.Metadata.AnnotationColor is { } annotationColor) {
            http.Response.Headers["X-OpenAstro-Annotation-Color"] = annotationColor;
        }
        return Results.Bytes(preview.Bytes, preview.ContentType);
    }
}