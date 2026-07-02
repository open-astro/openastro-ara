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

/// <summary>
/// Builds the §46 notification DTOs emitted at daemon startup. Keeps the
/// copy + severity decisions out of Program.cs so they're unit-testable
/// without spinning the whole web host. The reconciler returns a structural
/// <see cref="SequenceReconcileResult"/>; the factory translates
/// that into user-visible inbox text.
/// </summary>
public static class StartupNotificationFactory {

    /// <summary>
    /// Build the §46 notification for a non-Clean reconciler outcome.
    /// Interrupted → Warning (sequence was running, user should know).
    /// Corrupt → Critical (checkpoint file was damaged, possible data loss).
    /// Caller should not invoke this with <see cref="SequenceReconcileOutcome.Clean"/>.
    /// </summary>
    public static NotificationDto ForReconcilerResult(SequenceReconcileResult result) {
        var now = DateTimeOffset.UtcNow;
        return result.Outcome switch {
            SequenceReconcileOutcome.Interrupted => new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: now,
                Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Sequence,
                Title: "Previous sequence ended unexpectedly",
                Message: BuildInterruptedMessage(result.PreviousState),
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: result.PreviousState is not null ? "sequence" : null,
                RelatedEntityId: result.PreviousState?.SequenceId.ToString()),
            SequenceReconcileOutcome.Corrupt => new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: now,
                Severity: NotificationSeverity.Critical,
                Category: NotificationCategory.Sequence,
                Title: "Sequence checkpoint was damaged",
                Message: BuildCorruptMessage(result.QuarantinedPath),
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null),
            _ => throw new ArgumentException(
                $"ForReconcilerResult cannot build a notification for outcome {result.Outcome}",
                nameof(result)),
        };
    }

    /// <summary>
    /// Build the §51 diagnostic event for a Corrupt reconciler outcome. We
    /// don't emit a diagnostic for Interrupted — that's a routine "previous
    /// session ended" signal, not an open issue. Corrupt is a real
    /// data-integrity problem worth surfacing in the §51 panel as a Red,
    /// auto-cleared event (we already handled the file via quarantine, so
    /// no user action is required to clear it).
    /// </summary>
    public static (DiagnosticEventDto Event, string? RecommendedAction, bool? AutoCorrectible)
            DiagnosticForCorruptResult(SequenceReconcileResult result) {
        if (result.Outcome != SequenceReconcileOutcome.Corrupt) {
            throw new ArgumentException(
                $"DiagnosticForCorruptResult only handles Corrupt; got {result.Outcome}",
                nameof(result));
        }
        var now = DateTimeOffset.UtcNow;
        var dto = new DiagnosticEventDto(
            Id: Guid.NewGuid(),
            EventType: "sequence.checkpoint.corrupt",
            Severity: DiagnosticHealth.Red,
            Description: BuildCorruptDiagnosticDescription(result.QuarantinedPath),
            DetectedUtc: now,
            // Pre-cleared — the reconciler already quarantined the file, so
            // there's nothing for the user to do. §51 panel shows this in
            // history; it's never in the open-issues count.
            ClearedUtc: now,
            AutoActionTaken: true,
            AutoActionDescription: result.QuarantinedPath is null
                ? "Deleted the unreadable checkpoint to allow startup to proceed."
                : $"Quarantined the unreadable checkpoint to {result.QuarantinedPath}.");
        return (dto, RecommendedAction: null, AutoCorrectible: true);
    }

    private static string BuildCorruptDiagnosticDescription(string? quarantinedPath) =>
        quarantinedPath is null
            ? "Sequence checkpoint file was unreadable. It could not be quarantined and was deleted so startup could proceed."
            : $"Sequence checkpoint file was unreadable. Quarantined to {quarantinedPath} for diagnostics.";

    private static string BuildInterruptedMessage(SequenceRunStateDto? previous) {
        if (previous is null) {
            return "A sequence was running when the daemon stopped. It was not auto-resumed; re-start it from the Sequencer panel if needed.";
        }
        return $"Sequence {previous.SequenceId:D} was at instruction {previous.InstructionsCompleted}/{previous.InstructionsTotal} when the daemon stopped. It was not auto-resumed per the §28.2 safety policy; re-start it from the Sequencer panel if needed.";
    }

    private static string BuildCorruptMessage(string? quarantinedPath) {
        if (string.IsNullOrEmpty(quarantinedPath)) {
            return "The active-sequence checkpoint file was unreadable and could not be quarantined. The previous sequence state is lost; no auto-resume was attempted.";
        }
        return $"The active-sequence checkpoint file was unreadable. It has been quarantined to {quarantinedPath} for diagnostics. The previous sequence state is lost; no auto-resume was attempted.";
    }
}