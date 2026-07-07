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
// Phase 8 service interfaces per PORT_PLAYBOOK.md §8.1.
//
// FrameRepository, SessionRepository, BackupStream + Diagnostics replace
// NINA's WPF-thread-affinity image-history VM and the (Pi-only) periodic
// diagnostics worker. All async; CancellationToken-aware.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Frame catalog + per-frame previews/thumbnails/downloads (§40 + §65).
///
/// Previews are returned as PNG/JPEG byte streams from <see cref="GetPreviewAsync"/>
/// and <see cref="GetThumbnailAsync"/>. Raw FITS is served by the distinct
/// <see cref="OpenDownloadAsync"/> method (per §72) which streams the original
/// frame file — used by the WILMA library "Download original" action.
/// </summary>
/// <summary>§39.10 export plan for STREAMING with one file handle at a time
/// (the r1 FD-exhaustion fix — Pi-class ulimits are low, so files open
/// as-you-go in the endpoint's stream callback, never as a batch). Entries
/// carry paths + pre-deduped tar names for files that existed at plan time;
/// the count is therefore BEST-EFFORT — a file vanishing before its turn
/// streams is skipped at open, and per-entry failure after open cannot occur
/// (an open handle can't vanish), keeping the tar aligned with no rollback.</summary>
public sealed record FrameExportPrep(
    IReadOnlyList<(string Path, string EntryName)> Entries,
    string FileName);

public interface IFrameRepository {
    /// <summary>
    /// §14e capture write-path: inserts a newly captured frame row (the camera service writes the
    /// FITS file first, then registers it here so previews/downloads serve immediately).
    /// </summary>
    Task InsertAsync(FrameDto frame, CancellationToken ct);

    /// <summary>
    /// §14e — id of the lazily-created "manual capture" session that REST-initiated exposures
    /// attach to. Idempotent per daemon lifetime. Sequence runs do NOT use this — they open
    /// their own per-run session (see <see cref="CreateRunSessionAsync"/> + CaptureSessionScope).
    /// </summary>
    Task<Guid> EnsureManualCaptureSessionAsync(CancellationToken ct);

    /// <summary>
    /// §40/§50 — open a fresh catalog session for one sequence run, so the run's frames group
    /// per-run in the library and stats instead of joining the shared manual bucket. The
    /// session's display/target name is derived at read time from its frames (per §28.1), so
    /// no name is stored here.
    /// </summary>
    Task<Guid> CreateRunSessionAsync(CancellationToken ct);

    /// <summary>
    /// §40/§50 — stamp a run session's end time (idempotent: only a still-open session is
    /// touched). Called from the run worker's teardown on every terminal path.
    /// </summary>
    Task EndSessionAsync(Guid sessionId, CancellationToken ct);

