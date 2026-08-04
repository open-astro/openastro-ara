#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Stretch;

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

public enum PreviewChannelMode {
    Rgb,
    Luminance,
    Red,
    Green,
    Blue,
}

public sealed record PreviewCacheOptions(
    long MaxBytes = 512L * 1024 * 1024,
    int MaxEntries = 2048,
    int MaxVariantsPerFrame = 12,
    int MaxDimension = 4096) {

    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxVariantsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDimension);
    }
}

public sealed record PreviewRenderRequest(
    Guid FrameId,
    string SourcePath,
    string? SourceChecksumSha256,
    StretchAlgorithm Algorithm,
    StretchParams Parameters,
    int MaxDimension,
    bool ApplyDebayer,
    PreviewChannelMode ChannelMode,
    bool Invert,
    double Saturation,
    int? CropX,
    int? CropY,
    int? CropWidth,
    int? CropHeight,
    bool AnnotateStars = false,
    StarAnnotationOptions? AnnotationOptions = null,
    double StarSensitivity = 8.0,
    int StarNoiseReduction = 0);

public sealed record PreviewCacheMetadata(
    int SchemaVersion,
    Guid FrameId,
    string SourceChecksumSha256,
    string CacheKey,
    int Width,
    int Height,
    string Algorithm,
    StretchParams AppliedParameters,
    string DebayerMode,
    string ChannelMode,
    bool Inverted,
    double Saturation,
    DateTimeOffset CreatedUtc,
    bool Annotated = false,
    int AnnotationCount = 0,
    int RejectedAnnotationCount = 0,
    string? AnnotationColor = null,
    bool AnnotationLabels = false);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "The encoded preview buffer transfers directly to the HTTP response without another full-image copy.")]
public readonly record struct FramePreviewResult(
    byte[] Bytes,
    string ContentType,
    PreviewCacheMetadata Metadata,
    bool CacheHit);

/// <summary>Versioned, durable star-analysis measurement for one source frame.</summary>
public sealed record FrameAnalysisMeasurement(
    double Hfr,
    int StarCount,
    double? Eccentricity,
    double? SnrEstimate,
    string AnalysisVersion);

public sealed record FrameReanalysisResult(
    Guid FrameId,
    FrameAnalysisMeasurement Measurement,
    bool Persisted,
    string? Warning);

public sealed record FrameMetadataResult(
    FrameDto Frame,
    FrameStorageRecord? Storage,
    bool SourceExists,
    string? SourceChecksumSha256,
    string? ImageFormat,
    string? CfaPattern,
    string? AnalysisState,
    string? AnalysisFailureCode,
    string? AnalysisFailureMessage,
    string? PreviewState,
    string? PreviewFailureCode,
    string? PreviewFailureMessage,
    string? PreviewChecksum,
    string? DebayerMethod,
    string? PreviewVersion);

public interface IPreviewImageService {
    Task<FramePreviewResult> RenderAsync(PreviewRenderRequest request, CancellationToken ct);

    Task DeleteFrameEntriesAsync(Guid frameId, CancellationToken ct);
}

public interface IFrameOperationService {
    Task<OperationAcceptedDto?> RebuildPreviewAsync(Guid frameId,
        FramePreviewRequestDto request, string? idempotencyKey, CancellationToken ct);

