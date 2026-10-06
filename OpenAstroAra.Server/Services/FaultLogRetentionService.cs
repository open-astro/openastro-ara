#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.5 fault-log retention (#1145). The <c>faults</c> table was append-only for the daemon's
/// lifetime; this sweep deletes rows detected more than <c>storage.fault_log_retention_days</c>
/// ago, once shortly after startup and then once a day. The knob is read live from the profile
/// on every pass, so a settings change applies on the next sweep; 0 keeps everything, and a
/// profile read failure skips the pass rather than guessing. Best-effort like the §43-2b backup
/// pruner: a failed sweep is logged and retried next tick, never a daemon fault.
/// </summary>
public sealed partial class FaultLogRetentionService : BackgroundService {

    /// <summary>Mirrors <see cref="Contracts.StorageSettingsDto"/>'s ctor default.</summary>
    public const int DefaultRetentionDays = 90;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(2);

    private readonly IProfileStore _profiles;
    private readonly IFaultLogService _faultLog;
    private readonly ILogger<FaultLogRetentionService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _startupDelay;
    private readonly Func<DateTimeOffset> _clock;

    public FaultLogRetentionService(
            IProfileStore profiles,
            IFaultLogService faultLog,
            ILogger<FaultLogRetentionService>? logger = null,
            TimeSpan? interval = null,
            TimeSpan? startupDelay = null,
            Func<DateTimeOffset>? clock = null) {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _faultLog = faultLog ?? throw new ArgumentNullException(nameof(faultLog));
        _logger = logger ?? NullLogger<FaultLogRetentionService>.Instance;
        _interval = interval ?? DefaultInterval;
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The cutoff for a retention of <paramref name="days"/>, or null when nothing is pruned
    /// (0 keeps everything; a negative value is treated the same, defensively).</summary>
    public static DateTimeOffset? CutoffFor(int days, DateTimeOffset now) =>
        days <= 0 ? null : now - TimeSpan.FromDays(days);

    /// <summary>One sweep: read the knob, prune. Returns the rows removed (0 when retention is off).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct) {
        var days = _profiles.GetStorageSettings().FaultLogRetentionDays;
        if (CutoffFor(days, _clock()) is not { } cutoff) {
            return 0;
        }
        return await _faultLog.PruneBeforeAsync(cutoff, ct).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort sweep: a profile read or SQLite fault is logged and retried next tick; it must never tear down the daemon's hosted services.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                LogSweepFailed(ex);
            }
            try {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                    break;
                }
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault-log retention sweep failed — will retry next tick (§42.5, #1145)")]
    private partial void LogSweepFailed(Exception ex);
}
