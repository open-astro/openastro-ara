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
/// must not fill the inbox. Posting is fire-and-forget and never throws into the caller.
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
        var key = (isError ? "E|" : "W|") + header + "|" + message;
        var now = _clock();
        if (_lastPosted.TryGetValue(key, out var last) && now - last < RepeatWindow) {
            return; // the same toast inside the window: the inbox already has it
        }
        _lastPosted[key] = now;
        _ = PostAsync(isError, header, message, now);
    }

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
