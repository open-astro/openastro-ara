#region "copyright"

/*
    Copyright (c) 2026 - present Open Astro contributors

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
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
/// ARA-client auto-reconnect exists (tracked in PORT_TODO); today the client never
/// reconnects on its own, so waiting would only mean more unguided frames.
/// </summary>
public sealed partial class GuiderService {

    private readonly IProfileStore? _profileStore;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly INotificationService? _notifications;
    // Guarded by _gate. The kind of the fault that most recently latched a §42.2 reaction this episode
    // (null = not latched); cleared by SetStateLocked on the next successful connect. Kind-aware because
    // an EquipmentDisconnected fault stays Connected and so never clears the latch — a later, strictly
    // more severe LinkDown must still be able to react (see BeginFaultReactionLocked).
    private GuiderFaultKind? _latchedFaultKind;

    internal enum GuiderLostAction { PauseAndRetry, SkipTarget, AbortSequence }

    // Which fault triggered the reaction — shapes the user-facing copy and the idle (no-sequence)
    // behaviour. A LinkDown is covered by the §63.3 process recovery's own notification; an
    // EquipmentDisconnected (guide camera dropped, link still up) starts no recovery, so it must alert
    // the user itself even when no sequence is running.
    internal enum GuiderFaultKind { LinkDown, EquipmentDisconnected }

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
            return;
        }
        _latchedFaultKind = kind;
        // Fire-and-forget one-shot: ReactToGuidingLossAsync owns no disposables,
        // catches everything itself, and each rung uses CancellationToken.None
        // deliberately (a daemon shutdown mid-reaction should still finish
        // pausing the run — the sequencer's own shutdown path wins regardless).
        _ = Task.Run(() => ReactToGuidingLossAsync(kind), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background reaction boundary: a fault is logged and the sequence simply keeps its prior state — never an unobserved task exception, and never a blocked recovery (which runs on its own task).")]
    internal async Task ReactToGuidingLossAsync(GuiderFaultKind kind) {
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
                        affected = (await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false)).Count;
                        break;
                }
                LogFaultReacted(GuiderLostActionToken(action), affected);
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
                ["kind"] = kind == GuiderFaultKind.EquipmentDisconnected ? "equipment_disconnected" : "link_down",
            }).ConfigureAwait(false);

            var (title, message) = BuildFaultCopy(kind, action);
            await NotifyFaultQuietlyAsync(title, message).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultReactionFailed(ex);
        }
    }

    // Source-appropriate copy. A LinkDown genuinely dropped the connection and the §63.3 recovery is
    // restarting the guider (so "reconnect it" is correct). An EquipmentDisconnected left the guider
    // connected — only the guide camera dropped — so the copy must NOT tell the user to reconnect the
    // guider or claim process recovery is running (neither is true).
    private static (string title, string message) BuildFaultCopy(GuiderFaultKind kind, GuiderLostAction action) {
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
                + "Automatic process recovery is attempting to bring the guider back (see its own notification for the outcome)."),
            GuiderLostAction.SkipTarget => ("Guiding lost — current target skipped",
                "The guider connection dropped mid-sequence and your safety policy is set to skip: the current instructions were skipped and the sequence advances. "
                + "Instructions that need the guider will fail until it is reconnected."),
            _ => ("Guiding lost — sequence paused",
                "The guider connection dropped mid-sequence: the running sequence pauses at the current instruction so no more unguided frames burn sky time. "
                + "Automatic process recovery is attempting to restart the guider; reconnect it (Equipment → Guider), then Resume the run."),
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the fault reaction itself (the §58.7 precedent). Log-and-recover boundary.")]
    private async Task NotifyFaultQuietlyAsync(string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Critical,
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
}
