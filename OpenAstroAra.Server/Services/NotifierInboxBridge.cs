#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1174 — the ported code's warning/error toasts (<see cref="Notifier"/>, ~45 call sites in
/// Equipment, Sequencer, PlateSolving and Profile) become §46 inbox entries. Installed once at
/// boot with <c>Notifier.Use(...)</c>. A warning is <see cref="NotificationSeverity.Warning"/>,
/// an error <see cref="NotificationSeverity.Error"/>, category Equipment (that is what the
/// callers are: devices, the guider, the solver). The same text within
/// <see cref="RepeatWindow"/> is posted once — a device loop that warns every refresh tick
/// must not fill the inbox. Posting is handed to the thread pool (<c>Task.Run</c>): the
/// SQLite write, its busy wait and the unattended-escalation profile read must never run on
/// the device thread that raised the toast (r1 finding — PHD2's settle path, the centering
/// solver and the sequencer all toast mid-run). Nothing here throws into the caller.
/// </summary>
public sealed partial class NotifierInboxBridge : INotifierSink {

    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    private readonly INotificationService _notifications;
    private readonly ILogger<NotifierInboxBridge> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPosted = new(StringComparer.Ordinal);

    public NotifierInboxBridge(INotificationService notifications, ILogger<NotifierInboxBridge> logger, Func<DateTimeOffset>? clock = null) {
        _notifications = notifications;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Notify(bool isError, string? header, string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }
        header = string.IsNullOrWhiteSpace(header) ? null : header.Trim(); // a blank header is no header
        var key = (isError ? "E|" : "W|") + header + "|" + message;
        var now = _clock();
        if (!ClaimSlot(key, now)) {
            return; // the same toast inside the window: the inbox already has it
        }
        Prune(now);
        _ = Task.Run(() => PostAsync(isError, header, message, now));
    }

    /// <summary>Atomic check-and-set on the per-text slot: two threads raising the same toast at
    /// the same instant get one post, not two.</summary>
    private bool ClaimSlot(string key, DateTimeOffset now) {
        while (true) {
            if (_lastPosted.TryAdd(key, now)) {
                return true;
            }
            if (!_lastPosted.TryGetValue(key, out var last)) {
                continue; // pruned between the two calls — try to add again
            }
            if (now - last < RepeatWindow) {
                return false;
            }
            if (_lastPosted.TryUpdate(key, now, last)) {
                return true;
            }
        }
    }

    /// <summary>Drop slots older than the window so changing texts (exception messages, error
    /// codes) cannot grow the table over a multi-night uptime.</summary>
    private void Prune(DateTimeOffset now) {
        foreach (var entry in _lastPosted) {
            if (now - entry.Value >= RepeatWindow) {
                _lastPosted.TryRemove(entry);
            }
        }
    }

    /// <summary>Slots currently held (for tests).</summary>
    internal int SlotCount => _lastPosted.Count;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget inbox write behind a device-layer toast: a storage fault is logged, never surfaced at the call site.")]
    private async Task PostAsync(bool isError, string? header, string message, DateTimeOffset now) {
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: now,
                Severity: isError ? NotificationSeverity.Error : NotificationSeverity.Warning,
                Category: NotificationCategory.Equipment,
                Title: header ?? (isError ? "Equipment error" : "Equipment warning"),
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogInboxPostFailed(ex, message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifier → inbox: posting '{Message}' failed; the daemon log line above is the only record")]
    private partial void LogInboxPostFailed(Exception ex, string message);
}
