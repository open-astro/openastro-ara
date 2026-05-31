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
/// <see cref="SequenceStartupReconciler.Result"/>; the factory translates
/// that into user-visible inbox text.
/// </summary>
public static class StartupNotificationFactory {

    /// <summary>
    /// Build the §46 notification for a non-Clean reconciler outcome.
    /// Interrupted → Warning (sequence was running, user should know).
    /// Corrupt → Critical (checkpoint file was damaged, possible data loss).
    /// Caller should not invoke this with <see cref="SequenceStartupReconciler.Outcome.Clean"/>.
    /// </summary>
    public static NotificationDto ForReconcilerResult(SequenceStartupReconciler.Result result) {
        var now = DateTimeOffset.UtcNow;
        return result.Outcome switch {
            SequenceStartupReconciler.Outcome.Interrupted => new NotificationDto(
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
            SequenceStartupReconciler.Outcome.Corrupt => new NotificationDto(
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

    private static string BuildInterruptedMessage(SequenceRunStateDto? previous) {
        if (previous is null) {
            return "A sequence was running when the daemon stopped. It was not auto-resumed; re-start it from the Sequencer panel if needed.";
        }
        return $"Sequence {previous.SequenceId:D} was at frame {previous.FramesCompleted}/{previous.FramesTotal} when the daemon stopped. It was not auto-resumed per the §28.2 safety policy; re-start it from the Sequencer panel if needed.";
    }

    private static string BuildCorruptMessage(string? quarantinedPath) {
        if (string.IsNullOrEmpty(quarantinedPath)) {
            return "The active-sequence checkpoint file was unreadable and could not be quarantined. The previous sequence state is lost; no auto-resume was attempted.";
        }
        return $"The active-sequence checkpoint file was unreadable. It has been quarantined to {quarantinedPath} for diagnostics. The previous sequence state is lost; no auto-resume was attempted.";
    }
}
