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
/// Phase 9 server-state / lifecycle / log endpoints per PORT_PLAYBOOK.md §10.9.
/// Every route declares its intended request + response DTOs for WILMA codegen.
/// </summary>
public static class ServerStateEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 9's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapServerStateEndpoints(this IEndpointRouteBuilder app) {
        var server = app.MapGroup("/api/v1/server").WithTags("Server");

        // Phase 13.7 — wired to IServerStateService (placeholder today).
        server.MapGet("/state",
                async (IServerStateService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetSnapshotAsync(ct)))
              .Produces<ServerStateDto>(StatusCodes.Status200OK)
              .WithName("GetServerState");

        // §31.3 — the time-sync waterfall's two wire surfaces.
        server.MapGet("/time-sync",
                async (ITimeSyncService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetStateAsync(ct)))
              .Produces<TimeSyncStateDto>(StatusCodes.Status200OK)
              .WithName("GetTimeSync")
              .WithSummary("The server's §31 time-sync state (synced = fresh + at-least-medium trust).");

        server.MapPost("/time-sync",
                async (TimeSyncPushRequestDto request, ITimeSyncService svc, CancellationToken ct) => {
                    try {
                        return Results.Ok(await svc.PushAsync(request, ct));
                    } catch (TimeSyncInvalidRequestException ex) {
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }
                })
              .Accepts<TimeSyncPushRequestDto>("application/json")
              .Produces<TimeSyncPushResultDto>(StatusCodes.Status200OK)
              .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
              .WithName("PushTimeSync")
              .WithSummary("Push a client/mobile-GPS/manual time (+ optional location) per the §31.1 waterfall.");

        server.MapGet("/versions",
                async (IServerStateService svc, CancellationToken ct) =>
                    Results.Ok(await svc.GetVersionsAsync(ct)))
              .Produces<ApiVersionsDto>(StatusCodes.Status200OK)
              .WithName("GetServerVersions");

        server.MapGet("/release-notes",
                async (string? version, IServerStateService svc, CancellationToken ct) => {
                    var notes = await svc.GetReleaseNotesAsync(version, ct);
                    return notes is null ? Results.NotFound() : Results.Ok(notes);
                })
            .Produces<ReleaseNotesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetReleaseNotes");

        // §35.3 — the big red button. Synchronous on purpose: the caller
        // needs to know what actually stopped, not a 202 promise. Rungs are
        // best-effort inside the service; a dead device never 500s this.
        server.MapPost("/emergency-stop",
                async (EmergencyStopService svc) =>
                    Results.Ok(await svc.ExecuteAsync()))
              .Produces<EmergencyStopResultDto>(StatusCodes.Status200OK)
              .WithName("EmergencyStop");

        server.MapPost("/restart",
                async (IServerStateService svc,
                       [FromQuery] string? reason,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) =>
                    Results.Accepted(value: await svc.RestartAsync(reason ?? "operator_requested", idempotencyKey, ct)))
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .WithName("RestartServer");

        server.MapPost("/restart-on-idle",
                async (IServerStateService svc,
                       [FromQuery] string? reason,
                       [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                       CancellationToken ct) =>
                    Results.Accepted(value: await svc.RestartOnIdleAsync(reason ?? "operator_requested", idempotencyKey, ct)))
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .WithName("RestartServerOnIdle");

        // ─── Logs (§29.9) — Phase 13.8 wired to ILogService ───
        var logs = server.MapGroup("/logs");

        logs.MapPost("/rotate",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, ILogService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.RotateAsync(idempotencyKey, ct)))
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithName("RotateLogs")
            .WithDescription("Records an audit marker into the active log and acknowledges (202). " +
                "Does NOT force an immediate file roll — the daemon's log sink rolls automatically " +
                "by day and on a size cap, so the next tail/download is not guaranteed to see a fresh file. " +
                "Idempotency-Key is echoed for tracing but NOT enforced: each call writes a marker " +
                "unconditionally, so a retried key appends another line rather than de-duplicating.");

        logs.MapGet("/download",
                async (string? logFileName, ILogService svc, CancellationToken ct) => {
                    var result = await svc.OpenDownloadAsync(logFileName, ct);
                    if (result is null) return Results.NotFound();
                    return Results.Stream(result.Value.Stream, "application/octet-stream", result.Value.FileName);
                })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DownloadLogs");

        logs.MapPost("/tail",
                async ([FromBody] LogTailRequestDto request, ILogService svc, CancellationToken ct) =>
                    Results.Ok(await svc.TailAsync(request, ct)))
            .Accepts<LogTailRequestDto>("application/json")
            .Produces<IReadOnlyList<LogEntryDto>>(StatusCodes.Status200OK)
            .WithName("TailLogs");

        // /readyz (§60.8) — Phase 13.16. Distinct from /healthz (Phase 4)
        // which only signals "process is alive." /readyz signals "service
        // is ready to accept requests" — i.e. DI graph built + endpoints
        // mapped. Placeholder returns 200 unconditionally since the
        // daemon doesn't yet have async startup work (file-based
        // ProfileStore load is sync in the ctor). Real impl probes the
        // §28 frame catalog DB connection + the §44 backup stream pump
        // and returns 503 if either's not ready.
        app.MapGet("/readyz", (HttpContext http) => {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text("ready", contentType: "text/plain");
        })
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
           .WithName("ReadinessCheck")
           .WithTags("Health");

        return app;
    }
}