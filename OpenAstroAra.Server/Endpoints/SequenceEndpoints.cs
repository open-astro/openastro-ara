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

        // Phase 13.13 — CRUD wired to ISequenceService (in-memory placeholder).
        seq.MapGet("",
                async (int? limit, string? cursor, ISequenceService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(limit ?? 50, cursor, ct)))
           .Produces<CursorPage<SequenceListItemDto>>(StatusCodes.Status200OK)
           .WithName("ListSequences");

        seq.MapGet("/{id:guid}", async (Guid id, ISequenceService svc, CancellationToken ct) => {
                var dto = await svc.GetAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
           .Produces<SequenceDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("GetSequence");

        seq.MapPost("",
                async ([FromBody] SequenceCreateRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISequenceService svc, CancellationToken ct) => {
                    var dto = await svc.CreateAsync(request, key, ct);
                    return Results.Created($"/api/v1/sequences/{dto.Id}", dto);
                })
           .Accepts<SequenceCreateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .WithName("CreateSequence");

        seq.MapPatch("/{id:guid}",
                async (Guid id, [FromBody] SequenceUpdateRequestDto request, ISequenceService svc, CancellationToken ct) => {
                    var dto = await svc.UpdateAsync(id, request, ct);
                    return dto is null ? Results.NotFound() : Results.Ok(dto);
                })
           .Accepts<SequenceUpdateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .WithName("UpdateSequence");

        seq.MapDelete("/{id:guid}",
                async (Guid id, ISequenceService svc, CancellationToken ct) => {
                    var ok = await svc.DeleteAsync(id, ct);
                    return ok ? Results.NoContent() : Results.NotFound();
                })
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("DeleteSequence");

        // Phase 13.13 — Lifecycle wired to ISequencerService.
        seq.MapGet("/{id:guid}/state",
                async (Guid id, ISequencerService svc, CancellationToken ct) => {
                    var state = await svc.GetRunStateAsync(id, ct);
                    return state is null ? Results.NotFound() : Results.Ok(state);
                })
           .Produces<SequenceRunStateDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("GetSequenceState");

        seq.MapPost("/{id:guid}/start",
                async (Guid id, [FromBody] SequenceStartRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.StartAsync(id, request, key, ct)))
           .Accepts<SequenceStartRequestDto>("application/json")
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .WithName("StartSequence");

        seq.MapPost("/{id:guid}/pause",
                async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.PauseAsync(id, key, ct)))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .WithName("PauseSequence");

        seq.MapPost("/{id:guid}/resume",
                async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.ResumeAsync(id, key, ct)))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .WithName("ResumeSequence");

        seq.MapPost("/{id:guid}/abort",
                async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.AbortAsync(id, key, ct)))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .WithName("AbortSequence");

        seq.MapPost("/{id:guid}/stop",
                async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.StopAsync(id, key, ct)))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
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

        // Sharing (§70) — Phase 13.13 wired to ISequenceService.
        seq.MapPost("/{id:guid}/share-export",
                async (Guid id, ISequenceService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ShareExportAsync(id, ct)))
           .Produces<SequenceShareDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
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
