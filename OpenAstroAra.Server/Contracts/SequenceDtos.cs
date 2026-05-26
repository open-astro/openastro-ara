#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// PORT_PLAYBOOK.md §10.7 + §38 (sequence schema)
//
// Phase 7 sequence DTOs. Schema mirrors the NINA sequence-file structure
// (§38.1) but with these adjustments:
//   - All identifiers are GUIDs (§38.2), not auto-increment ints.
//   - Created/Modified timestamps are RFC 3339 strings, UTC only (§60.6).
//   - Sequence runtime state is a discriminated state machine, not free-form.
//   - NINA legacy JSON shapes accepted only via the import endpoint (§38.4),
//     never as the canonical wire format.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Sequence lifecycle state machine per §28.12.</summary>
public enum SequenceRunState {
    Idle,
    Starting,
    Running,
    Paused,
    Aborting,
    Stopped,
    Completed,
    Failed
}

/// <summary>List item for /api/v1/sequences (paginated per §60.2).</summary>
public sealed record SequenceListItemDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    SequenceRunState? CurrentRunState,
    int InstructionCount,
    int TargetCount,
    string? TemplateOrigin);

/// <summary>Full sequence detail. The <c>Body</c> is a JSON DOM matching §38.1.</summary>
public sealed record SequenceDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    System.Text.Json.JsonElement Body,
    string? TemplateOrigin);

/// <summary>POST /api/v1/sequences body.</summary>
public sealed record SequenceCreateRequestDto(
    string Name,
    string? Description,
    System.Text.Json.JsonElement Body,
    string? TemplateOrigin);

/// <summary>PUT /api/v1/sequences/{id} body. Name + Description nullable so partial updates patch.</summary>
public sealed record SequenceUpdateRequestDto(
    string? Name,
    string? Description,
    System.Text.Json.JsonElement? Body);

/// <summary>POST /api/v1/sequences/{id}/start body.</summary>
public sealed record SequenceStartRequestDto(
    bool DryRun,
    int? StartFromInstructionIndex,
    bool ContinueOnRecoverableErrors);

/// <summary>Runtime state for an active sequence run (live via WS sequence.progress).</summary>
public sealed record SequenceRunStateDto(
    Guid SequenceId,
    Guid RunId,
    SequenceRunState State,
    int? CurrentInstructionIndex,
    string? CurrentTargetName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    int FramesCompleted,
    int FramesTotal,
    string? CurrentInstructionDescription);

/// <summary>Per-instruction progress payload (WS sequence.instruction_started / _complete / _failed).</summary>
public sealed record InstructionProgressDto(
    Guid SequenceId,
    Guid RunId,
    int InstructionIndex,
    string InstructionType,
    string? Description,
    string Status,
    DateTimeOffset Timestamp,
    string? FailureReason);

/// <summary>List item for /api/v1/sequences/templates per §38.6.</summary>
public sealed record SequenceTemplateDto(
    string Name,
    string Category,
    string? Description,
    bool IsBuiltIn,
    System.Text.Json.JsonElement Body);

/// <summary>POST /api/v1/sequences/templates/{name}/instantiate body.</summary>
public sealed record TemplateInstantiateRequestDto(
    string NewSequenceName,
    System.Text.Json.JsonElement? Parameters);

/// <summary>POST /api/v1/sequences/import body per §38.4.</summary>
public sealed record SequenceImportRequestDto(
    string NewName,
    System.Text.Json.JsonElement NinaSequenceFile,
    bool TreatWarningsAsErrors);

/// <summary>Result of NINA import (§38.4). Warnings explain dropped/translated instructions.</summary>
public sealed record SequenceImportResultDto(
    Guid CreatedSequenceId,
    string Name,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DroppedInstructionTypes,
    bool LossyTranslation);

/// <summary>POST /api/v1/sequences/{id}/share-export body. Returns SequenceShareDto per §70.</summary>
public sealed record SequenceShareDto(
    Guid SequenceId,
    string SequenceName,
    string ShareFormat,
    System.Text.Json.JsonElement Manifest,
    long PayloadBytes,
    string DownloadUrl);

/// <summary>POST /api/v1/sequences/{id}/auto-flats-decision body per §48.</summary>
public sealed record AutoFlatsDecisionRequestDto(
    Guid SequenceRunId,
    bool ConfirmRun,
    int? OverrideFlatFrameCount,
    int? OverrideExposureMs);
