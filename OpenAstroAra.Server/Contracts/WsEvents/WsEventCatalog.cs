#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts.WsEvents;

// ────────────────────────────────────────────────────────────────────────────
// PORT_PLAYBOOK.md §10.10 + §60.9 (WebSocket protocol)
//
// Single source-of-truth catalog for every event type the server may publish
// on /api/v1/ws. Used by:
//   1. The WS broadcaster (§60.9) when emitting frames.
//   2. The OpenAPI/AsyncAPI generator (§71.3) when documenting the contract.
//   3. The §17 contract-check fixture which validates that every event the
//      runtime can emit appears in this catalog.
//
// To add a new event:
//   1. Add the type token here (kebab/dot-notation).
//   2. Document the payload schema in openapi.yaml under #/components/schemas/Ws*.
//   3. Register the emit-point with IWsBroadcaster.
//
// Event envelope is { "type": "<token>", "ts": "<rfc3339>",
// "seq": <int64>, "payload": <obj> }. Sequence numbers are monotonic across
// all events from a single server connection; resume reconnect re-delivers
// from a client-provided seq + 1.
// ────────────────────────────────────────────────────────────────────────────

public static class WsEventCatalog {

    // Phase 6 — equipment state machine
    public const string EquipmentStateChanged = "equipment.state_changed";
    public const string EquipmentConnected = "equipment.connected";
    public const string EquipmentDisconnected = "equipment.disconnected";
    public const string EquipmentConnectionFailed = "equipment.connection_failed";
    public const string EquipmentDiscoveryRefreshed = "equipment.discovery_refreshed";
    public const string CameraExposureStarted = "camera.exposure_started";
    public const string CameraExposureComplete = "camera.exposure_complete";
    public const string CameraExposureFailed = "camera.exposure_failed";
    public const string TelescopeSlewStarted = "telescope.slew_started";
    public const string TelescopeSlewComplete = "telescope.slew_complete";
    public const string TelescopeParkChanged = "telescope.park_changed";
    public const string GuiderState = "guider.state";
    public const string GuiderDitherComplete = "guider.dither_complete";

    // Phase 7 — sequence
    public const string SequenceCreated = "sequence.created";
    public const string SequenceUpdated = "sequence.updated";
    public const string SequenceDeleted = "sequence.deleted";
    public const string SequenceStarted = "sequence.started";
    public const string SequencePaused = "sequence.paused";
    public const string SequenceResumed = "sequence.resumed";
    public const string SequenceAborted = "sequence.aborted";
    public const string SequenceStopped = "sequence.stopped";
    public const string SequenceComplete = "sequence.complete";
    public const string SequenceInstructionStarted = "sequence.instruction_started";
    public const string SequenceInstructionComplete = "sequence.instruction_complete";
    public const string SequenceInstructionFailed = "sequence.instruction_failed";
    public const string SequenceProgress = "sequence.progress";
    public const string SequenceImported = "sequence.imported";
    public const string SequenceImportWarning = "sequence.import_warning";
    public const string SequenceAutoFlatsPrompt = "sequence.auto_flats_prompt";
    public const string SequenceAutoFlatsDecided = "sequence.auto_flats_decided";

    public const string CalibrationFlatsGenerated = "calibration.flats_generated";
    public const string DarkLibraryBuildStarted = "calibration.dark_library.build_started";
    public const string DarkLibraryFrameComplete = "calibration.dark_library.frame_complete";
    public const string DarkLibraryBuildComplete = "calibration.dark_library.build_complete";
    public const string DarkLibraryBuildFailed = "calibration.dark_library.build_failed";

    public const string MosaicCreated = "mosaic.created";
    public const string MosaicPanelComplete = "mosaic.panel_complete";
    public const string MosaicComplete = "mosaic.complete";

    // Phase 8 — frames, sessions, backup, diagnostics
    public const string FrameComplete = "frame.complete";
    public const string FrameRecoveredOrphan = "frame.recovered_orphan";
    public const string FramePreviewReady = "frame.preview.ready";
    public const string FramePreviewVariantReady = "frame.preview.variant.ready";
    public const string FramePreviewVariantEvicted = "frame.preview.variant.evicted";
    public const string FrameQualityScored = "frame.quality_scored";
    public const string FramesBulkOperationComplete = "frames.bulk_operation_complete";

    public const string SessionStarted = "session.started";
    public const string SessionEnded = "session.ended";
    public const string SessionRestretchProgress = "session.restretch.progress";
    public const string SessionRestretchComplete = "session.restretch.complete";
    public const string SessionRestretchFailed = "session.restretch.failed";

    public const string BackupStreamFrameAvailable = "backup.stream.frame_available";
    public const string BackupStreamFrameClaimed = "backup.stream.frame_claimed";
    public const string BackupStreamBackpressure = "backup.stream.backpressure";

    public const string DiagnosticsHealthChanged = "diagnostics.health_changed";
    public const string DiagnosticsIssueDetected = "diagnostics.issue_detected";
    public const string DiagnosticsAutoActionTaken = "diagnostics.auto_action_taken";
    public const string DiagnosticsAutoActionSkipped = "diagnostics.auto_action_skipped";
    public const string DiagnosticsCleared = "diagnostics.cleared";

