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
//   1. Declare the event here as a WsEvent (kebab/dot-notation token) and add it to All; the broadcaster
//      accepts only a declared WsEvent (ADR 0014), and an event nothing emits does not belong here.
//   2. Document the payload in design/API_CONTRACT.md (WebSocket section); the
//      generated openapi.yaml cannot carry WebSocket shapes (#1131).
//   3. Publish it through IWsBroadcaster.
//   4. Refresh ws-events.json (OPENASTROARA_UPDATE_WS_EVENTS=1, WsEventSnapshotTest) and rerun
//      scripts/generate-ws-events-dart.py for the client's WsEventTypes.
//
// Event envelope is { "type": "<token>", "ts": "<rfc3339>",
// "seq": <int64>, "payload": <obj> }. Sequence numbers are monotonic across
// all events from a single server connection; resume reconnect re-delivers
// from a client-provided seq + 1.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A declared WebSocket event (ADR 0014). Only <see cref="WsEventCatalog"/> creates one, so
/// <see cref="IWsBroadcaster.PublishAsync"/> cannot be handed an undeclared string.
/// </summary>
public readonly record struct WsEvent {
    public string Token { get; }
    internal WsEvent(string token) { Token = token; }
    public override string ToString() => Token;
    public static implicit operator string(WsEvent e) => e.Token;
}

public static class WsEventCatalog {

