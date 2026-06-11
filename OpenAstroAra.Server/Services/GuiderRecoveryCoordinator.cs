#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Outcome of a §63.3 guider crash-recovery pass.</summary>
public enum GuiderRecoveryOutcome {
    /// <summary>The guider unit came back to <c>active</c> within the backoff budget.</summary>
    Recovered,
    /// <summary>The guider unit never recovered — user notified, guiding unavailable.</summary>
    Failed,
    /// <summary>No systemd host to supervise (dev box) — recovery couldn't run.</summary>
    Unsupervised,
}

/// <summary>
/// §63.3 crash-detection + auto-restart decision tree, layered on top of systemd's own
/// <c>Restart=on-failure</c>. When ARA's guider client loses its link (the daemon crashed), this
/// polls the unit's service status on a backoff schedule (1→5→15→30→60→120 s) and:
/// <list type="bullet">
///   <item><c>activating</c> → systemd is already bringing it back; keep waiting.</item>
///   <item><c>inactive</c>/<c>failed</c> → systemd won't restart it on its own; nudge it once with a
///         <c>systemctl restart</c>, then keep polling.</item>
///   <item><c>active</c> → the daemon is back; recovery succeeded.</item>
///   <item><c>unknown</c> → not a systemd host; leave the client's Error state and stop.</item>
/// </list>
/// On exhaustion it raises a §42.2 Critical notification + §51 Red diagnostic. Resuming guiding /
/// reconnecting ARA's client + the mid-sequence fault flow are deferred follow-ups (PORT_TODO).
///
/// All I/O is behind <see cref="IGuiderProcessSupervisor"/>; the backoff delay is injected, so the
/// whole tree is unit-testable with a fake supervisor and a no-op delay.
/// </summary>
public sealed partial class GuiderRecoveryCoordinator {

    // §63.3 backoff schedule between status polls. Also bounds total recovery time (~3.7 min).
    internal static readonly IReadOnlyList<TimeSpan> DefaultBackoff = new[] {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    };

    // Per-poll ceiling on a single `systemctl is-active` call. A partially-wedged systemd could
    // otherwise block a poll indefinitely (the loop's token is only cancelled by Dispose / reconnect),
    // silently stalling the whole backoff window. A timed-out poll is treated as "couldn't confirm —
    // keep waiting", not as a failure.
    internal static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromSeconds(5);

    private readonly IGuiderProcessSupervisor _supervisor;
    private readonly INotificationService _notifications;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<GuiderRecoveryCoordinator> _logger;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollTimeout;

    public GuiderRecoveryCoordinator(
        IGuiderProcessSupervisor supervisor,
        INotificationService notifications,
        IDiagnosticsService diagnostics,
        ILogger<GuiderRecoveryCoordinator> logger)
        : this(supervisor, notifications, diagnostics, logger, DefaultBackoff,
               static (delay, ct) => Task.Delay(delay, ct), DefaultPollTimeout) {
    }

    // Test seam: inject a short backoff + a no-op delay + a tight poll timeout so the tree runs instantly.
    internal GuiderRecoveryCoordinator(
        IGuiderProcessSupervisor supervisor,
        INotificationService notifications,
        IDiagnosticsService diagnostics,
        ILogger<GuiderRecoveryCoordinator> logger,
        IReadOnlyList<TimeSpan> backoff,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan pollTimeout) {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _pollTimeout = pollTimeout;
    }

