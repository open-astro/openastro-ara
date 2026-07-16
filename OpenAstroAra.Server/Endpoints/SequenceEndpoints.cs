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

    // Import-replay cache — see the /import handler. Static: endpoint mapping is
    // once-per-process and the cache is deliberately in-process (IdempotencyCache docs).
    private static readonly IdempotencyCache<SequenceImportResultDto> ImportReplays = new();

    private static IResult NotImplementedStub(string endpoint, string section) =>
        Results.Problem(
            type: "https://openastro.net/errors/not-implemented",
            title: "Endpoint not yet implemented",
            statusCode: StatusCodes.Status501NotImplemented,
            detail: $"{endpoint} is part of Phase 7's incremental implementation ({section}). Stub registered so the OpenAPI surface is stable; service wiring lands per area.");

    /// <summary>§38 — a live executor owns this sequence's file; mutations are
    /// refused until the run reaches a terminal state (stop/abort it first).</summary>
    private static IResult RunActiveProblem(Guid id, string verb) =>
        Results.Problem(
            type: "https://openastro.net/errors/sequence-run-active",
            title: "Sequence has an active run",
            statusCode: StatusCodes.Status409Conflict,
            detail: $"Sequence {id} cannot be {verb} while its run is active. Stop or abort the run first.");

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
                    var (valid, reason) = SequenceSchemaValidator.Validate(request.Body);
                    if (!valid) return Results.UnprocessableEntity(new { error = reason });
                    var dto = await svc.CreateAsync(request, key, ct);
                    return Results.Created($"/api/v1/sequences/{dto.Id}", dto);
                })
           .Accepts<SequenceCreateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .WithName("CreateSequence");

        // §38.5 dry-run validation: run a raw body through the schema validator
        // without persisting. Always 200 — the body reports valid/reason, so the
        // editor can surface a problem before the user saves or runs.
        seq.MapPost("/validate",
                (SequenceValidateRequestDto request) => {
                    var (valid, reason) = SequenceSchemaValidator.Validate(request.Body);
                    return Results.Ok(new SequenceValidationResultDto(valid, reason));
                })
           .Accepts<SequenceValidateRequestDto>("application/json")
           .Produces<SequenceValidationResultDto>(StatusCodes.Status200OK)
           .WithName("ValidateSequence");

        seq.MapPatch("/{id:guid}",
                async (Guid id, [FromBody] SequenceUpdateRequestDto request, ISequenceService svc, CancellationToken ct) => {
                    // §38.5: only validate Body if it's being updated (PATCH semantics —
                    // null Body means "leave existing body unchanged").
                    if (request.Body.HasValue) {
                        var (valid, reason) = SequenceSchemaValidator.Validate(request.Body.Value);
                        if (!valid) return Results.UnprocessableEntity(new { error = reason });
                    }
                    var result = await svc.UpdateAsync(id, request, ct);
                    if (result.RunActive) return RunActiveProblem(id, "updated");
                    return result.Sequence is null ? Results.NotFound() : Results.Ok(result.Sequence);
                })
           .Accepts<SequenceUpdateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .WithName("UpdateSequence");

        seq.MapDelete("/{id:guid}",
                async (Guid id, ISequenceService svc, CancellationToken ct) =>
                    await svc.DeleteAsync(id, ct) switch {
                        SequenceDeleteResult.Deleted => Results.NoContent(),
                        SequenceDeleteResult.RunActive => RunActiveProblem(id, "deleted"),
                        _ => Results.NotFound(),
                    })
           .Produces(StatusCodes.Status204NoContent)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .ProducesProblem(StatusCodes.Status409Conflict)
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

        // §38 — skip whatever the run is currently executing (e.g. a target that's no longer
        // well-positioned in the sky), advancing the sequence to the next item.
        seq.MapPost("/{id:guid}/skip-current",
                async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, ISequencerService svc, CancellationToken ct) =>
                    Results.Accepted(value: await svc.SkipAsync(id, key, ct)))
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status409Conflict)
           .WithName("SkipCurrentSequence");

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


        // Phase 13.15 — Templates (§38.6, §38.7) wired to ISequenceTemplateService.
        seq.MapGet("/templates",
                async (ISequenceTemplateService svc, CancellationToken ct) =>
                    Results.Ok(await svc.ListAsync(ct)))
           .Produces<IReadOnlyList<SequenceTemplateDto>>(StatusCodes.Status200OK)
           .WithName("ListSequenceTemplates");

        seq.MapPost("/templates/{name}/instantiate",
                async (string name, [FromBody] TemplateInstantiateRequestDto request, ISequenceTemplateService svc, CancellationToken ct) => {
                    // Convert unknown-template signals to 404 at the
                    // endpoint layer rather than coupling to a concrete
                    // service type — keeps the contract clean when the
                    // real ISequenceTemplateService impl lands.
                    try {
                        var dto = await svc.InstantiateAsync(name, request, ct);
                        return Results.Created($"/api/v1/sequences/{dto.Id}", dto);
                    } catch (KeyNotFoundException) {
                        return Results.NotFound();
                    }
                })
           .Accepts<TemplateInstantiateRequestDto>("application/json")
           .Produces<SequenceDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("InstantiateSequenceTemplate");

        // Phase 13.15 — NINA import (§38.4) wired to ISequenceImportService.
        seq.MapPost("/import",
                async ([FromBody] SequenceImportRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, ISequenceImportService svc, CancellationToken ct) => {
                    // The one create-style POST that never declared the key (2026-07-15
                    // audit) — a retried import always duplicated the whole sequence.
                    if (ImportReplays.TryGet(key) is { } replay) {
                        return Results.Created($"/api/v1/sequences/{replay.CreatedSequenceId}", replay);
                    }
                    var result = await svc.ImportAsync(request, ct);
                    ImportReplays.Record(key, result);
                    return Results.Created($"/api/v1/sequences/{result.CreatedSequenceId}", result);
                })
           .Accepts<SequenceImportRequestDto>("application/json")
           .Produces<SequenceImportResultDto>(StatusCodes.Status201Created)
           .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
           .WithName("ImportNinaSequence");

        // Sharing (§70) — Phase 13.13 wired to ISequenceService.
        seq.MapPost("/{id:guid}/share-export",
                async (Guid id, ISequenceService svc, CancellationToken ct) => {
                    var share = await svc.ShareExportAsync(id, ct);
                    return share is null ? Results.NotFound() : Results.Ok(share);
                })
           .Produces<SequenceShareDto>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("ShareExportSequence");

        // Phase 13.15 — Auto-flats decision (§48) wired to IAutoFlatsService.
        // Existence-check via ISequenceService first per the §48 contract —
        // matches the matching-flats/mosaic-panels pattern.
        seq.MapPost("/{id:guid}/auto-flats-decision",
                async (Guid id, [FromBody] AutoFlatsDecisionRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? key, IAutoFlatsService svc, ISequenceService sequences, CancellationToken ct) => {
                    var sequence = await sequences.GetAsync(id, ct);
                    if (sequence is null) return Results.NotFound();
                    return Results.Accepted(value: await svc.ProvideDecisionAsync(id, request, key, ct));
                })
           .Accepts<AutoFlatsDecisionRequestDto>("application/json")
           .Produces<OperationAcceptedDto>(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status404NotFound)
           .WithName("DecideAutoFlats");

        return app;
    }
}