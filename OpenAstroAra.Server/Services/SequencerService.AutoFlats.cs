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
/// §48.1 auto-flats prompt flow. The flat automation is the §39.5 matching-flats
/// machinery, which regenerates tonight's exact per-filter geometry (focus, gain,
/// offset) from the run's own catalog session — since §48.3 each per-filter block is a
/// native <c>FlatPanelFlats</c> auto-exposure set (panel flavor) or, since §48.4, a
/// <c>SkyFlats</c> twilight set (sky flavor) driven by the §48.7 flat_panel / sky_flat policy.
/// So: on run start the
/// profile's <c>calibration_capture_default</c> decides — "ask" emits the
/// <c>sequence.auto_flats_prompt</c> WS event for WILMA's dialog, "panel_at_end"/
/// "sky_at_twilight" auto-decide, "never" stays silent. The decision (from the profile
/// or the §48.1 decide endpoint) is remembered on the run; when the run COMPLETES,
/// "panel_at_end" generates the §39.5 panel-flats sequence and starts it immediately, and
/// (since §48.4) "sky_at_twilight" generates the sky-flats sequence and ALSO starts it
/// immediately — the sky sequence carries its own <c>WaitForSunAltitude</c> gate, so starting
/// it just parks the run on the wait until twilight arrives. One singleton serves both
/// <see cref="ISequencerService"/> and <see cref="IAutoFlatsService"/> (§8.1 pattern).
/// </summary>
public sealed partial class SequencerService : IAutoFlatsService {

    private readonly IProfileStore? _profileStore;
    private readonly Func<ICalibrationService?>? _calibrationResolver;
    private readonly INotificationService? _notifications;

    internal const string ChoicePanelAtEnd = "panel_at_end";
    internal const string ChoiceSkyAtTwilight = "sky_at_twilight";
    internal const string ChoiceLater = "later";

    public async Task<OperationAcceptedDto> ProvideDecisionAsync(Guid sequenceId, AutoFlatsDecisionRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var choice = request.Choice?.Trim().ToLowerInvariant() switch {
            ChoicePanelAtEnd => ChoicePanelAtEnd,
            ChoiceSkyAtTwilight => ChoiceSkyAtTwilight,
            ChoiceLater or "never" => ChoiceLater,
            _ => throw new ArgumentException($"unknown auto-flats choice '{request.Choice}'", nameof(request)),
        };

        if (_runs.TryGetValue(sequenceId, out var run) && !IsTerminal(run.State)) {
            run.AutoFlatsChoice = choice == ChoiceLater ? null : choice;
        }
        // A decision for an unknown/finished run still records the preference
        // below when asked — the user answered the dialog; losing the "remember"
        // because the run raced to completion would be surprising.

        if (request.Remember) {
            PersistCaptureDefaultQuietly(choice == ChoiceLater ? "never" : choice);
        }

        await EmitAutoFlatsEventAsync(WsEventCatalog.SequenceAutoFlatsDecided, new JsonObject {
            ["sequence_id"] = sequenceId.ToString(),
            ["choice"] = choice,
            ["remembered"] = request.Remember,
            ["source"] = "user",
        }).ConfigureAwait(false);
        LogAutoFlatsDecided(sequenceId, choice, request.Remember);
        return PlaceholderEquipmentHelpers.Accepted("sequence.auto-flats-decision", idempotencyKey);
    }

    // Sync profile read on the start path — the choice must land on the run
    // BEFORE the worker launches (#735 race). Fail toward asking, never toward
    // silently skipping calibration.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort profile read on the start path: a store fault degrades to the ask default; logged, never thrown into StartAsync.")]
    private string ResolveAutoFlatsToken() {
        try {
            return _profileStore?.GetSafetyPolicies().CalibrationCaptureDefault ?? "ask";
        } catch (Exception ex) {
            LogAutoFlatsPolicyReadFailed(ex);
            return "ask";
        }
    }

    // Background WS announcement only — the run's choice was already set
    // synchronously by StartCoreAsync.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background announce boundary: a WS fault must never affect the run that just started; logged, never rethrown.")]
    private async Task EmitAutoFlatsAnnouncementAsync(Guid sequenceId, Guid runId, string token) {
        try {
            switch (token) {
                case ChoicePanelAtEnd:
                case ChoiceSkyAtTwilight:
                    await EmitAutoFlatsEventAsync(WsEventCatalog.SequenceAutoFlatsDecided, new JsonObject {
                        ["sequence_id"] = sequenceId.ToString(),
                        ["choice"] = token,
                        ["remembered"] = true,
                        ["source"] = "profile",
                    }).ConfigureAwait(false);
                    break;
                case "never":
                    break;
                default:
                    await EmitAutoFlatsEventAsync(WsEventCatalog.SequenceAutoFlatsPrompt, new JsonObject {
                        ["sequence_id"] = sequenceId.ToString(),
                        ["run_id"] = runId.ToString(),
                    }).ConfigureAwait(false);
                    break;
            }
        } catch (Exception ex) {
            LogAutoFlatsAnnounceFailed(ex, sequenceId);
        }
    }

