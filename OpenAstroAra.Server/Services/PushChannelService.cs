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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §54 push channels (the v0.1.0 fence was lifted 2026-07-06): forwards notifications to
/// Pushover and/or Telegram so a genuinely away-from-the-rig user hears about overnight events —
/// the §58 unattended-safety work (flip failures, dark-hours escalation) all funnels into
/// notifications, and until now the stored tokens sent NOTHING.
///
/// <para><b>Sending rules</b> (per channel, evaluated at each notification): both of the
/// channel's values are configured (Pushover app token + user key; Telegram bot token + chat
/// id), the notification's severity is Warning or above (the push surface is for problems, not
/// housekeeping Info), and the matching per-trigger toggle allows the category. Delivery is
/// BEST-EFFORT and fire-and-forget off the caller's path: a push-provider outage can never block
/// or delay the in-app notification (which §46.1 keeps as the source of truth), and failures log
/// once per send. Requests carry a 5 s timeout.</para>
/// </summary>
public sealed partial class PushChannelService : IDisposable {

    private static readonly Uri PushoverUri = new("https://api.pushover.net/1/messages.json");

    private readonly IProfileStore _profiles;
    private readonly HttpClient _http;
    private readonly ILogger<PushChannelService> _logger;
    private bool _disposed;

    /// <summary>Production ctor: a plain HttpClient. Tests inject a canned
    /// <see cref="HttpMessageHandler"/> to capture the outbound requests.</summary>
    public PushChannelService(IProfileStore profiles, ILogger<PushChannelService>? logger = null,
            HttpMessageHandler? handler = null) {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? NullLogger<PushChannelService>.Instance;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>Whether the per-trigger §54 toggles allow pushing this notification. The toggle
    /// set predates the push channels (it gates the in-app triggers), so the mapping is by
    /// category: Safety → OnSafetyEvent, Storage → OnDiskSpaceLow, Equipment/Software/Alarm ride
    /// OnCriticalDiagnostic. Sequence is TWO populations sharing one category: pause-ish events
    /// gate on OnSequencePaused, while a CRITICAL sequence event (the startup reconciler's
    /// interrupted/corrupt-checkpoint alerts, a halted run) also passes via OnCriticalDiagnostic
    /// — turning "notify on sequence paused" off must not silence checkpoint-corruption pages.
    /// Pure.</summary>
    internal static bool TriggerAllows(NotificationsSettingsDto s, NotificationCategory category,
            NotificationSeverity severity) => category switch {
        NotificationCategory.Safety => s.OnSafetyEvent,
        NotificationCategory.Storage => s.OnDiskSpaceLow,
        NotificationCategory.Sequence => s.OnSequencePaused
            || (severity == NotificationSeverity.Critical && s.OnCriticalDiagnostic),
        _ => s.OnCriticalDiagnostic,
    };

    /// <summary>Severity gate: the push surface is for problems. Info never pushes.</summary>
    internal static bool SeverityAllows(NotificationSeverity severity) =>
        severity >= NotificationSeverity.Warning;

    /// <summary>Fire-and-forget forward of <paramref name="notification"/> to every configured
    /// channel. Returns immediately; the sends run on the thread pool so the notification
    /// chokepoint (capture path, flip executor, monitors) is never delayed by a slow provider.</summary>
    public void ForwardInBackground(NotificationDto notification) {
        ArgumentNullException.ThrowIfNull(notification);
        if (_disposed || !SeverityAllows(notification.Severity)) {
            return;
        }
        // EVERYTHING else — including the profile read — runs off the caller's path, so the
        // notification chokepoint (capture path / flip executor / monitors) never waits on the
        // store lock, let alone a provider.
        _ = Task.Run(() => SendAllAsync(notification), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Push delivery is best-effort by contract: a profile-read or HTTP/provider fault must be logged and dropped, never propagated back toward the notification chokepoint (capture path / flip executor). CA1031's log-and-recover boundary applies.")]
    private async Task SendAllAsync(NotificationDto n) {
        NotificationsSettingsDto s;
        try {
            s = _profiles.GetNotificationsSettings();
        } catch (Exception ex) {
            LogSettingsReadFailed(ex);
            return;
        }
        if (!TriggerAllows(s, n.Category, n.Severity)) {
            return;
        }
        var pushover = !string.IsNullOrWhiteSpace(s.PushoverToken)
            && !string.IsNullOrWhiteSpace(s.PushoverUserKey);
        var telegram = !string.IsNullOrWhiteSpace(s.TelegramBotToken)
            && !string.IsNullOrWhiteSpace(s.TelegramChatId);
        if (pushover) {
            try {
                await SendPushoverAsync(n, s).ConfigureAwait(false);
            } catch (Exception ex) {
                LogPushFailed(ex, "Pushover", n.Title);
            }
        }
        if (telegram) {
            try {
                await SendTelegramAsync(n, s).ConfigureAwait(false);
            } catch (Exception ex) {
                LogPushFailed(ex, "Telegram", n.Title);
            }
        }
    }

    private async Task SendPushoverAsync(NotificationDto n, NotificationsSettingsDto s) {
        // https://pushover.net/api — form-encoded; priority 1 (high) for Critical so the
        // provider bypasses the receiving device's quiet hours, 0 otherwise.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["token"] = s.PushoverToken.Trim(),
            ["user"] = s.PushoverUserKey.Trim(),
            ["title"] = $"ARA: {n.Title}",
            ["message"] = n.Message,
            ["priority"] = n.Severity == NotificationSeverity.Critical ? "1" : "0",
        });
        using var response = await _http.PostAsync(PushoverUri, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LogPushSent("Pushover", n.Title);
    }

    private async Task SendTelegramAsync(NotificationDto n, NotificationsSettingsDto s) {
        // https://core.telegram.org/bots/api#sendmessage — the bot token is a URL path segment
        // BY TELEGRAM'S OWN DESIGN (there is no header/body alternative). Defensive note: never
        // wire request-URI logging/tracing (HttpClientFactory logging handlers, OpenTelemetry
        // HTTP instrumentation) onto this client, or the token leaks into logs; the failure
        // logging below deliberately records only the exception, not the URI.
        // EscapeDataString hardens against copy-paste garbage (stray reserved characters would
        // otherwise make new Uri() throw, silently dropping the push into the best-effort catch).
        var uri = new Uri($"https://api.telegram.org/bot{Uri.EscapeDataString(s.TelegramBotToken.Trim())}/sendMessage");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["chat_id"] = s.TelegramChatId.Trim(),
            ["text"] = $"ARA: {n.Title}\n{n.Message}",
        });
        using var response = await _http.PostAsync(uri, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LogPushSent("Telegram", n.Title);
    }

    public void Dispose() {
        _disposed = true;
        _http.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "§54 push: '{Title}' forwarded via {Channel}")]
    private partial void LogPushSent(string channel, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§54 push via {Channel} failed for '{Title}' — the in-app notification is unaffected")]
    private partial void LogPushFailed(Exception ex, string channel, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§54 push: reading notification settings failed; skipping the forward")]
    private partial void LogSettingsReadFailed(Exception ex);
}
