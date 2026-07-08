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
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §59.9 — the daemon-side <see cref="IAutofocusConditionGate"/>: autofocus defers while the
/// §51 diagnostics state carries an open SKY-CONDITION issue (clouds passing / aperture
/// blocked / dew formation — the patterns whose presence guarantees a failed, sky-time-wasting
/// sweep). Other open issues (disk space, guider recovery, equipment reconnects) deliberately
/// do NOT defer: they say nothing about whether stars are measurable, and probe frames are
/// never persisted so even a disk-critical state is irrelevant to focusing.
///
/// Failure posture: a broken or slow diagnostics read returns null (don't defer) — diagnostics
/// must never be able to freeze focusing. The user hears about a deferral once per episode
/// ("Autofocus deferred — clouds passing. Will run when conditions recover."), not once per
/// trigger check.
/// </summary>
public sealed partial class DiagnosticsAutofocusGate : IAutofocusConditionGate {

    /// <summary>The §51 issue types that make an autofocus pointless, with the phrasing the
    /// deferral notification carries. Enforcement-first (the §35.1 pattern): the gate is live
    /// the moment any emitter opens one of these issues.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SkyConditionIssueTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["clouds_passing"] = "clouds passing",
        ["aperture_blocked"] = "the aperture is blocked",
        ["dew_formation"] = "dew forming on the optics",
    };

    // The trigger check runs between sequence instructions; a diagnostics read is a local
    // SQLite query (~ms). The bound exists so a wedged DB can only ever cost one short pause,
    // never hang the run worker.
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly IDiagnosticsService _diagnostics;
    private readonly INotificationService? _notifications;
    private readonly ILogger<DiagnosticsAutofocusGate> _logger;
    private readonly object _episodeGate = new();
    private bool _inEpisode;

    public DiagnosticsAutofocusGate(
            IDiagnosticsService diagnostics,
            INotificationService? notifications = null,
            ILogger<DiagnosticsAutofocusGate>? logger = null) {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _notifications = notifications;
        _logger = logger ?? NullLogger<DiagnosticsAutofocusGate>.Instance;
    }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fail-open boundary: any diagnostics read fault (DB, wrapper, cancellation surfacing as AggregateException) must degrade to 'don't defer' — diagnostics must never freeze focusing. CA1031's log-and-recover boundary applies.")]
    public string? DeferralReason() {
        DiagnosticsStateDto state;
        try {
            var read = _diagnostics.GetStateAsync(CancellationToken.None);
            if (!read.Wait(ReadTimeout)) {
                LogReadTimedOut();
                return null;
            }
            state = read.Result;
        } catch (Exception ex) {
            LogReadFailed(ex);
            return null;
        }

        var issue = state.OpenIssues.FirstOrDefault(i => SkyConditionIssueTypes.ContainsKey(i.IssueType));
        lock (_episodeGate) {
            if (issue is null) {
                _inEpisode = false;
                return null;
            }
            var reason = SkyConditionIssueTypes[issue.IssueType];
            if (!_inEpisode) {
                _inEpisode = true;
                LogDeferred(reason);
                _ = NotifyQuietlyAsync(reason);
            }
            return reason;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the deferral decision itself (the §58.7 precedent). Log-and-recover boundary.")]
    private async Task NotifyQuietlyAsync(string reason) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Info,
                Category: NotificationCategory.Sequence,
                Title: "Autofocus deferred",
                Message: $"Autofocus deferred — {reason}. Will run when conditions recover.",
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogNotifyFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "§59.9 autofocus deferred — {Reason}; triggers retry once conditions recover")]
    private partial void LogDeferred(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§59.9 diagnostics read timed out — autofocus proceeds (diagnostics must never freeze focusing)")]
    private partial void LogReadTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "§59.9 diagnostics read failed — autofocus proceeds (diagnostics must never freeze focusing)")]
    private partial void LogReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§59.9 deferral notification failed — the deferral itself stands")]
    private partial void LogNotifyFailed(Exception ex);
}
