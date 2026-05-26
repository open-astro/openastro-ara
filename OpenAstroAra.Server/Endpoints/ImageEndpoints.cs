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
/// Phase 8 image / session / backup-stream endpoint registration per PORT_PLAYBOOK.md §10.8.
/// All endpoints return 501 NotImplemented stubs until services are wired up.
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
        frames.MapGet("", () => NotImplementedStub("GET /api/v1/frames", "§40"));
        frames.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/frames/{id}", "§40"));
        frames.MapPost("/{id:guid}/preview", (Guid id) => NotImplementedStub("POST /api/v1/frames/{id}/preview", "§65"));
        frames.MapGet("/{id:guid}/thumbnail", (Guid id) => NotImplementedStub("GET /api/v1/frames/{id}/thumbnail", "§65"));
        frames.MapGet("/{id:guid}/download", (Guid id) => NotImplementedStub("GET /api/v1/frames/{id}/download", "§72"));
        frames.MapPost("/bulk/rate", () => NotImplementedStub("POST /api/v1/frames/bulk/rate", "§40.8"));
        frames.MapPost("/bulk/tag", () => NotImplementedStub("POST /api/v1/frames/bulk/tag", "§40.8"));
        frames.MapPost("/bulk/delete", () => NotImplementedStub("POST /api/v1/frames/bulk/delete", "§40.8"));

        // ─── Sessions (§40, §65) ───
        var sessions = app.MapGroup("/api/v1/sessions").WithTags("Sessions");
        sessions.MapGet("", () => NotImplementedStub("GET /api/v1/sessions", "§40"));
        sessions.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/sessions/{id}", "§40"));
        sessions.MapGet("/{id:guid}/frames", (Guid id) => NotImplementedStub("GET /api/v1/sessions/{id}/frames", "§40"));
        sessions.MapPost("/{id:guid}/resume-target", (Guid id) => NotImplementedStub("POST /api/v1/sessions/{id}/resume-target", "§40"));
        sessions.MapPost("/{id:guid}/restretch", (Guid id) => NotImplementedStub("POST /api/v1/sessions/{id}/restretch", "§65"));
        sessions.MapGet("/{id:guid}/hfr-analysis", (Guid id) => NotImplementedStub("GET /api/v1/sessions/{id}/hfr-analysis", "§40.7"));

        // ─── Backup stream (§44) ───
        var backup = app.MapGroup("/api/v1/backup/stream").WithTags("BackupStream");
        backup.MapPost("/subscribe", () => NotImplementedStub("POST /api/v1/backup/stream/subscribe", "§44"));
        backup.MapPost("/claim", () => NotImplementedStub("POST /api/v1/backup/stream/claim", "§44"));

        return app;
    }
}