    Task<OperationAcceptedDto?> ReanalyzeAsync(Guid frameId,
        FrameReanalysisRequestDto request, string? idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Durable capture/storage lifecycle. Terminal states are <see cref="Complete"/>,
/// <see cref="Failed"/>, and <see cref="Partial"/>. Partial means recoverable
/// bytes may remain on disk and require startup reconciliation or operator review.
/// </summary>
public enum FrameStorageState {
    Accepted,
    Exposing,
    Downloading,
    Persisting,
    Complete,
    Failed,
    Partial,
}

/// <summary>Information known before camera work starts.</summary>
public sealed record FrameStorageAttempt(
    Guid FrameId,
    Guid SessionId,
    DateTimeOffset AcceptedUtc,
    string TemporaryPath,
    string FinalPath,
    string ImageFormat);

/// <summary>Integrity information measured from the committed source file.</summary>
public sealed record FrameStorageCompletion(
    long ByteCount,
    string ChecksumSha256,
    DateTimeOffset CompletedUtc,
    string ImageFormat,
    string? CfaPattern);

/// <summary>Safe, persisted failure information. Do not put exception dumps in Message.</summary>
public sealed record FrameStorageFailure(
    string Code,
    string Message,
    DateTimeOffset FailedUtc);

/// <summary>Durable storage ledger row used by recovery and diagnostics.</summary>
public sealed record FrameStorageRecord(
    Guid FrameId,
    Guid SessionId,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset? CompletedUtc,
    string? TemporaryPath,
    string FinalPath,
    long? ByteCount,
    string? ChecksumSha256,
    string ImageFormat,
    string? CfaPattern,
    FrameStorageState State,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset UpdatedUtc);

public interface IFrameRepository {
    /// <summary>
    /// §14e capture write-path: inserts a newly captured frame row (the camera service writes the
    /// FITS file first, then registers it here so previews/downloads serve immediately).
    /// </summary>
    Task InsertAsync(FrameDto frame, CancellationToken ct);

    /// <summary>Create the durable ledger row before camera work starts.</summary>
    Task BeginStorageAsync(FrameStorageAttempt attempt, CancellationToken ct);

    /// <summary>
    /// Advance a non-terminal lifecycle state. Repeating the same state is idempotent;
    /// backward transitions and transitions out of terminal states are rejected.
    /// </summary>
    Task AdvanceStorageAsync(Guid frameId, FrameStorageState state, CancellationToken ct);

    /// <summary>
    /// Insert the frame row and mark its lifecycle Complete in one SQLite transaction.
    /// The source checksum is stored in both the frame catalog and lifecycle ledger.
    /// </summary>
    Task CompleteStorageAsync(FrameDto frame, FrameStorageCompletion completion, CancellationToken ct);

    /// <summary>Persist a safe failure code/message without deleting recoverable bytes.</summary>
    Task FailStorageAsync(Guid frameId, FrameStorageFailure failure, CancellationToken ct);

    /// <summary>Read lifecycle state even when no completed frame row exists.</summary>
    Task<FrameStorageRecord?> GetStorageAsync(Guid frameId, CancellationToken ct);

    Task BeginAnalysisAsync(Guid frameId, CancellationToken ct);

    Task RecordAnalysisSkippedAsync(Guid frameId, int starCount,
        string code, string message, CancellationToken ct);

    Task FailAnalysisAsync(Guid frameId, string code, string message,
        CancellationToken ct);

    /// <summary>
    /// §59.5 post-capture star-analysis write-back: stamps versioned HFR, star count,
    /// eccentricity, and SNR measurements (computed off the capture path) and broadcasts
    /// <c>frame.analyzed</c>. A frame deleted between capture and analysis is a silent no-op.
    /// </summary>
    Task UpdateAnalysisAsync(Guid frameId, FrameAnalysisMeasurement measurement, CancellationToken ct);

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
    Task<FrameMetadataResult?> GetMetadataAsync(Guid id, CancellationToken ct);
    Task<FramePreviewResult?> GetPreviewAsync(Guid id, FramePreviewRequestDto request, CancellationToken ct);
    Task<FramePreviewResult?> RebuildPreviewAsync(Guid id,
        FramePreviewRequestDto request, CancellationToken ct);
    Task<FrameReanalysisResult?> ReanalyzeAsync(Guid id,
        FrameReanalysisRequestDto request, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(Guid id, CancellationToken ct);
    Task<(Stream FitsStream, string FileName)?> OpenDownloadAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// §18.I — load a catalogued frame's raw pixels as <see cref="OpenAstroAra.Image.Interfaces.IImageData"/>
    /// for plate-solving (reuses the same FITS read path as previews). The <paramref name="profileService"/>
    /// is the one the loaded image carries — the solver writes a temp FITS via the image's SaveToDisk, which
    /// needs a real profile. Null when the frame row or its FITS file is missing.
    /// </summary>
    Task<OpenAstroAra.Image.Interfaces.IImageData?> LoadImageDataAsync(Guid id, OpenAstroAra.Profile.Interfaces.IProfileService profileService, CancellationToken ct);

    /// <summary>
    /// §18.I — read a catalogued frame's own pointing from its <c>OBJCTRA</c>/<c>OBJCTDEC</c> FITS headers
    /// (both in J2000), to seed a near-solve when the caller supplies no explicit hint. Returns null when the
    /// frame row/file is missing or the headers are absent/unparseable — the solve then falls back to blind.
    /// RA is returned in degrees (not hours) so both header and body hints build coordinates uniformly.
    /// </summary>
    Task<(double RaDegrees, double DecDegrees)?> TryReadTargetCoordinatesAsync(Guid id, CancellationToken ct);
    Task<OperationAcceptedDto> BulkRateAsync(BulkRateRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkTagAsync(BulkTagRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkDeleteAsync(BulkDeleteRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkMoveAsync(BulkMoveRequestDto request, string? idempotencyKey, CancellationToken ct);
    Task<OperationAcceptedDto> BulkQuarantineAsync(BulkQuarantineRequestDto request,
        string? idempotencyKey, CancellationToken ct);
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

    /// <summary>§44.5 ack — marks the frame synced to the active target. The result
    /// distinguishes the failure causes so the endpoint can map them to distinct
    /// statuses (409 lost-slot / 422 unverified / 404 unknown frame).</summary>
    Task<BackupStreamAckResult> AckAsync(string hostname, BackupStreamAckRequestDto request, CancellationToken ct);
}

/// <summary>Outcome of a §44.5 ack.</summary>
public enum BackupStreamAckResult { Acked, NotHolder, UnverifiedRefused, UnknownFrame }

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