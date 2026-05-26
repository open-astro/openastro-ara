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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Phase 7 sequence-endpoint registration per PORT_PLAYBOOK.md §10.7.
///
/// All endpoints return 501 NotImplemented stubs with an RFC 7807 Problem
/// body until the corresponding service implementations land. The endpoint
/// surface is registered so OpenAPI/codegen can target it today; impls
/// follow incrementally as the §38 sequencer engine is brought across.
/// </summary>
public static class SequenceEndpoints {

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 7's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    public static IEndpointRouteBuilder MapSequenceEndpoints(this IEndpointRouteBuilder app) {
        var seq = app.MapGroup("/api/v1/sequences").WithTags("Sequences");

        // CRUD
        seq.MapGet("", () => NotImplementedStub("GET /api/v1/sequences", "§38"));
        seq.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/sequences/{id}", "§38"));
        seq.MapPost("", () => NotImplementedStub("POST /api/v1/sequences", "§38"));
        seq.MapPut("/{id:guid}", (Guid id) => NotImplementedStub("PUT /api/v1/sequences/{id}", "§38"));
        seq.MapDelete("/{id:guid}", (Guid id) => NotImplementedStub("DELETE /api/v1/sequences/{id}", "§38"));

        // Lifecycle
        seq.MapGet("/{id:guid}/state", (Guid id) => NotImplementedStub("GET /api/v1/sequences/{id}/state", "§28.12"));
        seq.MapPost("/{id:guid}/start", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/start", "§28"));
        seq.MapPost("/{id:guid}/pause", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/pause", "§28.12"));
        seq.MapPost("/{id:guid}/resume", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/resume", "§28.12"));
        seq.MapPost("/{id:guid}/abort", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/abort", "§28"));
        seq.MapPost("/{id:guid}/stop", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/stop", "§28"));

        // Templates (§38.6, §38.7)
        seq.MapGet("/templates", () => NotImplementedStub("GET /api/v1/sequences/templates", "§38.6"));
        seq.MapPost("/templates/{name}/instantiate", (string name) =>
            NotImplementedStub("POST /api/v1/sequences/templates/{name}/instantiate", "§38.6"));

        // NINA import (§38.4)
        seq.MapPost("/import", () => NotImplementedStub("POST /api/v1/sequences/import", "§38.4"));

        // Sharing (§70)
        seq.MapPost("/{id:guid}/share-export", (Guid id) =>
            NotImplementedStub("POST /api/v1/sequences/{id}/share-export", "§70"));

        // Auto-flats decision (§48)
        seq.MapPost("/{id:guid}/auto-flats-decision", (Guid id) =>
            NotImplementedStub("POST /api/v1/sequences/{id}/auto-flats-decision", "§48"));

        return app;
    }
}
