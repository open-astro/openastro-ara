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
    // Guarded by _gate. One §42.2 reaction per disconnect episode; cleared by
    // SetStateLocked on the next successful connect.
    private bool _faultReactionLatched;

    internal enum GuiderLostAction { PauseAndRetry, SkipTarget, AbortSequence }

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

    // Caller holds _gate.
    private void BeginFaultReactionLocked() {
        if (_disposed || _faultReactionLatched) {
            return;
        }
        _faultReactionLatched = true;
        // Fire-and-forget one-shot: ReactToGuidingLossAsync owns no disposables,
        // catches everything itself, and each rung uses CancellationToken.None
        // deliberately (a daemon shutdown mid-reaction should still finish
        // pausing the run — the sequencer's own shutdown path wins regardless).
        _ = Task.Run(() => ReactToGuidingLossAsync(), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background reaction boundary: a fault is logged and the sequence simply keeps its prior state — never an unobserved task exception, and never a blocked recovery (which runs on its own task).")]
    internal async Task ReactToGuidingLossAsync() {
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
            if (sequencer is null) {
                return;
            }
            var affected = 0;
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
            if (affected == 0) {
                // No sequence was running — the §63.3 recovery's own notifications
                // cover the crash itself; a sequence-action notification would be noise.
                return;
            }

            await PublishFaultEventAsync(new JsonObject {
                ["action"] = GuiderLostActionToken(action),
                ["runs_affected"] = affected,
            }).ConfigureAwait(false);

            var (title, message) = action switch {
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
            await NotifyFaultQuietlyAsync(title, message).ConfigureAwait(false);
        } catch (Exception ex) {
            LogFaultReactionFailed(ex);
        }
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
