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
/// Phase 9 server-state / lifecycle / log endpoints per PORT_PLAYBOOK.md §10.9.
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

        server.MapGet("/state", () => NotImplementedStub("GET /api/v1/server/state", "§60.4"));
        server.MapGet("/versions", () => NotImplementedStub("GET /api/v1/server/versions", "§33.2.1"));
        server.MapGet("/release-notes", () => NotImplementedStub("GET /api/v1/server/release-notes", "§54"));
        server.MapPost("/restart", () => NotImplementedStub("POST /api/v1/server/restart", "§34.7"));
        server.MapPost("/restart-on-idle", () => NotImplementedStub("POST /api/v1/server/restart-on-idle", "§34.7"));

        // /api/v1/server/info already lives directly in Program.cs (Phase 4 scaffold).

        // ─── Logs (§29.9) ───
        var logs = server.MapGroup("/logs");
        logs.MapPost("/rotate", () => NotImplementedStub("POST /api/v1/server/logs/rotate", "§29.9"));
        logs.MapGet("/download", () => NotImplementedStub("GET /api/v1/server/logs/download", "§29.9"));
        logs.MapPost("/tail", () => NotImplementedStub("POST /api/v1/server/logs/tail", "§29.9"));

        // /readyz (§60.8) — distinct from /healthz which Phase 4 owns.
        app.MapGet("/readyz", () => NotImplementedStub("GET /readyz", "§60.8"));

        return app;
    }
}
