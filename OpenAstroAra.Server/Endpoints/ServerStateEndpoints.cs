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

        server.MapPost("/restart", () => NotImplementedStub("POST /api/v1/server/restart", "§34.7"))
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("RestartServer");

        server.MapPost("/restart-on-idle", () => NotImplementedStub("POST /api/v1/server/restart-on-idle", "§34.7"))
              .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
              .ProducesProblem(StatusCodes.Status501NotImplemented)
              .WithName("RestartServerOnIdle");

        // ─── Logs (§29.9) — Phase 13.8 wired to ILogService ───
        var logs = server.MapGroup("/logs");

        logs.MapPost("/rotate",
                async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, ILogService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.RotateAsync(idempotencyKey, ct)))
            .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
            .WithName("RotateLogs");

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

        // /readyz (§60.8) — distinct from /healthz which Phase 4 owns.
        app.MapGet("/readyz", () => NotImplementedStub("GET /readyz", "§60.8"))
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ReadinessCheck")
           .WithTags("Health");

        return app;
    }
}