    // Phase 6 — equipment state machine
    public static readonly WsEvent EquipmentStateChanged = new("equipment.state_changed");
    public static readonly WsEvent EquipmentConnected = new("equipment.connected");
    public static readonly WsEvent EquipmentDisconnected = new("equipment.disconnected");
    public static readonly WsEvent EquipmentConnectionFailed = new("equipment.connection_failed");
    // §42.2 — a DETECTED device fault (disconnect streak, stall, value mismatch, …); payload
    // { device_type, device_id, device_name, kind, details, detected_utc }. What was done about
    // it (retries/pause/abort+park) is the §42.3 reaction slice's separate event.
    public static readonly WsEvent EquipmentFault = new("equipment.fault");
    // §42.3 — what the FaultReactionService DID about a fault; payload { device_type, device_id,
    // device_name, kind, action, … } where action is sequence_paused | reconnecting | recovered
    // | gave_up | readopted | released | escalated | notify_only (gave_up carries the terminal action
    // taken; released = the user disconnected the device mid-recovery, #1307, so nothing more is done).
    public static readonly WsEvent EquipmentFaultActionTaken = new("equipment.fault_action_taken");
    // #1298 — the manual filter wheel asked for, or got, a hand swap; payload { pending_slot,
    // pending_name, current_slot, current_name } (pending_* null once the swap is resolved or
    // cancelled). Clients raise / drop their "install the X filter" prompt on it.
    public static readonly WsEvent FilterWheelManualSwap = new("equipment.filter_wheel.manual_swap");
    // §42.4 — a written switch port whose read-back disagrees with the commanded value beyond
    // the profile tolerance past the settle window; payload { device_id, device_name, port_id,
    // port_name, commanded, read_back, tolerance_pct }. Fires alongside the equipment.fault
    // broadcast (kind value_mismatch) with the structured per-port detail WILMA needs.
    public static readonly WsEvent SwitchValueMismatch = new("switch.value_mismatch");
    // Exposure lifecycle, published from the ONE device round-trip every capture takes
    // (CameraService.ExposeAndDownloadAsync): Take One, sequence lights/flats/darks, the §59
    // autofocus probe and the §28 plate-solve capture alike. started payload { frame_id,
    // exposure_sec, started_utc, kind } where kind is the lower-cased image type ("light",
    // "flat", …) or "analysis" / "plate-solve" for the unpersisted probes, plus filter_name when
    // the request named one. complete payload adds { elapsed_ms } (shutter open → pixels in
    // hand, so the download is INSIDE the window — the §44.4 backup stream treats the camera as
    // busy until then). failed payload { frame_id, kind, reason } for a device timeout,
    // disconnect/supersede, caller cancellation ("cancelled"), a REST abort ("aborted") or a
    // thrown device fault. A persisted frame's
    // frame.complete still follows the exposure_complete once the FITS is catalogued.
    public static readonly WsEvent CameraExposureStarted = new("camera.exposure_started");
    public static readonly WsEvent CameraExposureComplete = new("camera.exposure_complete");
    public static readonly WsEvent CameraExposureFailed = new("camera.exposure_failed");
    // §57.8 — slew lifecycle, published from observed IsSlewing transitions in the telescope
    // status poll (every slew source: REST, sequencer, flip, recovery, park/home). started
    // payload { target_ra_hours?, target_dec_degrees?, ra_hours?, dec_degrees? } (targets only
    // for coordinate slews); complete payload { final_ra_hours?, final_dec_degrees?,
    // duration_seconds }; aborted payload { halted_ra_hours?, halted_dec_degrees?, reason }
    // published immediately from the §57.4 panic-stop path (which suppresses that episode's
    // complete event).
    public static readonly WsEvent TelescopeSlewStarted = new("telescope.slew_started");
    public static readonly WsEvent TelescopeSlewComplete = new("telescope.slew_complete");
    public static readonly WsEvent TelescopeSlewAborted = new("telescope.slew_aborted");
    // §63.18 — one event per guide frame while the guider is connected, straight from the
    // daemon's GuideStep stream: payload { frame, time_sec, ra_raw_px, dec_raw_px,
    // ra_arcsec?, dec_arcsec? (null until the guider reports a pixel scale), ra_duration_ms,
    // dec_duration_ms (signed: negative = East / South, PHD2's own graph convention),
    // pixel_scale_arcsec?, star_mass?, snr? }. The client's Live-tab guide graph is built from
    // these; the RMS on GET /equipment/guider stays the daemon's windowed figure.
    public static readonly WsEvent GuiderStep = new("guider.step");
    // §63.18 — PHD2's non-step session events, one per PHD2 message, so the client can draw the
    // markers PHD2's own graph draws: payload { kind, ... } with kind = dithered {dx_px, dy_px} |
    // settling {distance_px, time_sec, settle_time_sec} | settle_done {status, error?} |
    // star_lost {frame?, star_mass?, snr?, distance_px?, status?, error?} | calibration_started |
    // calibrating {direction, frame = step, distance_px, dx_px, dy_px} (one per calibration step) |
    // calibration_complete | calibration_failed {error?} | guiding_started | guiding_stopped |
    // paused | resumed | lock_position_lost | guiding_assistant_progress {state} |
    // guiding_assistant_done {state, error?} (#1364 item 4 — re-read GET /equipment/guider/live/assistant
    // for the figures). Detail fields are omitted when PHD2 sent none.
    public static readonly WsEvent GuiderEvent = new("guider.event");
    // #1311 — the polar alignment residual measured from the first minutes of each guided run:
    // payload = PaResidualDto (status measuring → done | unavailable; a dropped measurement
    // republishes the previous result, or { status: "idle" } when there is none).
    public static readonly WsEvent GuiderPaResidual = new("guider.pa_residual");
    // #1156 — the terminal status behind every guider 202 (start, stop, dither): payload
    // { operation_id, operation: "guider.start" | "guider.stop" | "guider.dither", ok, error? }.
    // `ok: false` with no exception is a refusal (not guiding, already stopped, daemon said
    // no); a settle failure or timeout carries the daemon's text in `error`.
    public static readonly WsEvent GuiderOperationComplete = new("guider.operation_complete");
    // #1156 — one per dither, REST or sequencer-driven: payload { ok, operation_id? (REST only),
    // error? }. Published after SettleDone or the settle failsafe, so `ok` is the settle verdict.
    public static readonly WsEvent GuiderDitherComplete = new("guider.dither_complete");
    // §42.2 — the mid-sequence guider fault flow reports the executed
    // on_guider_lost policy (pause_and_retry / skip_target / abort_sequence).
    public static readonly WsEvent GuiderFaultActionTaken = new("guider.fault_action_taken");
    // §63.17 — an on-demand POST /guider/profile/push completed; payload carries the RPC method names
    // attempted so the client can show what reached the daemon.
    public static readonly WsEvent GuiderProfilePushed = new("guider.profile_pushed");
    // §63.17 — a profile push changed the guide-camera selection while a dark library exists: the stored
    // darks belong to the previous camera and should be rebuilt. Payload: {reason: "guide_camera_changed"}.
    public static readonly WsEvent GuiderDarkLibraryInvalidated = new("guider.dark_library.invalidated");
    // §45 polar alignment — the routine lifecycle (started/stopped) plus the engine's live stream:
    // progress per solved iteration ({iteration, altitude/azimuth/total_error_arcmin, zone, solved}),
    // frame_started when a capture begins ({frame_id, exposure_seconds}), frame_complete per capture
    // ({frame_id, solved, consecutive_solve_failures, exposure_seconds, capture_ms, solve_ms?, ra_deg?,
    // dec_deg?}), paused when
    // 5 consecutive solves fail (§45.11 — the loop keeps retrying and resumes on the next good
    // solve), and error on a fatal routine failure ({reason, message}).
    public static readonly WsEvent PolarAlignStarted = new("polar_align.started");
    public static readonly WsEvent PolarAlignStopped = new("polar_align.stopped");
    public static readonly WsEvent PolarAlignProgress = new("polar_align.progress");
    public static readonly WsEvent PolarAlignFrameStarted = new("polar_align.frame_started");
    public static readonly WsEvent PolarAlignFrameComplete = new("polar_align.frame_complete");
    public static readonly WsEvent PolarAlignPaused = new("polar_align.paused");
    public static readonly WsEvent PolarAlignError = new("polar_align.error");
    // Stages: stage when the routine moves to a new stage ({stage, state}); needs_home when the
    // mount cannot find Home and the routine waits for the user's Continue ({stage, message});
    // checked when the three-point check has its result (the PolarAlignCheckResultDto fields).
    // started and progress also carry {stage}.
    public static readonly WsEvent PolarAlignStage = new("polar_align.stage");
    public static readonly WsEvent PolarAlignNeedsHome = new("polar_align.needs_home");
    public static readonly WsEvent PolarAlignChecked = new("polar_align.checked");

