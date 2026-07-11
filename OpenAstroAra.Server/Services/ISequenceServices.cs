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

    /// <summary>§38 — mutating a sequence with an ACTIVE run is refused at the daemon
    /// (the endpoint maps <c>RunActive</c> to 409): a second client or bare curl must
    /// not rewrite the file out from under a live executor. The client's probe-first
    /// guards remain UX sugar on top of this invariant.</summary>
    Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct);

    /// <summary>§38 — same active-run refusal as <see cref="UpdateAsync"/>.</summary>
    Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>§70.5 share-export. Returns null for an unknown id (the endpoint maps
    /// that to 404); otherwise a share carrying the sequence body inline in
    /// <c>Manifest</c>, mirroring the profile-share contract.</summary>
    Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct);
}

/// <summary>§38 — outcome of a sequence delete. Split three ways (not a bool) so the
/// endpoint can tell "gone" (404) from "refused: a live run owns this file" (409) —
/// mirrors <see cref="PackageDeleteResult"/>.</summary>
public enum SequenceDeleteResult {
    Deleted,
    NotFound,
    RunActive,
}

/// <summary>§38 — outcome of a sequence update. <c>RunActive</c> = refused (409);
/// otherwise <c>Sequence</c> is the updated dto, or null for an unknown id (404).</summary>
public sealed record SequenceUpdateResult(SequenceDto? Sequence, bool RunActive) {
    public static readonly SequenceUpdateResult NotFound = new(null, false);
    public static readonly SequenceUpdateResult Refused = new(null, true);
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

    /// <summary>
    /// §35 — arm the instruction-boundary pause gate on every active run (the safety-reaction engine's
    /// <c>pause_and_park</c> action). Unlike <see cref="PauseAsync"/> this is a daemon-automated action, so it
    /// deliberately does NOT count as §58.12 user activity. Returns the ids of the runs a pause was requested
    /// on, so the caller can resume exactly those runs when conditions clear.
    /// </summary>
    Task<IReadOnlyList<Guid>> PauseActiveRunsAsync(CancellationToken ct);

    /// <summary>
    /// §35 — resume the given runs (the safety engine's auto-resume when conditions turn safe again). Same
    /// CAS + gate-release semantics as <see cref="ResumeAsync"/> but daemon-automated (no §58.12 user-activity
    /// signal), and it never resurrects a <c>PausedAwaitingUser</c> run — that state means a human is owed and
    /// only an explicit user command clears it. Returns the count of runs whose gate was released.
    /// </summary>
    Task<int> ResumeRunsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// §42.2 — skip the currently-executing instructions of every active run (the guider-fault flow's
    /// <c>skip_target</c> action). Same semantics as <see cref="SkipAsync"/> per run, but daemon-automated
    /// (no §58.12 user-activity signal). Returns the count of runs a skip was issued on.
    /// </summary>
    Task<int> SkipActiveRunsAsync(CancellationToken ct);

    /// <summary>
    /// §35 — the run's live deep-sky target coordinates, or null when the run/plan carries none
    /// (a plan with no coordinate-bearing target container, or an unknown run). Prefers the
    /// RUNNING target container over the plan's first, so a multi-target plan reports the one a
    /// pause interrupted. Best-effort read of the live plan tree; the safety engine's
    /// auto-resume re-centering consumes this.
    /// </summary>
    Task<OpenAstroAra.Astrometry.Coordinates?> GetActiveTargetCoordinatesAsync(Guid id, CancellationToken ct);
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
    Task<GeneratedFlatSequenceDto> GenerateMatchingFlatsAsync(Guid sessionId, MatchingFlatsRequestDto request, string? idempotencyKey, CancellationToken ct);
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