#region "copyright"

/*
    Copyright (c) 2026 - present Open Astro contributors

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 mid-sequence guider fault flow. When the guider connection drops while a
/// sequence is running, the run is shooting unguided from that instant — so the
/// profile's <c>on_guider_lost</c> policy executes immediately, alongside (not
/// instead of) the §63.3 process recovery: <c>pause_and_retry</c> (default —
/// pause active runs at the instruction boundary; the user resumes after
/// reconnecting), <c>skip_target</c> (skip the current instructions and let the
/// sequence advance), or <c>abort_sequence</c>. All actions are daemon-automated
/// (never §58.12 user activity). One reaction per disconnect episode — the latch
/// clears on the next successful connect. <c>GuiderRetryTimeoutSec</c> is
/// deliberately not consumed here: a grace window only makes sense once the §63.3
/// ARA-client auto-reconnect exists (tracked in the P1–P5 issue backlog); today the client never
/// reconnects on its own, so waiting would only mean more unguided frames.
/// </summary>
public sealed partial class GuiderService {

    private readonly IProfileStore? _profileStore;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly INotificationService? _notifications;
    // §42.5 — the guider is deliberately OFF the EquipmentFaultHub channel (this partial owns its
    // §42.2 reaction), so it writes its faults to the persistent log directly.
    private readonly IFaultLogService? _faultLog;
    // Guarded by _gate. The kind of the fault that most recently latched a §42.2 reaction this episode
    // (null = not latched); cleared by SetStateLocked on the next successful connect. Kind-aware because
    // an EquipmentDisconnected fault stays Connected and so never clears the latch — a later, strictly
    // more severe LinkDown must still be able to react (see BeginFaultReactionLocked).
    private GuiderFaultKind? _latchedFaultKind;
    // Guarded by _gate. The §42.5 row of the camera-drop fault that latched this episode, held until
    // the guider reports the camera back (EquipmentReconnected, #1191) so that row can be resolved
    // the moment the device recovers instead of on the next guider connect. Null when no camera-drop
    // row is open; cleared on the next connect (SetStateLocked) and superseded by a LinkDown (a dead
    // link brings no reconnect event — the next connect's ResolveOnReconnectAsync closes the row).
    private EquipmentFaultEvent? _openCameraDropFault;

    internal enum GuiderLostAction { PauseAndRetry, SkipTarget, AbortSequence }

    // Which fault triggered the reaction — shapes the user-facing copy and the idle (no-sequence)
    // behaviour. A LinkDown is covered by the §63.3 process recovery's own notification; an
    // EquipmentDisconnected (guide camera dropped, link still up) starts no recovery, so it must alert
    // the user itself even when no sequence is running.
    internal enum GuiderFaultKind { LinkDown, EquipmentDisconnected, StarLost }

    /// <summary>
    /// Maps the profile's snake_case on_guider_lost token. Unknown tokens fall
    /// back to the default <c>pause_and_retry</c> (fail toward not wasting sky
    /// time on unguided frames; logged so a typo'd profile is visible).
    /// </summary>
    internal static GuiderLostAction ParseGuiderLostAction(string? token) => token switch {
        "skip_target" => GuiderLostAction.SkipTarget,
        "abort_sequence" => GuiderLostAction.AbortSequence,
        "pause_and_retry" => GuiderLostAction.PauseAndRetry,
        _ => GuiderLostAction.PauseAndRetry,
    };

    internal static string GuiderLostActionToken(GuiderLostAction action) => action switch {
        GuiderLostAction.SkipTarget => "skip_target",
        GuiderLostAction.AbortSequence => "abort_sequence",
        _ => "pause_and_retry",
    };

    // Caller holds _gate. One reaction per disconnect episode; the latch clears on the next successful
    // connect (SetStateLocked). Deliberately NOT re-armed on a device reconnect — a flapping guide camera
    // must not re-trigger skip/abort per cycle. (Debounced re-arm for repeated genuine incidents in one
    // session is a tracked follow-up, alongside the guider#66 reconnect-abandonment watchdog.)
    //
    // Exception, safety-critical: a LinkDown always reacts even when an EquipmentDisconnected already
    // latched. An equipment fault stays Connected, so its latch never clears on its own — without this a
    // genuine link death following an earlier camera glitch in the same episode would be swallowed and the
    // run would keep shooting unguided through a real disconnect. LinkDown is strictly more severe.
    private void BeginFaultReactionLocked(GuiderFaultKind kind) {
        if (_disposed) {
            return;
        }
        var alreadyReacted = _latchedFaultKind is not null
            && !(kind == GuiderFaultKind.LinkDown && _latchedFaultKind == GuiderFaultKind.EquipmentDisconnected);
        if (alreadyReacted) {
            // #1241 — the reaction stays one-shot, but the history must show every drop: a repeated
            // camera drop in a latched episode gets its own §42.5 row (no action stamp — nothing was
            // done) and becomes the open row the next EquipmentReconnected resolves.
            if (kind == GuiderFaultKind.EquipmentDisconnected && _openCameraDropFault is null) {
                var repeat = new EquipmentFaultEvent(DeviceType.Guider, DeviceId: null, DeviceName: "PHD2",
                    EquipmentFaultKind.Disconnected,
                    "guide camera disconnected again — guider link still up; the on_guider_lost reaction already ran this episode",
                    DateTimeOffset.UtcNow);
                RecordFaultQuietly(repeat);
                _openCameraDropFault = repeat;
                LogRepeatedCameraDrop();
            }
            return;
        }
        _latchedFaultKind = kind;
        // §42.5 — the fault goes on the record the moment it latches, natural-keyed by this
        // event instance so the reaction's later action stamp lands on the same row. Kind is
        // Disconnected for both (the log's wire vocabulary; a link-down IS a disconnect, and
        // the camera drop is a guiding disconnect in effect) — the details distinguish them.
        var fault = new EquipmentFaultEvent(DeviceType.Guider, DeviceId: null, DeviceName: "PHD2",
            EquipmentFaultKind.Disconnected,
            kind == GuiderFaultKind.LinkDown
                ? "guider link down (socket dropped or daemon unresponsive)"
                : "guide camera disconnected — guider link still up",
            DateTimeOffset.UtcNow);
        RecordFaultQuietly(fault);
        _openCameraDropFault = kind == GuiderFaultKind.EquipmentDisconnected ? fault : null;
        // Fire-and-forget one-shot: ReactToGuidingLossAsync owns no disposables,
        // catches everything itself, and each rung uses CancellationToken.None
        // deliberately (a daemon shutdown mid-reaction should still finish
        // pausing the run — the sequencer's own shutdown path wins regardless).
        _ = Task.Run(() => ReactToGuidingLossAsync(kind, fault), CancellationToken.None);
    }

    // #1191 — the guider reports the lost device back. Resolves the open camera-drop row (the device
    // behind it recovered; the link never went down, so nothing else would close it until the next
    // guider connect) and tells the user guiding can resume. Deliberately does NOT re-arm the one-shot
    // reaction (_latchedFaultKind stays set): a flapping guide camera must not skip/abort per cycle.
    private void OnEquipmentReconnected(object? sender, EquipmentReconnectedEventArgs e) {
        EquipmentFaultEvent? fault;
        lock (_gate) {
            if (_disposed || !ReferenceEquals(sender, _guider) || _state != EquipmentConnectionState.Connected
                    || !AffectsGuiding(e.DeviceType)) {
                return;
            }
            fault = _openCameraDropFault;
            _openCameraDropFault = null;
        }
        if (fault is null) {
            return; // no open camera-drop row this episode (an unsolicited or repeated reconnect)
        }
        LogCameraReconnected(e.DeviceType);
        _ = Task.Run(async () => {
            await ResolveFaultQuietlyAsync(fault, DateTimeOffset.UtcNow).ConfigureAwait(false);
            await NotifyQuietlyAsync(NotificationSeverity.Info, "Guide camera reconnected",
                "The guider reported the guide camera is back. Guiding can resume; if a sequence was paused "
                + "by the drop, Resume the run.").ConfigureAwait(false);
        }, CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background reaction boundary: a fault is logged and the sequence simply keeps its prior state — never an unobserved task exception, and never a blocked recovery (which runs on its own task).")]
    internal async Task ReactToGuidingLossAsync(GuiderFaultKind kind, EquipmentFaultEvent? fault = null) {
        try {
            SafetyPoliciesDto? policy = null;
            try {
                policy = _profileStore?.GetSafetyPolicies();
            } catch (Exception ex) {
                LogFaultPolicyReadFailed(ex);
            }
            var action = ParseGuiderLostAction(policy?.OnGuiderLost);
            if (policy is not null && policy.OnGuiderLost is not ("pause_and_retry" or "skip_target" or "abort_sequence")) {
                LogFaultUnknownToken(policy.OnGuiderLost);
            }

            var sequencer = _sequencerResolver?.Invoke();
            var affected = 0;
            if (sequencer is not null) {
                switch (action) {
                    case GuiderLostAction.AbortSequence:
                        affected = await sequencer.AbortActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
                        break;
                    case GuiderLostAction.SkipTarget:
                        affected = await sequencer.SkipActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
                        break;
                    default:
                        // A star-lost pause is adoptable: should the safety engine pause and park before the
                        // star is back, its auto-resume takes these runs over (a parked, stopped guider sends
                        // no guide step for this watch to act on).
                        var paused = kind == GuiderFaultKind.StarLost
                            ? await sequencer.PauseActiveRunsAsync(adoptable: true, CancellationToken.None).ConfigureAwait(false)
                            : await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
                        affected = paused.Count;
                        if (kind == GuiderFaultKind.StarLost) {
                            // The star-lost watch resumes exactly these runs when guide steps return. If a step
                            // already landed while the pause was being placed, release them right away.
                            var starBackAlready = false;
                            lock (_gate) {
                                if (_starLostReacted) {
                                    _starLostPausedRuns = paused;
                                    // The long-outage half: after StarLostRecoverAfter the guider's own
                                    // frames are asked whether the clouds have gone.
                                    ArmStarLostRecoveryLocked(StarLostRecoverAfter);
                                } else {
                                    starBackAlready = true;
                                }
                            }
                            if (starBackAlready) {
                                _ = Task.Run(() => ResumeAfterStarRecoveredAsync(null, paused), CancellationToken.None);
                            }
                        }
                        break;
                }
                LogFaultReacted(GuiderLostActionToken(action), affected);
            }

            // §42.5 — stamp what the reaction DID onto the fault's history row: the policy
            // token when it acted on running sequences, notify_only when there was nothing
            // to act on (the FaultReactionService's idle vocabulary).
            if (fault is not null) {
                await RecordActionQuietlyAsync(fault, affected > 0 ? GuiderLostActionToken(action) : "notify_only")
                    .ConfigureAwait(false);
            }

            if (affected == 0) {
                // No sequence was running, so there's no sequence-action to report. A LinkDown is still
                // covered by the §63.3 recovery's own notification, so it stays quiet here. An equipment
                // fault starts NO recovery, so a silently-dropped guide camera between targets would go
                // completely unnoticed — alert the user directly instead.
                if (kind == GuiderFaultKind.EquipmentDisconnected) {
                    await NotifyFaultQuietlyAsync(
                        "Guide camera disconnected",
                        "The guider reported that the guide camera disconnected. Guiding is offline until it comes back; "
                        + "the guider stays connected and is attempting to recover the camera on its own.").ConfigureAwait(false);
                }
                return;
            }

            await PublishFaultEventAsync(new JsonObject {
                ["action"] = GuiderLostActionToken(action),
                ["runs_affected"] = affected,
                ["kind"] = kind switch {
                    GuiderFaultKind.EquipmentDisconnected => "equipment_disconnected",
                    GuiderFaultKind.StarLost => "star_lost",
                    _ => "link_down",
                },
            }).ConfigureAwait(false);

            var (title, message) = BuildFaultCopy(kind, action, (int) StarLostGrace.TotalSeconds);
            await NotifyFaultQuietlyAsync(title, message).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultReactionFailed(ex);
        }
    }

    // Source-appropriate copy. A LinkDown genuinely dropped the connection and the §63.3 recovery is
    // bringing the guider back (a local unit restart, or just a reconnect loop for a guider on another
    // machine — #1192 — so the copy names neither). An EquipmentDisconnected left the guider
    // connected — only the guide camera dropped — so the copy must NOT tell the user to reconnect the
    // guider or claim process recovery is running (neither is true).
    internal static (string title, string message) BuildFaultCopy(GuiderFaultKind kind, GuiderLostAction action, int starLostGraceSec = 30) {
        if (kind == GuiderFaultKind.StarLost) {
            // The link and the camera are fine; the guider cannot see its star (cloud, a bump, dew). PHD2
            // keeps looking on its own, so a pause resumes by itself when guide steps return.
            var lost = $"The guider lost its guide star for more than {starLostGraceSec} s (cloud, dew, a knock)";
            return action switch {
                GuiderLostAction.AbortSequence => ("Guide star lost — sequence aborted",
                    $"{lost} and your safety policy is set to abort: the running sequence was aborted. The guider keeps looking for the star."),
                GuiderLostAction.SkipTarget => ("Guide star lost — current target skipped",
                    $"{lost} and your safety policy is set to skip: the current instructions were skipped and the sequence advances. The guider keeps looking for the star."),
                _ => ("Guide star lost — sequence paused",
                    $"{lost}: the running sequence pauses so no unguided frame burns sky time. It resumes on its own as soon as the guider is guiding again; "
                    + "if the star does not come back, check the guider and press Resume."),
            };
        }
        if (kind == GuiderFaultKind.EquipmentDisconnected) {
            return action switch {
                GuiderLostAction.AbortSequence => ("Guide camera dropped — sequence aborted",
                    "The guide camera disconnected mid-sequence and your safety policy is set to abort: the running sequence was aborted. "
                    + "The guider stays connected and is trying to recover the camera on its own."),
                GuiderLostAction.SkipTarget => ("Guide camera dropped — current target skipped",
                    "The guide camera disconnected mid-sequence and your safety policy is set to skip: the current instructions were skipped and the sequence advances. "
                    + "Instructions that need guiding will fail until the camera is back."),
                _ => ("Guide camera dropped — sequence paused",
                    "The guide camera disconnected mid-sequence: the running sequence pauses so no more unguided frames burn sky time. "
                    + "The guider stays connected and is trying to recover the camera on its own; Resume the run once guiding is back."),
            };
        }
        return action switch {
            GuiderLostAction.AbortSequence => ("Guiding lost — sequence aborted",
                "The guider connection dropped mid-sequence and your safety policy is set to abort: the running sequence was aborted. "
                + "Automatic recovery is attempting to bring the guider back (see its own notification for the outcome)."),
            GuiderLostAction.SkipTarget => ("Guiding lost — current target skipped",
                "The guider connection dropped mid-sequence and your safety policy is set to skip: the current instructions were skipped and the sequence advances. "
                + "Instructions that need the guider will fail until it is reconnected."),
            _ => ("Guiding lost — sequence paused",
                "The guider connection dropped mid-sequence: the running sequence pauses at the current instruction so no more unguided frames burn sky time. "
                + "Automatic recovery is attempting to bring the guider back (see its own notification for the outcome); once it is reconnected, Resume the run."),
        };
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must not abort the fault reaction. Log-and-recover boundary.")]
    private async Task PublishFaultEventAsync(JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.GuiderFaultActionTaken, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultWsPublishFailed(ex);
        }
    }

    private Task NotifyFaultQuietlyAsync(string title, string message) =>
        NotifyQuietlyAsync(NotificationSeverity.Critical, title, message);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the fault reaction itself (the §58.7 precedent). Log-and-recover boundary.")]
    private async Task NotifyQuietlyAsync(NotificationSeverity severity, string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: severity,
                Category: NotificationCategory.Equipment,
                Title: title,
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultNotifyFailed(ex);
        }
    }

    // §42.5 — best-effort persistence: a fault-log store hiccup must never touch the reaction.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort fault-log write on a fire-and-forget task; any store fault is logged and the reaction proceeds. Log-and-recover boundary.")]
    private void RecordFaultQuietly(EquipmentFaultEvent fault) {
        if (_faultLog is null) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await _faultLog.RecordFaultAsync(fault, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogFaultLogWriteFailed(ex);
            }
        }, CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort fault-log write; any store fault is logged and the reaction proceeds. Log-and-recover boundary.")]
    private async Task RecordActionQuietlyAsync(EquipmentFaultEvent fault, string action) {
        if (_faultLog is null) {
            return;
        }
        try {
            await _faultLog.RecordActionAsync(fault, action, resolvedUtc: null, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultLogWriteFailed(ex);
        }
    }

    // #1191 — stamp resolved_at on the one row the recovered device belongs to.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort fault-log resolve on a fire-and-forget task; any store fault is logged. Log-and-recover boundary.")]
    private async Task ResolveFaultQuietlyAsync(EquipmentFaultEvent fault, DateTimeOffset resolvedUtc) {
        if (_faultLog is null) {
            return;
        }
        try {
            await _faultLog.ResolveAsync(fault, resolvedUtc, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultLogWriteFailed(ex);
        }
    }

    // §42.5 — an observed reconnect resolves this guider's open disconnect rows (the same
    // semantics EquipmentEventPublisher gives Alpaca devices). A camera-drop row (link still
    // up) normally closes earlier, on the guider's EquipmentReconnected event (#1191); this
    // is the backstop for one whose reconnect never arrived (the link died first, or the
    // daemon abandoned the device — guider#66).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort fault-log resolve on a fire-and-forget task; any store fault is logged. Log-and-recover boundary.")]
    private void ResolveGuiderFaultsQuietly() {
        if (_faultLog is null) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await _faultLog.ResolveOnReconnectAsync(DeviceType.Guider, DateTimeOffset.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (Exception ex) {
                LogFaultLogWriteFailed(ex);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(EventId = 4227, Level = LogLevel.Warning, Message = "§42.5 guider fault-log write failed (best-effort; the reaction is unaffected)")]
    private partial void LogFaultLogWriteFailed(Exception exception);

    [LoggerMessage(EventId = 4221, Level = LogLevel.Warning, Message = "§42.2 guider-lost reaction executed: action={Action} runsAffected={RunsAffected}")]
    private partial void LogFaultReacted(string action, int runsAffected);

    [LoggerMessage(EventId = 4222, Level = LogLevel.Warning, Message = "§42.2 profile safety-policy read failed; using the default pause_and_retry")]
    private partial void LogFaultPolicyReadFailed(Exception exception);

    [LoggerMessage(EventId = 4223, Level = LogLevel.Warning, Message = "§42.2 unrecognized on_guider_lost token '{Token}' — falling back to pause_and_retry")]
    private partial void LogFaultUnknownToken(string? token);

    [LoggerMessage(EventId = 4224, Level = LogLevel.Error, Message = "§42.2 guider-lost reaction failed; the sequence keeps its prior state")]
    private partial void LogFaultReactionFailed(Exception exception);

    [LoggerMessage(EventId = 4225, Level = LogLevel.Warning, Message = "§42.2 WS publish of the guider fault action failed (best-effort)")]
    private partial void LogFaultWsPublishFailed(Exception exception);

    [LoggerMessage(EventId = 4226, Level = LogLevel.Warning, Message = "§42.2 fault notification post failed (best-effort)")]
    private partial void LogFaultNotifyFailed(Exception exception);

    [LoggerMessage(EventId = 4229, Level = LogLevel.Information, Message = "§42.2 guider reports its {DeviceType} back — resolving the open camera-drop fault row (#1191); the one-shot reaction stays latched")]
    private partial void LogCameraReconnected(string deviceType);

    [LoggerMessage(EventId = 4230, Level = LogLevel.Warning, Message = "Guide camera dropped again in the same episode — recorded (§42.5, #1241); the on_guider_lost reaction stays one-shot")]
    private partial void LogRepeatedCameraDrop();
}
