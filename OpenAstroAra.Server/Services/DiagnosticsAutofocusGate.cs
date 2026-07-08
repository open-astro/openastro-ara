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
///
/// Concurrency assumption: the daemon controls one rig, so in practice one sequence run's
/// worker consults this singleton at a time. If concurrent runs ever become a real mode,
/// their trigger checks would contend on the internal lock (each bounded by the read timeout)
/// and share one notification episode — safe, just coarser than per-run.
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
    private readonly System.Diagnostics.Stopwatch _sinceLastRead = new();
    private string? _cachedReason;
    private string? _episodeReason;

    /// <summary>
    /// One diagnostics read serves every trigger of a RunTriggers pass: the sequencer asks
    /// each registered AF trigger after every item transition, and in a bad-sky episode
    /// several can be "due" at once — without this, each would block on its own bounded
    /// read (compounding to ~10 s of run-thread stall per tick when diagnostics is
    /// degraded). One second is far below any sky-condition change cadence and caps a
    /// degraded read at one bounded wait per second. Internal so tests can set Zero to
    /// exercise state transitions call-by-call.
    /// </summary>
    internal TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(1);

    public DiagnosticsAutofocusGate(
            IDiagnosticsService diagnostics,
            INotificationService? notifications = null,
            ILogger<DiagnosticsAutofocusGate>? logger = null) {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _notifications = notifications;
        _logger = logger ?? NullLogger<DiagnosticsAutofocusGate>.Instance;
    }

    /// <inheritdoc/>
    public string? DeferralReason() {
        // The lock also serializes concurrent callers behind one read: whoever arrives while
        // a read is in flight gets the freshly cached answer instead of issuing its own.
        lock (_episodeGate) {
            if (_sinceLastRead.IsRunning && _sinceLastRead.Elapsed < CacheTtl) {
                return _cachedReason;
            }
            _cachedReason = ReadDeferralReasonLocked();
            _sinceLastRead.Restart();
            return _cachedReason;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fail-open boundary: any diagnostics read/shape fault (DB, wrapper, a null issue list or issue type from a future implementation, cancellation surfacing as AggregateException) must degrade to 'don't defer' — diagnostics must never freeze or crash focusing. CA1031's log-and-recover boundary applies.")]
    private string? ReadDeferralReasonLocked() {
        // The whole read AND match run inside the fail-open boundary: the class promises
        // diagnostics can never crash focusing, and a malformed state (null issue list /
        // null issue type from some future IDiagnosticsService) is a diagnostics fault too.
        DiagnosticIssueDto? issue;
        var cts = new CancellationTokenSource();
        var disposeNow = true;
        try {
            var read = _diagnostics.GetStateAsync(cts.Token);
            if (!read.Wait(ReadTimeout)) {
                LogReadTimedOut();
                // ACTUALLY tear the abandoned read down — Wait() only stops waiting; without
                // the cancel, a sustained wedge would spawn a fresh read (each holding its
                // own open SQLite connection) every ReadTimeout+CacheTtl for hours. The
                // continuation observes a late non-cancellation fault (it can explain why
                // diagnostics was wedged) and disposes the source once the task settles.
                cts.Cancel();
                disposeNow = false;
                _ = read.ContinueWith(t => {
                    if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException) {
                        LogReadFailed(t.Exception);
                    }
                    cts.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return null;
            }
            issue = read.Result.OpenIssues?.FirstOrDefault(
                i => i?.IssueType is { } type && SkyConditionIssueTypes.ContainsKey(type));
        } catch (Exception ex) {
            LogReadFailed(ex);
            return null;
        } finally {
            if (disposeNow) {
                cts.Dispose();
            }
        }

        if (issue is null) {
            _episodeReason = null;
            return null;
        }
        var reason = SkyConditionIssueTypes[issue.IssueType];
        // One alert per episode, keyed by REASON — if clouds clear but dew opens before
        // diagnostics ever reports clean, the situation changed and the user hears the new
        // reason instead of a stale "clouds passing" notification.
        if (_episodeReason != reason) {
            _episodeReason = reason;
            LogDeferred(reason);
            _ = NotifyQuietlyAsync(reason);
        }
        return reason;
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