    // §59.10 — collimation health verdict, a free byproduct of a completed autofocus sweep on an obstructed
    // scope (SCT/Mak/RC/Newtonian). Emitted once per sweep when the donut stars yield a confident read;
    // refractors / in-focus fields produce no donut and are skipped. Payload:
    // { severity, offset_percent, direction_degrees, stars_used }.
    public static readonly WsEvent AutofocusCollimationVerdict = new("autofocus.collimation_verdict");

    // §59.15 — the Smart Focus run lifecycle (§59.2 one-frame runner). `started` fires ONCE per AF run
    // with the initially resolved mode ("smart" | "classic"); `shot_complete` per Smart shot with
    // { shot_index, position, hfr, stars }; `fallback_classic` when an in-run Smart failure (§59.11
    // ladder) hands the run to the full Classic sweep, with { reason } — that event IS the mode
    // transition (no second `started`), so a client keys its live panel off started + fallback_classic.
    public static readonly WsEvent AutofocusStarted = new("autofocus.started");
    public static readonly WsEvent AutofocusShotComplete = new("autofocus.shot_complete");
    public static readonly WsEvent AutofocusFallbackClassic = new("autofocus.fallback_classic");
    // §59.12/§59.15 — the Classic sweep's live stream for the Setup tab's Smart Focus pane. `step_complete`
    // per probe (coarse search and fine sweep alike): { step_index, phase: "coarse"|"fine", position, hfr,
    // stars, kept, total_steps } — `kept` false is a probe the sweep measured but dropped (too few stars).
    // `curve_fit` once per sweep attempt: { algorithm, r_squared, best_position, predicted_hfr, usable,
    // within_range }. `completed` closes a successful run of either mode: { mode, final_position, final_hfr
    // (measured at best focus when the confirmation frame read, else the fit's prediction), final_stars?,
    // duration_seconds, probes } — probes = Smart shots taken, or for Classic the probes on the run record
    // (coarse, dropped and the §59.7 backlash shots included; a re-centred sweep's earlier attempts are not), backlash_up_steps? +
    // backlash_verified? (§59.7, a Classic sweep whose backlash probe measured). `failed` closes an unsuccessful one: { reason, restored_position? } —
    // reason "cancelled" is the user's Cancel (POST /api/v1/autofocus/cancel). The full run (every probe,
    // the sampled fit curve, the rendered frame) rehydrates from GET /api/v1/autofocus/state.
    public static readonly WsEvent AutofocusStepComplete = new("autofocus.step_complete");
    public static readonly WsEvent AutofocusCurveFit = new("autofocus.curve_fit");
    public static readonly WsEvent AutofocusCompleted = new("autofocus.completed");
    public static readonly WsEvent AutofocusFailed = new("autofocus.failed");

