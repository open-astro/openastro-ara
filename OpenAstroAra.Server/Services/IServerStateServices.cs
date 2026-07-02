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
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Services;

// ────────────────────────────────────────────────────────────────────────────
// Phase 9 service interfaces per PORT_PLAYBOOK.md §8.1.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>State snapshot + lifecycle (§60.4, §34.7).</summary>
public interface IServerStateService {
    Task<ServerStateDto> GetSnapshotAsync(CancellationToken ct);
    Task<ApiVersionsDto> GetVersionsAsync(CancellationToken ct);
    Task<ReleaseNotesDto?> GetReleaseNotesAsync(string? version, CancellationToken ct);
    Task<OperationAcceptedDto> RestartAsync(string reason, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> RestartOnIdleAsync(string reason, string? idempotencyKey, CancellationToken ct);
}

/// <summary>Log endpoints (§29.9).</summary>
public interface ILogService {
    Task<OperationAcceptedDto> RotateAsync(string? idempotencyKey, CancellationToken ct);
    Task<(Stream Stream, string FileName)?> OpenDownloadAsync(string? logFileName, CancellationToken ct);
    Task<IReadOnlyList<LogEntryDto>> TailAsync(LogTailRequestDto request, CancellationToken ct);
}

/// <summary>Notifications (§46, §35.5).</summary>
public interface INotificationService {
    Task<CursorPage<NotificationDto>> ListAsync(int limit, string? cursor, bool? unreadOnly, CancellationToken ct);
    Task<NotificationDto?> DismissAsync(Guid id, NotificationActionRequestDto request, CancellationToken ct);
    Task<NotificationDto?> MarkReadAsync(Guid id, CancellationToken ct);
    Task<NotificationPreferenceDto> GetPreferencesAsync(CancellationToken ct);
    Task<NotificationPreferenceDto> SetPreferencesAsync(NotificationPreferenceDto preferences, CancellationToken ct);

    /// <summary>
    /// Insert a notification from a server-side emitter (e.g. §28.2 startup
    /// reconciler, §38 sequence lifecycle, §51 diagnostics monitor). Endpoints
    /// don't call this — it's for internal pipelines that need to surface a
    /// §46 entry to the inbox.
    /// </summary>
    Task CreateAsync(NotificationDto notification, CancellationToken ct);
}

/// <summary>Stats (§50). Per-view methods so an AOT-friendly switch picks the right serializer.</summary>
public interface IStatsService {
    Task<StatsOverviewDto> GetOverviewAsync(CancellationToken ct);
    Task<StatsTargetsDto> GetTargetsAsync(CancellationToken ct);
    Task<StatsFocusTempDto> GetFocusTempAsync(DateTimeOffset? since, CancellationToken ct);
    Task<StatsGuidingDto> GetGuidingAsync(DateTimeOffset? since, CancellationToken ct);
    Task<StatsFrameQualityDto> GetFrameQualityAsync(string? filter, CancellationToken ct);
    Task<StatsBestFramesDto> GetBestFramesAsync(int limit, CancellationToken ct);
    Task<StatsCalendarDto> GetCalendarAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct);
    Task<StatsAchievementsDto> GetAchievementsAsync(CancellationToken ct);
    Task<(Stream Stream, string FileName)?> OpenCsvExportAsync(string scope, CancellationToken ct);
    Task<(Stream Stream, string FileName)?> OpenAstrobinExportAsync(string targetName, CancellationToken ct);
}

/// <summary>Bug-report bundle preparation (§54).</summary>
public interface IBugReportService {
    Task<BugReportPreparationDto> PrepareAsync(string? idempotencyKey, CancellationToken ct);
    Task<(Stream Stream, string FileName)?> OpenDownloadAsync(Guid preparationId, CancellationToken ct);
}

/// <summary>Data Manager (§36.2).</summary>
/// <summary>§36 — outcome of a sky-data package delete. Split (not a bool) so callers can tell
/// "give up, it's gone" (<see cref="NotInstalled"/>) from "retry later, the files are locked or
/// permission was denied" (<see cref="Blocked"/>) — a locked directory must not be treated as
/// already-clear.</summary>
public enum PackageDeleteResult {
    Deleted,
    NotInstalled,
    Blocked,
}

