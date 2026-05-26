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
/// Phase 7 sequence-endpoint registration per PORT_PLAYBOOK.md §10.7.
///
/// All handlers return 501 NotImplemented stubs with an RFC 7807 Problem
/// body until the corresponding service implementations land. Each route
/// declares its intended request + response DTOs via the .Accepts<T>() /
/// .Produces<T>() / .ProducesProblem() helpers so the generated OpenAPI
/// surface lists the real schemas — WILMA client codegen can target the
/// full Phase 7 contract today even though every handler is a stub.
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
        seq.MapGet("", () => NotImplementedStub("GET /api/v1/sequences", "§38"))
           .Produces<CursorPage<SequenceListItemDto>>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ListSequences");

        seq.MapGet("/{id:guid}", (Guid id) => NotImplementedStub("GET /api/v1/sequences/{id}", "§38"))
           .Produces<SequenceDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("GetSequence");

        seq.MapPost("", ([FromBody] SequenceCreateRequestDto request) => NotImplementedStub("POST /api/v1/sequences", "§38"))
           .Accepts<SequenceCreateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("CreateSequence");

        // PATCH (not PUT) because the partial-update semantics — only supplied
        // fields are touched — don't match REST's full-replacement PUT contract.
        // See SequenceUpdateRequestDto XML doc.
        seq.MapPatch("/{id:guid}", (Guid id, [FromBody] SequenceUpdateRequestDto request) =>
                NotImplementedStub("PATCH /api/v1/sequences/{id}", "§38"))
           .Accepts<SequenceUpdateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("UpdateSequence");

        seq.MapDelete("/{id:guid}", (Guid id) => NotImplementedStub("DELETE /api/v1/sequences/{id}", "§38"))
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("DeleteSequence");

        // Lifecycle
        seq.MapGet("/{id:guid}/state", (Guid id) => NotImplementedStub("GET /api/v1/sequences/{id}/state", "§28.12"))
           .Produces<SequenceRunStateDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("GetSequenceState");

        seq.MapPost("/{id:guid}/start", (Guid id, [FromBody] SequenceStartRequestDto request) =>
                NotImplementedStub("POST /api/v1/sequences/{id}/start", "§28"))
           .Accepts<SequenceStartRequestDto>("application/json")
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("StartSequence");

        seq.MapPost("/{id:guid}/pause", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/pause", "§28.12"))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("PauseSequence");

        seq.MapPost("/{id:guid}/resume", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/resume", "§28.12"))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ResumeSequence");

        seq.MapPost("/{id:guid}/abort", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/abort", "§28"))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("AbortSequence");

        seq.MapPost("/{id:guid}/stop", (Guid id) => NotImplementedStub("POST /api/v1/sequences/{id}/stop", "§28"))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("StopSequence");

        // Templates (§38.6, §38.7)
        seq.MapGet("/templates", () => NotImplementedStub("GET /api/v1/sequences/templates", "§38.6"))
           .Produces<IReadOnlyList<SequenceTemplateDto>>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ListSequenceTemplates");

        seq.MapPost("/templates/{name}/instantiate",
                (string name, [FromBody] TemplateInstantiateRequestDto request) =>
                    NotImplementedStub("POST /api/v1/sequences/templates/{name}/instantiate", "§38.6"))
           .Accepts<TemplateInstantiateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("InstantiateSequenceTemplate");

        // NINA import (§38.4)
        seq.MapPost("/import", ([FromBody] SequenceImportRequestDto request) =>
                NotImplementedStub("POST /api/v1/sequences/import", "§38.4"))
           .Accepts<SequenceImportRequestDto>("application/json")
           .Produces<SequenceImportResultDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ImportNinaSequence");

        // Sharing (§70)
        seq.MapPost("/{id:guid}/share-export", (Guid id) =>
                NotImplementedStub("POST /api/v1/sequences/{id}/share-export", "§70"))
           .Produces<SequenceShareDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("ShareExportSequence");

        // Auto-flats decision (§48)
        seq.MapPost("/{id:guid}/auto-flats-decision",
                (Guid id, [FromBody] AutoFlatsDecisionRequestDto request) =>
                    NotImplementedStub("POST /api/v1/sequences/{id}/auto-flats-decision", "§48"))
           .Accepts<AutoFlatsDecisionRequestDto>("application/json")
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status501NotImplemented)
           .WithName("DecideAutoFlats");

        return app;
    }
}