    /// <summary>Run one §63.3 recovery pass for a dropped guider. Caller guarantees single-flight.</summary>
    public async Task<GuiderRecoveryOutcome> RecoverAsync(CancellationToken ct) {
        LogRecoveryStarted();
        await NotifyLostAsync(ct).ConfigureAwait(false);

        var nudged = false;
        foreach (var backoff in _backoff) {
            await _delay(backoff, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Bound each poll independently so a wedged `systemctl` can't stall the whole window.
            // A timed-out poll (not an outer cancel) is "couldn't confirm" → keep waiting, not give up.
            GuiderProcessStatus status;
            using (var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                pollCts.CancelAfter(_pollTimeout);
                try {
                    status = await _supervisor.QueryStatusAsync(pollCts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    LogPollTimedOut();
                    continue;
                }
            }
            switch (status) {
                case GuiderProcessStatus.Active:
                    LogRecovered();
                    await NotifyRecoveredAsync(ct).ConfigureAwait(false);
                    return GuiderRecoveryOutcome.Recovered;

                case GuiderProcessStatus.Activating:
                    // systemd is restarting it (Restart=on-failure / our nudge) — keep waiting.
                    continue;

                case GuiderProcessStatus.Inactive:
                case GuiderProcessStatus.Failed:
                    // Neither is a bringing-itself-back state. A `failed` unit has exhausted systemd's
                    // own retries; an `inactive` one is stopped and not restarting (a deliberate
                    // `systemctl stop` would also read inactive — but we only get here from
                    // OnConnectionLost, i.e. the daemon died on us, so treating it as "won't
                    // self-recover, nudge it" is correct). Either way we nudge a restart exactly once
                    // per pass (the sticky `nudged` flag) — re-issuing it every poll would just thrash
                    // systemd while it's already coming up — then keep polling for it to reach `active`.
                    if (!nudged) {
                        _supervisor.RequestRestart();
                        nudged = true;
                    }
                    continue;

                case GuiderProcessStatus.Unknown:
                default:
                    // Not a systemd host (dev box) — nothing to supervise. Leave the Error state, but
                    // close the loop on the "attempting to recover" warning so the user isn't left
                    // hanging without an explanation.
                    LogUnsupervised();
                    await NotifyUnsupervisedAsync(ct).ConfigureAwait(false);
                    return GuiderRecoveryOutcome.Unsupervised;
            }
        }

        LogRecoveryFailed();
        await NotifyFailedAsync(ct).ConfigureAwait(false);
        return GuiderRecoveryOutcome.Failed;
    }

    private Task NotifyLostAsync(CancellationToken ct) =>
        _notifications.CreateAsync(NewNotification(
            NotificationSeverity.Warning,
            "Guider connection lost",
            "Lost the link to the guider daemon. Attempting to recover it…"), ct);

    private Task NotifyRecoveredAsync(CancellationToken ct) =>
        _notifications.CreateAsync(NewNotification(
            NotificationSeverity.Info,
            "Guider recovered",
            "The guider daemon is back up. Reconnect the guider to resume guiding."), ct);

    private Task NotifyUnsupervisedAsync(CancellationToken ct) =>
        _notifications.CreateAsync(NewNotification(
            NotificationSeverity.Info,
            "Guider not auto-recoverable here",
            "Lost the guider link, but automatic recovery isn't available on this host. Restart the guider service manually."), ct);

    private async Task NotifyFailedAsync(CancellationToken ct) {
        await _notifications.CreateAsync(NewNotification(
            NotificationSeverity.Critical,
            "Guider failed to recover",
            "The guider daemon did not come back. Guiding is unavailable until it is restored."), ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await _diagnostics.CreateEventAsync(
            new DiagnosticEventDto(
                Id: Guid.NewGuid(),
                EventType: "guider.process.failed",
                Severity: DiagnosticHealth.Red,
                Description: $"The {SystemctlGuiderProcessSupervisor.Unit} guider daemon did not return to active after a crash, despite a restart request.",
                DetectedUtc: now,
                ClearedUtc: null,
                AutoActionTaken: true,
                AutoActionDescription: $"Requested 'systemctl restart {SystemctlGuiderProcessSupervisor.Unit}'."),
            recommendedAction: $"Check the guider service: 'systemctl status {SystemctlGuiderProcessSupervisor.Unit}' and its journal.",
            autoCorrectible: false,
            ct).ConfigureAwait(false);
    }

    private static NotificationDto NewNotification(NotificationSeverity severity, string title, string message) =>
        new(Id: Guid.NewGuid(),
            PostedUtc: DateTimeOffset.UtcNow,
            Severity: severity,
            Category: NotificationCategory.Equipment,
            Title: title,
            Message: message,
            Read: false,
            Dismissed: false,
            DismissedUtc: null,
            Payload: null,
            RelatedEntityType: "guider",
            RelatedEntityId: null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guider link lost — starting §63.3 crash recovery")]
    partial void LogRecoveryStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Guider daemon recovered to active")]
    partial void LogRecovered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guider status poll timed out (systemctl wedged?) — retrying")]
    partial void LogPollTimedOut();

    [LoggerMessage(Level = LogLevel.Error, Message = "Guider daemon did not recover within the backoff budget")]
    partial void LogRecoveryFailed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guider process supervision unavailable (no systemd host) — leaving Error state")]
    partial void LogUnsupervised();
}