public interface IDataManagerService {
    Task<IReadOnlyList<DataPackageDto>> ListPackagesAsync(CancellationToken ct);
    Task<OperationAcceptedDto> DownloadAsync(DownloadRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> CancelAsync(Guid downloadId, CancellationToken ct);
    Task<PackageDeleteResult> DeleteAsync(string packageId, CancellationToken ct);
    Task<DataManagerStateDto> GetStateAsync(CancellationToken ct);

    /// <summary>§36 — read an installed catalog package's objects for the Sky Atlas overlay, normalized to
    /// {name, ra°, dec°, mag}. Returns null when the package isn't a known catalog, isn't installed, or has no
    /// installed <c>catalog.csv</c> (the endpoint maps null to 404). <paramref name="maxMag"/> drops fainter objects;
    /// <paramref name="limit"/> caps the count (both optional).</summary>
    Task<IReadOnlyList<CatalogObjectDto>?> ReadCatalogAsync(string packageId, double? maxMag, int? limit, CancellationToken ct);
}

/// <summary>Backup (§43).</summary>
public interface IBackupService {
    Task<OperationAcceptedDto> CreateZipAsync(string? idempotencyKey, CancellationToken ct);

    /// <summary>§43-2: restore the selected config areas from a local snapshot (the request's source URL must be a
    /// snapshot-download URL). §43-2b: validates synchronously (snapshot-exists, manifest checksum) then runs the
    /// extract + atomic swap on a background worker, returning <c>202</c> immediately; poll <see cref="GetCloneStatusAsync"/>
    /// for progress. NOTE: the validation failures throw <em>synchronously</em> (not via a faulted task) — an unknown
    /// snapshot, unsupported source, no selected area, checksum mismatch, or a restore already in progress — which the
    /// endpoint maps to 404 / 422 / 409. A caller awaiting this inside a try/catch handles them either way.</summary>
    Task<OperationAcceptedDto> RestoreZipAsync(RestoreRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<IReadOnlyList<BackupZipDto>> ListSnapshotsAsync(CancellationToken ct);
    Task<System.Text.Json.JsonElement> GetCloneStatusAsync(CancellationToken ct);

    /// <summary>Open a snapshot's on-disk <c>.zip</c> for download, returning the read stream + its filename, or
    /// <c>null</c> if no such snapshot exists (or it vanished between resolve and open). Backs
    /// <c>GET /api/v1/backup/snapshot/{id}/download</c>. The id is a guid, so the resolved filename is derived from
    /// it (no caller-controlled path component → no traversal). The returned stream is owned by the response
    /// pipeline (disposed when the response finishes); opening the handle here closes the resolve→serve TOCTOU
    /// window (a delete after this returns can't turn the send into a 500).</summary>
    Task<(System.IO.Stream Stream, string FileName)?> OpenSnapshotAsync(Guid id, CancellationToken ct);
}

/// <summary>Profile sharing (§70).</summary>
public interface IProfileShareService {
    /// <summary>Render the §70.2 <c>profile-share-v1</c> manifest for a profile, or
    /// <c>null</c> if no profile has that id (the endpoint maps null → 404).</summary>
    Task<ProfileShareDto?> ExportAsync(Guid profileId, CancellationToken ct);
    Task<ProfileShareImportPreviewDto> ImportPreviewAsync(System.Text.Json.JsonElement manifest, CancellationToken ct);
    Task<Guid> ImportCommitAsync(Guid importToken, CancellationToken ct);
}

/// <summary>
/// WebSocket broadcaster (§60.9). Singleton; called by every service that
/// publishes an event. Sequence numbers are server-assigned + monotonic.
/// </summary>
public interface IWsBroadcaster {
    Task PublishAsync(string eventType, System.Text.Json.JsonElement payload, CancellationToken ct);
    long CurrentSequence { get; }
}

/// <summary>
/// Buffered event channel feeding <see cref="IWsBroadcaster"/>. Bounded so
/// fast emitters don't drown the consumer; backpressure surfaces as a
/// <c>backup.stream.backpressure</c> event when buffers approach capacity.
/// </summary>
public interface IWsEventChannel {
    Task EnqueueAsync(WsEventEnvelopeDto envelope, CancellationToken ct);
    IAsyncEnumerable<WsEventEnvelopeDto> ReadAllAsync(CancellationToken ct);
    Task<IReadOnlyList<WsEventEnvelopeDto>> ResumeFromAsync(long lastSeenSeq, CancellationToken ct);
}