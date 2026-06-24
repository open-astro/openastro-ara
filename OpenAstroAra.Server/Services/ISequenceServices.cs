#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

// ────────────────────────────────────────────────────────────────────────────
// Phase 7 service interfaces per PORT_PLAYBOOK.md §8.1.
//
// Same pattern as the Phase 6 equipment services: interfaces land here,
// implementations land per area as the underlying NINA sequencer engine is
// migrated off WPF threading. Endpoints return 501 NotImplemented until the
// implementations are registered.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Replaces NINA's <c>ISequenceMediator</c>. CRUD + lifecycle ownership for
/// stored sequences; runtime control delegates to <see cref="ISequencerService"/>.
/// </summary>
public interface ISequenceService {
    Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct);
    Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct);
    Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    /// <summary>§70.5 share-export. Returns null for an unknown id (the endpoint maps
    /// that to 404); otherwise a share carrying the sequence body inline in
    /// <c>Manifest</c>, mirroring the profile-share contract.</summary>
    Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Runtime control. Wraps the legacy <c>ISequencer</c> with a thread-safe
/// async API; emits §60.9 WS events for every state transition.
/// </summary>
public interface ISequencerService {
    Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct);
    Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> SkipAsync(Guid id, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct);

    /// <summary>
    /// §29 — abort every still-running sequence (the disk-space monitor's "abort on critical" policy uses this
    /// so a full disk halts capture rather than failing frame-by-frame). Returns the count of runs asked to abort.
    /// </summary>
    Task<int> AbortActiveRunsAsync(CancellationToken ct);
}

/// <summary>Templates per §38.6 / §38.7 — built-ins + user-saved.</summary>
public interface ISequenceTemplateService {
    Task<IReadOnlyList<SequenceTemplateDto>> ListAsync(CancellationToken ct);
    Task<SequenceDto> InstantiateAsync(string templateName, TemplateInstantiateRequestDto request, CancellationToken ct);
}

/// <summary>NINA sequence-file import per §38.4. Lossy translation flagged on response.</summary>
public interface ISequenceImportService {
    Task<SequenceImportResultDto> ImportAsync(SequenceImportRequestDto request, CancellationToken ct);
}

/// <summary>Auto-flats interactive prompt per §48. Captures user decision mid-sequence.</summary>
public interface IAutoFlatsService {
    Task<OperationAcceptedDto> ProvideDecisionAsync(Guid sequenceId, AutoFlatsDecisionRequestDto request, string? idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Calibration session inventory + matching flats generation (§39). Reads from
/// the §40 frame repository to figure out what filters/exposures were used per
/// session, then generates a corresponding flat-capture sequence on request.
/// </summary>
public interface ICalibrationService {
    Task<CursorPage<CalibrationSessionDto>> ListSessionsAsync(int limit, string? cursor, CancellationToken ct);
    Task<CalibrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct);
    Task<GeneratedFlatSequenceDto> GenerateMatchingFlatsAsync(Guid sessionId, MatchingFlatsRequestDto request, CancellationToken ct);
}

/// <summary>Dark library builder + state (§39 / §63).</summary>
public interface IDarkLibraryService {
    Task<OperationAcceptedDto> StartBuildAsync(DarkLibraryBuildRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<DarkLibraryStateDto> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<DarkLibraryEntryDto>> ListEntriesAsync(CancellationToken ct);
}

/// <summary>Mosaic plan + per-panel progress (§47).</summary>
public interface IMosaicService {
    Task<CursorPage<MosaicDto>> ListAsync(int limit, string? cursor, CancellationToken ct);
    Task<MosaicDto?> GetAsync(Guid id, CancellationToken ct);
    Task<MosaicDto> CreateAsync(MosaicCreateRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MosaicPanelDto>> GetPanelsAsync(Guid mosaicId, CancellationToken ct);
    Task<MosaicProgressDto?> GetProgressAsync(Guid mosaicId, CancellationToken ct);
}