    // #1264 — the unguided tracking probe's live stream (the Run tab shows it like an autofocus run).
    // `started` once per probe: { target, rungs: [s…], prior_seconds? }. `rung_complete` per rung:
    // { rung_index, exposure_seconds, eccentricity?, fwhm_px?, stars, usable, passed }. `completed`
    // once: { ceiling_seconds?, prior_seconds?, source: "measured"|"prior", usable_rungs, summary } —
    // source "prior" means fewer than two usable rungs, so the prior (possibly null) stands. The stored
    // record rehydrates from GET /api/v1/tracking/calibration.
    public static readonly WsEvent TrackingProbeStarted = new("tracking_probe.started");
    public static readonly WsEvent TrackingProbeRungComplete = new("tracking_probe.rung_complete");
    public static readonly WsEvent TrackingProbeCompleted = new("tracking_probe.completed");

    // Phase 7 — sequence
    public static readonly WsEvent SequenceStarted = new("sequence.started");
    public static readonly WsEvent SequencePaused = new("sequence.paused");
    public static readonly WsEvent SequenceResumed = new("sequence.resumed");
    // §38.10 — a same-target resume is running its pre-release refinement
    // (plate-solve re-center and/or autofocus) while the engine stays
    // suspended; payload { sequence_id, run_id, recenter, refocus }. Fires
    // after sequence.resumed; imaging continues when the refinement ends
    // (outcome rides a notification, not a WS event).
    public static readonly WsEvent SequenceResumeRecentering = new("sequence.resume_recentering");
    public static readonly WsEvent SequenceAborted = new("sequence.aborted");
    public static readonly WsEvent SequenceStopped = new("sequence.stopped");
    public static readonly WsEvent SequenceComplete = new("sequence.complete");
    public static readonly WsEvent SequenceInstructionStarted = new("sequence.instruction_started");
    public static readonly WsEvent SequenceInstructionComplete = new("sequence.instruction_complete");
    public static readonly WsEvent SequenceInstructionFailed = new("sequence.instruction_failed");
    public static readonly WsEvent SequenceProgress = new("sequence.progress");
    // §38.9 — a live mid-run edit (add/remove/move of a pending item) was applied
    // to the executing plan and persisted; payload { sequence_id, run_id, op,
    // instructions_completed, instructions_total, estimated_total_seconds,
    // estimated_remaining_seconds } (#1068). Clients re-fetch the sequence
    // detail (the body changed) and refresh run progress totals.
    public static readonly WsEvent SequenceRunItemsChanged = new("sequence.run_items_changed");
    public static readonly WsEvent SequenceAutoFlatsPrompt = new("sequence.auto_flats_prompt");
    public static readonly WsEvent SequenceAutoFlatsDecided = new("sequence.auto_flats_decided");
    // #1185 — the four `calibration.dark_library.*` build events that used to be declared here were never
    // published by anything (the §39 dark-library build runs as a sequence and reports through
    // GET /api/v1/calibration/dark-library/status + the sequence.* events). Declaring an event nobody sends is a
    // contract lie, so they are gone; the guider's own library events (guider.dark_library.*) are unrelated.