    // Fired from the worker's finally when a COMPLETED run carries a choice.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "End-of-night automation boundary: a generation/start fault posts a notification and never throws into the worker teardown.")]
    internal async Task ExecuteAutoFlatsAsync(Guid sequenceId, Guid sessionId, string choice) {
        try {
            var calibration = _calibrationResolver?.Invoke();
            if (calibration is null) {
                return;
            }
            var skyFlavor = choice != ChoicePanelAtEnd;
            var generated = await calibration.GenerateMatchingFlatsAsync(
                sessionId,
                new MatchingFlatsRequestDto(OverrideFrameCount: null, OverrideTargetAdu: null, GenerateOnly: false,
                    Flavor: skyFlavor ? "sky" : "panel"),
                idempotencyKey: null,
                CancellationToken.None).ConfigureAwait(false);
            if (generated.GeneratedSequenceId is not Guid flatsId || generated.TotalFlatFrames <= 0) {
                LogAutoFlatsNothingToCapture(sequenceId, sessionId);
                return;
            }

            // Both flavors auto-start now: the §48.4 sky sequence carries its own twilight wait
            // (WaitForSunAltitude) + slew, so starting it immediately just parks the run on the
            // wait until dawn brightens. announce:false — the flats run must not prompt about
            // calibrating the calibration, nor re-tag itself for a second execute pass.
            await StartCoreAsync(flatsId,
                new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false),
                idempotencyKey: null, announceAutoFlats: false).ConfigureAwait(false);
            LogAutoFlatsStarted(sequenceId, flatsId, generated.TotalFlatFrames);
            if (skyFlavor) {
                await NotifyAutoFlatsQuietlyAsync("Twilight sky flats armed",
                    $"Your sequence completed and \"{generated.GeneratedSequenceName}\" is running — it waits for morning twilight, "
                    + $"slews to the sky-flat position, then captures {generated.TotalFlatFrames} matching flats, adapting exposure as the sky brightens.").ConfigureAwait(false);
            } else {
                await NotifyAutoFlatsQuietlyAsync("End-of-session flats started",
                    $"Your sequence completed and \"{generated.GeneratedSequenceName}\" is now capturing {generated.TotalFlatFrames} matching flats — "
                    + "each filter replays tonight's focus position, gain, and offset. Light your flat panel if it isn't already on.").ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogAutoFlatsExecuteFailed(ex, sequenceId, sessionId);
            await NotifyAutoFlatsQuietlyAsync("End-of-session flats could not be prepared",
                "Your sequence completed, but generating the matching-flats sequence failed. "
                + "You can still capture them anytime: Image Library → tonight's session → \"Capture Matching Flats\".").ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Preference persistence is best-effort; a store fault must not fail the decision. Logged.")]
    private void PersistCaptureDefaultQuietly(string token) {
        try {
            if (_profileStore is null) {
                return;
            }
            var policy = _profileStore.GetSafetyPolicies();
            _profileStore.PutSafetyPolicies(policy with { CalibrationCaptureDefault = token });
        } catch (Exception ex) {
            LogAutoFlatsPersistFailed(ex, token);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must not fail the prompt/decision flow. Logged.")]
    private async Task EmitAutoFlatsEventAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogAutoFlatsWsFailed(ex, eventType);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the flats automation outcome itself (the §58.7 precedent). Logged.")]
    private async Task NotifyAutoFlatsQuietlyAsync(string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Info,
                Category: NotificationCategory.Sequence,
                Title: title,
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogAutoFlatsNotifyFailed(ex);
        }
    }

    [LoggerMessage(EventId = 4801, Level = LogLevel.Information, Message = "§48 auto-flats decision for sequence {SequenceId}: {Choice} (remember={Remember})")]
    private partial void LogAutoFlatsDecided(Guid sequenceId, string choice, bool remember);

    [LoggerMessage(EventId = 4802, Level = LogLevel.Warning, Message = "§48 profile read failed for the calibration capture default; prompting")]
    private partial void LogAutoFlatsPolicyReadFailed(Exception exception);

    [LoggerMessage(EventId = 4803, Level = LogLevel.Warning, Message = "§48 auto-flats announce failed for sequence {SequenceId} (the run is unaffected)")]
    private partial void LogAutoFlatsAnnounceFailed(Exception exception, Guid sequenceId);

    [LoggerMessage(EventId = 4804, Level = LogLevel.Information, Message = "§48 end-of-session flats started: run of sequence {SequenceId} → flats sequence {FlatsSequenceId} ({Frames} frames)")]
    private partial void LogAutoFlatsStarted(Guid sequenceId, Guid flatsSequenceId, int frames);

    [LoggerMessage(EventId = 4806, Level = LogLevel.Information, Message = "§48 nothing to capture: session {SessionId} of sequence {SequenceId} yielded no flats plan (no light frames?)")]
    private partial void LogAutoFlatsNothingToCapture(Guid sequenceId, Guid sessionId);

    [LoggerMessage(EventId = 4807, Level = LogLevel.Error, Message = "§48 end-of-session flats execution failed for sequence {SequenceId} / session {SessionId}")]
    private partial void LogAutoFlatsExecuteFailed(Exception exception, Guid sequenceId, Guid sessionId);

    [LoggerMessage(EventId = 4808, Level = LogLevel.Warning, Message = "§48 persisting calibration_capture_default='{Token}' failed (best-effort)")]
    private partial void LogAutoFlatsPersistFailed(Exception exception, string token);

    [LoggerMessage(EventId = 4809, Level = LogLevel.Warning, Message = "§48 WS publish of {EventType} failed (best-effort)")]
    private partial void LogAutoFlatsWsFailed(Exception exception, string eventType);

    [LoggerMessage(EventId = 4810, Level = LogLevel.Warning, Message = "§48 flats notification post failed (best-effort)")]
    private partial void LogAutoFlatsNotifyFailed(Exception exception);
}