    // Phase 9 — server lifecycle + notifications + storage
    public const string ServerPendingRestart = "server.pending_restart";
    public const string ServerRestartImminent = "server.restart_imminent";
    public const string ServerMigratingDatabase = "server.migrating_database";
    public const string ServerMigrationComplete = "server.migration_complete";
    public const string ServerMigrationFailed = "server.migration_failed";

    public const string StorageLogPressure = "storage.log_pressure";

    public const string NotificationPosted = "notification.posted";
    public const string NotificationDismissed = "notification.dismissed";
    public const string NotificationCleared = "notification.cleared";
    public const string NotificationAlarmStarted = "notification.alarm.started";
    public const string NotificationAlarmStopped = "notification.alarm.stopped";

    public const string BugReportPrepared = "bugreport.prepared";
    public const string BugReportSharingModeSet = "bugreport.sharing_mode_set";

    public const string DataManagerDownloadProgress = "data_manager.download.progress";
    public const string DataManagerDownloadComplete = "data_manager.download.complete";
    public const string DataManagerDownloadFailed = "data_manager.download.failed";

    public const string BackupZipCreated = "backup.zip_created";
    public const string BackupRestoreProgress = "backup.restore_progress";
    public const string BackupRestoreComplete = "backup.restore_complete";

    /// <summary>
    /// All registered event tokens. Iteration order is the order declared here.
    /// Used by the §17 contract-check to assert no runtime emit calls a token
    /// that isn't in this list. Exposed at runtime via <c>/api/v1/ws/catalog</c>
    /// so the WILMA client can validate as well (§60.9.4).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[] {
        EquipmentStateChanged, EquipmentConnected, EquipmentDisconnected,
        EquipmentConnectionFailed, EquipmentDiscoveryRefreshed,
        CameraExposureStarted, CameraExposureComplete, CameraExposureFailed,
        TelescopeSlewStarted, TelescopeSlewComplete, TelescopeParkChanged,
        GuiderState, GuiderDitherComplete,

        SequenceCreated, SequenceUpdated, SequenceDeleted,
        SequenceStarted, SequencePaused, SequenceResumed, SequenceAborted,
        SequenceStopped, SequenceComplete,
        SequenceInstructionStarted, SequenceInstructionComplete, SequenceInstructionFailed,
        SequenceProgress, SequenceImported, SequenceImportWarning,
        SequenceAutoFlatsPrompt, SequenceAutoFlatsDecided,

        CalibrationFlatsGenerated,
        DarkLibraryBuildStarted, DarkLibraryFrameComplete,
        DarkLibraryBuildComplete, DarkLibraryBuildFailed,

        MosaicCreated, MosaicPanelComplete, MosaicComplete,

        FrameComplete, FrameRecoveredOrphan,
        FramePreviewReady, FramePreviewVariantReady, FramePreviewVariantEvicted,
        FrameQualityScored, FramesBulkOperationComplete,

        SessionStarted, SessionEnded,
        SessionRestretchProgress, SessionRestretchComplete, SessionRestretchFailed,

        BackupStreamFrameAvailable, BackupStreamFrameClaimed, BackupStreamBackpressure,

        DiagnosticsHealthChanged, DiagnosticsIssueDetected,
        DiagnosticsAutoActionTaken, DiagnosticsAutoActionSkipped, DiagnosticsCleared,

        ServerPendingRestart, ServerRestartImminent,
        ServerMigratingDatabase, ServerMigrationComplete, ServerMigrationFailed,
        StorageLogPressure,
        NotificationPosted, NotificationDismissed, NotificationCleared,
        NotificationAlarmStarted, NotificationAlarmStopped,
        BugReportPrepared, BugReportSharingModeSet,
        DataManagerDownloadProgress, DataManagerDownloadComplete, DataManagerDownloadFailed,
        BackupZipCreated, BackupRestoreProgress, BackupRestoreComplete
    };
}

/// <summary>
/// Wire envelope for every event emitted on /api/v1/ws per §60.9.3.
/// Records are AOT-friendly + JSON-stable; the WS broadcaster serializes
/// these directly into client frames.
/// </summary>
public sealed record WsEventEnvelopeDto(
    string Type,
    DateTimeOffset Ts,
    long Seq,
    System.Text.Json.JsonElement Payload);

/// <summary>
/// Optional first message a client may send after the §60.9 WS upgrade
/// completes. <c>ResumeToken</c> is the opaque string returned by REST
/// <c>GET /api/v1/server/state.ws_resume_token</c> in a prior session;
/// for v0.0.1 it's the base-10 stringified last-seen sequence number.
/// </summary>
public sealed record WsResumeRequestDto(string? ResumeToken);

/// <summary>
/// Server response to a <see cref="WsResumeRequestDto"/>. Three shapes
/// per openapi.yaml §60.9 docs:
/// <list type="bullet">
///   <item><c>{ resumed: true,  missed_events: N, last_event_id: "..." }</c> → replay follows immediately</item>
///   <item><c>{ resumed: false, code: "resume_token_expired" }</c> → fresh subscription, no replay</item>
///   <item><c>{ resumed: false, code: "resume_token_invalid" }</c> → client should clear local cursor</item>
/// </list>
/// </summary>
public sealed record WsResumeResponseDto(
    bool Resumed,
    int? MissedEvents,
    string? LastEventId,
    string? Code,
    string? Reason);