    // The sequencer run ended in an unhandled failure (same payload as the other sequence.* run events).
    public static readonly WsEvent SequenceFailed = new("sequence.failed");
    // §43-2 async backup create (BackupService): started when the worker claims the slot, complete/failed as the terminal.
    public static readonly WsEvent BackupCreateStarted = new("backup.create.started");
    public static readonly WsEvent BackupCreateComplete = new("backup.create.complete");
    public static readonly WsEvent BackupCreateFailed = new("backup.create.failed");
    // §63.8 / §63.6 guider calibration builds (GuiderService): dark library and defect map; progress is the granular poll.
    public static readonly WsEvent GuiderDarkLibraryStarted = new("guider.dark_library.started");
    public static readonly WsEvent GuiderDarkLibraryProgress = new("guider.dark_library.progress");
    public static readonly WsEvent GuiderDarkLibraryComplete = new("guider.dark_library.complete");
    public static readonly WsEvent GuiderDarkLibraryFailed = new("guider.dark_library.failed");
    public static readonly WsEvent GuiderDefectMapStarted = new("guider.defect_map.started");
    public static readonly WsEvent GuiderDefectMapProgress = new("guider.defect_map.progress");
    public static readonly WsEvent GuiderDefectMapComplete = new("guider.defect_map.complete");
    public static readonly WsEvent GuiderDefectMapFailed = new("guider.defect_map.failed");

    // Phase 8 — frames, sessions, backup, diagnostics
    public static readonly WsEvent FrameComplete = new("frame.complete");
    /// <summary>§59.5 — post-capture star analysis landed (HFR + star count written back).</summary>
    public static readonly WsEvent FrameAnalyzed = new("frame.analyzed");
    public static readonly WsEvent FramePreviewVariantEvicted = new("frame.preview.variant.evicted");

    public static readonly WsEvent SessionStarted = new("session.started");
    public static readonly WsEvent SessionEnded = new("session.ended");
    public static readonly WsEvent SessionRestretchProgress = new("session.restretch.progress");
    public static readonly WsEvent SessionRestretchComplete = new("session.restretch.complete");
    public static readonly WsEvent SessionRestretchFailed = new("session.restretch.failed");

    public static readonly WsEvent DiagnosticsIssueDetected = new("diagnostics.issue_detected");
    public static readonly WsEvent DiagnosticsAutoActionTaken = new("diagnostics.auto_action_taken");
    public static readonly WsEvent DiagnosticsCleared = new("diagnostics.cleared");
    // Per-connection resync frame sent once after every WS accept (§51
    // reconnect-replay gap): the full set of currently-open issues, so a client
    // whose socket was down when a diagnostics.cleared fired can drop the stale
    // issue instead of showing amber/red forever. Sent directly on the socket
    // (never broadcast), always with seq 0 — it is a statement of state, not a
    // position in the event stream.
    public static readonly WsEvent DiagnosticsSnapshot = new("diagnostics.snapshot");

    // Phase 9 — server lifecycle + notifications + storage
    public static readonly WsEvent ServerRestartImminent = new("server.restart_imminent");

    // §29 — a block device appeared or vanished, or the store mount came/went;
    // clients re-fetch device+space state (empty payload by design).
    public static readonly WsEvent StorageDevicesChanged = new("storage.devices_changed");


    public static readonly WsEvent DataManagerDownloadProgress = new("data_manager.download.progress");
    public static readonly WsEvent DataManagerDownloadComplete = new("data_manager.download.complete");
    public static readonly WsEvent DataManagerDownloadFailed = new("data_manager.download.failed");

    // §35.4 safety-reaction engine. safety.unsafe fires BEFORE the configured
    // action executes (so the client can raise its alarm modal while the daemon
    // reacts); safety.action_taken reports what the engine actually did (its
    // payload's `action` is the executed policy token, or "auto_resume" for the
    // §35 resume-when-safe path); safety.safe fires on the unsafe→safe transition.
    public static readonly WsEvent SafetyUnsafe = new("safety.unsafe");
    public static readonly WsEvent SafetySafe = new("safety.safe");
    public static readonly WsEvent SafetyActionTaken = new("safety.action_taken");

    // §35.3 emergency stop: fires BEFORE the stop rungs run (same
    // client-can-alarm-while-daemon-reacts contract as safety.unsafe); the
    // per-rung outcome rides the follow-up safety.action_taken event with
    // action == "emergency_stop".
    public static readonly WsEvent SafetyEmergencyStop = new("safety.emergency_stop");

