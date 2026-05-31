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
public interface IFrameRepository {
    Task<CursorPage<FrameListItemDto>> ListAsync(int limit, string? cursor, Guid? sessionId, string? targetName, CancellationToken ct);
    Task<FrameDto?> GetAsync(Guid id, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct);
    Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct);
    Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct);
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
    Task<OperationAcceptedDto> ResumeTargetAsync(Guid sessionId, ResumeTargetRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> RestretchAsync(Guid sessionId, SessionRestretchRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<HfrAnalysisDto?> GetHfrAnalysisAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>Backup stream per §44. Out-of-band frame fan-out to long-running backup processes.</summary>
public interface IBackupStreamService {
    Task<BackupSubscriptionDto> SubscribeAsync(CancellationToken ct);
    Task<BackupFrameDto?> ClaimAsync(BackupClaimRequestDto request, CancellationToken ct);
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
        DiagnosticEventDto @event,
        string? recommendedAction,
        bool? autoCorrectible,
        CancellationToken ct);
}