    Task<CursorPage<FrameListItemDto>> ListAsync(int limit, string? cursor, Guid? sessionId, string? targetName, CancellationToken ct);
    Task<FrameDto?> GetAsync(Guid id, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct);
    Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// §18.I — load a catalogued frame's raw pixels as <see cref="OpenAstroAra.Image.Interfaces.IImageData"/>
    /// for plate-solving (reuses the same FITS read path as previews). The <paramref name="profileService"/>
    /// is the one the loaded image carries — the solver writes a temp FITS via the image's SaveToDisk, which
    /// needs a real profile. Null when the frame row or its FITS file is missing.
    /// </summary>
    Task<OpenAstroAra.Image.Interfaces.IImageData?> LoadImageDataAsync(Guid id, OpenAstroAra.Profile.Interfaces.IProfileService profileService, CancellationToken ct);
    Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkMoveAsync(BulkMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
    /// <summary>§39.10 export: a tar stream of the selected frames' FITS files.
    /// Frames whose files are missing on disk are skipped; null when NOTHING
    /// was exportable (unknown ids or all files gone) — the endpoint 404s.</summary>
    Task<FrameExportPrep?> PrepareExportAsync(BulkExportRequestDto request, CancellationToken ct);
    /// <summary>
    /// §65.6 cache reset: delete all alt-stretch variants for a frame.
    /// Returns true if the frame exists, false if not found (→ 404).
    /// </summary>
    Task<bool> DeletePreviewVariantsAsync(Guid id, CancellationToken ct);
}

/// <summary>Session catalog + per-session operations (§40, §65).</summary>
public interface ISessionService {
    Task<CursorPage<SessionDto>> ListAsync(int limit, string? cursor, CancellationToken ct);
    Task<SessionDto?> GetAsync(Guid id, CancellationToken ct);
    Task<CursorPage<FrameListItemDto>> GetFramesAsync(Guid sessionId, int limit, string? cursor, CancellationToken ct);
    Task<ResumeTargetResultDto> ResumeTargetAsync(Guid sessionId, ResumeTargetRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> RestretchAsync(Guid sessionId, SessionRestretchRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<HfrAnalysisDto?> GetHfrAnalysisAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>Backup stream per §44. Out-of-band frame fan-out to long-running backup processes.</summary>
public interface IBackupStreamService {
    /// <summary>§44.5 — enabled/active-target/pending/synced/queue-bytes rollup.</summary>
    Task<BackupStreamStatusDto> GetStatusAsync(CancellationToken ct);

    /// <summary>§44.3 single-target claim. The same hostname re-claims its own slot
    /// idempotently (crash recovery); a different hostname gets null (→ 409 with the
    /// holder's name) unless the holder has been silent past the stale window.</summary>
    Task<BackupStreamClaimResultDto?> ClaimAsync(BackupStreamClaimRequestDto request, CancellationToken ct);

    /// <summary>Voluntary release. Only the holding hostname releases; anyone else is a no-op. Returns whether a slot was released.</summary>
    Task<bool> ReleaseAsync(BackupStreamClaimRequestDto request, CancellationToken ct);

    /// <summary>§44.5 pending queue, oldest first: catalogued frames not yet acked by the active target.
    /// Serving an entry lazily computes + caches its sha256 when missing. Null when the caller doesn't hold the slot.</summary>
    Task<IReadOnlyList<BackupStreamQueueEntryDto>?> GetQueueAsync(string hostname, int limit, CancellationToken ct);

    /// <summary>§44.5 ack — marks the frame synced to the active target. False for an unknown frame or a non-holder.</summary>
    Task<bool> AckAsync(string hostname, BackupStreamAckRequestDto request, CancellationToken ct);
}

/// <summary>Diagnostics monitor (§51). Worker emits §60.9 WS events on state changes.</summary>
public interface IDiagnosticsService {
    Task<DiagnosticsStateDto> GetStateAsync(CancellationToken ct);
    Task<DiagnosticsStateDto> SetModeAsync(DiagnosticsModeRequestDto request, CancellationToken ct);
    Task<CursorPage<DiagnosticEventDto>> GetHistoryAsync(int limit, string? cursor, CancellationToken ct);

    /// <summary>
    /// Insert a diagnostic event from a server-side emitter (e.g. §28.2
    /// startup reconciler on a Corrupt outcome, §38 sequence-lifecycle
    /// monitor, equipment failure handlers). Endpoints don't call this —
    /// it's for the §51 monitor pipeline. <paramref name="event"/>
    /// supplies the wire shape; emitter can additionally pass an optional
    /// <paramref name="recommendedAction"/> + <paramref name="autoCorrectible"/>
    /// for the columns absent from the read DTO.
    /// </summary>
    Task CreateEventAsync(
        DiagnosticEventDto diagnosticEvent,
        string? recommendedAction,
        bool? autoCorrectible,
        CancellationToken ct);

    /// <summary>
    /// Resolve every still-open event (cleared_utc IS NULL) of a given type by stamping
    /// <paramref name="clearedUtc"/>. For transient-condition monitors (e.g. the §29 disk-space monitor)
    /// that open an issue when a signal degrades and need to close it on recovery — <see cref="CreateEventAsync"/>
    /// only inserts, so this is the matching "clear" half. Returns the number of events cleared.
    /// </summary>
    Task<int> ClearOpenEventsByTypeAsync(string eventType, DateTimeOffset clearedUtc, CancellationToken ct);
}