    /// <summary>
    /// All registered event tokens. Iteration order is the order declared here.
    /// Used by the §17 contract-check to assert no runtime emit calls a token
    /// that isn't in this list. Exposed at runtime via <c>/api/v1/ws/catalog</c>
    /// so the WILMA client can validate as well (§60.9.4).
    /// </summary>
    public static readonly IReadOnlyList<WsEvent> All = new[] {
        EquipmentStateChanged,
        EquipmentConnected,
        EquipmentDisconnected,
        EquipmentConnectionFailed,
        EquipmentFault,
        EquipmentFaultActionTaken,
        FilterWheelManualSwap,
        SwitchValueMismatch,
        CameraExposureStarted,
        CameraExposureComplete,
        CameraExposureFailed,
        TelescopeSlewStarted,
        TelescopeSlewComplete,
        TelescopeSlewAborted,
        GuiderStep,
        GuiderEvent,
        GuiderPaResidual,
        GuiderOperationComplete,
        GuiderDitherComplete,
        GuiderFaultActionTaken,
        GuiderProfilePushed,
        GuiderDarkLibraryInvalidated,
        PolarAlignStarted,
        PolarAlignStopped,
        PolarAlignProgress,
        PolarAlignFrameStarted,
        PolarAlignFrameComplete,
        PolarAlignPaused,
        PolarAlignError,
        PolarAlignStage,
        PolarAlignNeedsHome,
        PolarAlignChecked,
        AutofocusCollimationVerdict,
        AutofocusStarted,
        AutofocusShotComplete,
        AutofocusFallbackClassic,
        AutofocusStepComplete,
        AutofocusCurveFit,
        AutofocusCompleted,
        AutofocusFailed,
        TrackingProbeStarted,
        TrackingProbeRungComplete,
        TrackingProbeCompleted,
        SequenceStarted,
        SequencePaused,
        SequenceResumed,
        SequenceResumeRecentering,
        SequenceAborted,
        SequenceStopped,
        SequenceComplete,
        SequenceInstructionStarted,
        SequenceInstructionComplete,
        SequenceInstructionFailed,
        SequenceProgress,
        SequenceRunItemsChanged,
        SequenceAutoFlatsPrompt,
        SequenceAutoFlatsDecided,
        SequenceFailed,
        BackupCreateStarted,
        BackupCreateComplete,
        BackupCreateFailed,
        GuiderDarkLibraryStarted,
        GuiderDarkLibraryProgress,
        GuiderDarkLibraryComplete,
        GuiderDarkLibraryFailed,
        GuiderDefectMapStarted,
        GuiderDefectMapProgress,
        GuiderDefectMapComplete,
        GuiderDefectMapFailed,
        FrameComplete,
        FrameAnalyzed,
        FramePreviewVariantEvicted,
        SessionStarted,
        SessionEnded,
        SessionRestretchProgress,
        SessionRestretchComplete,
        SessionRestretchFailed,
        DiagnosticsIssueDetected,
        DiagnosticsAutoActionTaken,
        DiagnosticsCleared,
        DiagnosticsSnapshot,
        ServerRestartImminent,
        StorageDevicesChanged,
        DataManagerDownloadProgress,
        DataManagerDownloadComplete,
        DataManagerDownloadFailed,
        SafetyUnsafe,
        SafetySafe,
        SafetyActionTaken,
        SafetyEmergencyStop,
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
/// completes. <c>ResumeToken</c> is the base-10 stringified last-seen
/// sequence number (an opaque token issued by REST
/// <c>GET /api/v1/server/state.ws_resume_token</c> is design intent, #1131).
/// </summary>
public sealed record WsResumeRequestDto(string? ResumeToken);

/// <summary>
/// Server response to a <see cref="WsResumeRequestDto"/>. Four shapes, per
/// design/API_CONTRACT.md (WebSocket section):
/// <list type="bullet">
///   <item><c>{ resumed: false }</c> (no code) → the token was absent or empty; fresh subscription</item>
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