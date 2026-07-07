#region "copyright"

/*
    Copyright (c) 2026 - present Open Astro contributors

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §35.4 safety-reaction engine. Polls the connected Alpaca SafetyMonitor and, on a
/// safe→unsafe transition, executes the profile's <c>safety_policies.on_unsafe</c>
/// action: <c>ignore</c> (notify only), <c>park_only</c> (stop guiding + park, the
/// sequence untouched), <c>pause_and_park</c> (pause active runs at the instruction
/// boundary + stop guiding + park — the default), or <c>abort_and_park</c>. The
/// <c>safety.unsafe</c> WS event fires BEFORE the action per §35.4 so WILMA can raise
/// its alarm modal while the daemon reacts. When conditions turn safe again and the
/// engine paused the runs itself, <c>auto_resume_when_safe</c> + <c>resume_delay_min</c>
/// drive the resume: unpark → wait for the mount to report unparked → tracking on →
/// release exactly the runs this engine paused. A <c>PausedAwaitingUser</c> run is
/// never auto-resumed, and an automated action never counts as §58.12 user activity.
/// </summary>
public sealed partial class SafetyReactionService : IHostedService, IDisposable {

    private readonly ISafetyMonitorService? _safetyMonitor;
    private readonly IProfileStore? _profiles;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly IGuiderService? _guider;
    private readonly ITelescopeService? _telescope;
    private readonly INotificationService? _notifications;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<SafetyReactionService> _logger;

    private readonly object _lock = new();
    private Timer? _timer;
    // null = unknown (monitor disconnected / never read). A disconnect is neither
    // safe nor unsafe: it cancels a pending auto-resume but takes no new action.
    private bool? _lastSafe;
    // Non-empty exactly while a pause_and_park reaction of OURS is outstanding —
    // the run ids the engine paused, i.e. the only runs auto-resume may release.
    private readonly List<Guid> _pausedRunIds = new();
    // Single-flight: reactions and resumes run on background tasks; a poll tick
    // never starts a second one while the first is still executing.
    private bool _reacting;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Owned by the auto-resume countdown task, which disposes its local reference in a finally after nulling this field; disposing here would race that owner (the UnattendedShutdownService._ladderCts pattern).")]
    private CancellationTokenSource? _resumeCountdown;
    // The in-flight auto-resume countdown task; kept so the launch site has an
    // owner for the task (and tests can await it via TickAsync-driven flows).
    private Task? _resumeWorker;
    private bool _disposed;

    // ─── test seams (the UnattendedShutdownService knob pattern) ───
    /// <summary>§35.4 poll cadence; production 10 s, tests compress it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maps the profile's resume_delay_min to the actual delay; tests compress it.</summary>
    internal Func<int, TimeSpan> ResumeDelayFromMinutes { get; set; } = m => TimeSpan.FromMinutes(Math.Max(0, m));
    /// <summary>Bound on the wait for the mount to report unparked before runs resume.</summary>
    internal TimeSpan UnparkTimeout { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Cadence of the unpark read-back poll inside <see cref="UnparkTimeout"/>.</summary>
    internal TimeSpan UnparkPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public SafetyReactionService(
            ISafetyMonitorService? safetyMonitor = null,
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            IGuiderService? guider = null,
            ITelescopeService? telescope = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            ILogger<SafetyReactionService>? logger = null) {
        _safetyMonitor = safetyMonitor;
        _profiles = profiles;
        _sequencerResolver = sequencerResolver;
        _guider = guider;
        _telescope = telescope;
        _notifications = notifications;
        _ws = ws;
        _logger = logger ?? NullLogger<SafetyReactionService>.Instance;
    }

    // ─── pure decision helpers (unit-tested sim-free) ───

    internal enum Transition { None, BecameUnsafe, BecameSafe }

    /// <summary>
    /// Classifies one poll observation against the previous one. First-ever
    /// observation of unsafe (prev unknown) counts as BecameUnsafe — a monitor
    /// that connects during rain must trigger the reaction, not wait for a
    /// flap. First observation of safe is None (nothing to react to, nothing
    /// to resume). An unknown <paramref name="now"/> (disconnect) is None:
    /// losing the monitor is not evidence conditions changed.
    /// </summary>
    internal static Transition ClassifyTransition(bool? prev, bool? now) => (prev, now) switch {
        (_, null) => Transition.None,
        (null or true, false) => Transition.BecameUnsafe,
        (false, true) => Transition.BecameSafe,
        _ => Transition.None,
    };

    internal enum UnsafeAction { Ignore, ParkOnly, PauseAndPark, AbortAndPark }

    /// <summary>
    /// Maps the profile's snake_case on_unsafe token. An unrecognized token maps
    /// to the default <c>pause_and_park</c> (fail toward protecting the gear; the
    /// mapping is logged so a typo'd profile is visible, never silently inert).
    /// </summary>
    internal static UnsafeAction ParseAction(string? token) => token switch {
        "ignore" => UnsafeAction.Ignore,
        "park_only" => UnsafeAction.ParkOnly,
        "abort_and_park" => UnsafeAction.AbortAndPark,
        "pause_and_park" => UnsafeAction.PauseAndPark,
        _ => UnsafeAction.PauseAndPark,
    };

    internal static string ActionToken(UnsafeAction action) => action switch {
        UnsafeAction.Ignore => "ignore",
        UnsafeAction.ParkOnly => "park_only",
        UnsafeAction.AbortAndPark => "abort_and_park",
        _ => "pause_and_park",
    };

    // ─── poll loop ───

    private void OnTick(object? state) {
        // Timer callback boundary: nothing here may throw (a timer-callback
        // exception kills the process), and overlapping work is skipped.
        _ = TickAsync();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timer-callback boundary: any fault is logged and the next tick retries; an escaped exception would be an unobserved task fault.")]
    internal async Task TickAsync() {
        if (_safetyMonitor is null) {
            return;
        }
        try {
            SafetyMonitorDto? dto;
            try {
                dto = await _safetyMonitor.GetAsync(CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogMonitorReadFailed(ex);
                dto = null;
            }
            bool? nowSafe = dto is { State: EquipmentConnectionState.Connected } ? dto.Safe : null;

            Transition transition;
            lock (_lock) {
                if (_disposed) {
                    return;
                }
                transition = ClassifyTransition(_lastSafe, nowSafe);
                _lastSafe = nowSafe;
                if (nowSafe is null or false) {
                    // Unsafe again (or the monitor vanished) — a pending resume
                    // countdown is no longer standing on solid ground.
                    CancelResumeCountdownLocked();
                }
                if (transition == Transition.BecameUnsafe) {
                    if (_reacting) {
                        // A reaction is still executing (e.g. a slow park from a
                        // fast safe→unsafe→safe→unsafe flap) — don't stack another.
                        transition = Transition.None;
                    } else {
                        _reacting = true;
                    }
                }
            }

            switch (transition) {
                case Transition.BecameUnsafe:
                    try {
                        await ReactToUnsafeAsync(dto).ConfigureAwait(false);
                    } finally {
                        lock (_lock) { _reacting = false; }
                    }
                    break;
                case Transition.BecameSafe:
                    await OnBecameSafeAsync().ConfigureAwait(false);
                    break;
            }
        } catch (Exception ex) {
            LogTickFailed(ex);
        }
    }

    // ─── unsafe reaction ───

    private async Task ReactToUnsafeAsync(SafetyMonitorDto? monitor) {
        var policy = ReadPolicies();
        var action = ParseAction(policy?.OnUnsafe);
        if (policy is not null && ParseActionIsFallback(policy.OnUnsafe)) {
            LogUnknownActionToken(policy.OnUnsafe);
        }

        // §35.4: the unsafe event fires BEFORE the action so the client can alarm
        // while the daemon reacts.
        await PublishAsync(WsEventCatalog.SafetyUnsafe, new JsonObject {
            ["device_name"] = monitor?.Name,
            ["action"] = ActionToken(action),
        }).ConfigureAwait(false);

        var pausedRuns = 0;
        var abortedRuns = 0;
        if (action is UnsafeAction.PauseAndPark) {
            var ids = await PauseActiveRunsAsync().ConfigureAwait(false);
            pausedRuns = ids.Count;
            lock (_lock) {
                _pausedRunIds.Clear();
                _pausedRunIds.AddRange(ids);
            }
        } else if (action is UnsafeAction.AbortAndPark) {
            abortedRuns = await AbortActiveRunsAsync().ConfigureAwait(false);
        }

        var guidingStopped = false;
        var parkRequested = false;
        if (action is not UnsafeAction.Ignore) {
            guidingStopped = await StopGuidingQuietlyAsync().ConfigureAwait(false);
            parkRequested = await ParkQuietlyAsync().ConfigureAwait(false);
        }

        LogReacted(ActionToken(action), pausedRuns, abortedRuns, guidingStopped, parkRequested);

        await PublishAsync(WsEventCatalog.SafetyActionTaken, new JsonObject {
            ["action"] = ActionToken(action),
            ["runs_paused"] = pausedRuns,
            ["runs_aborted"] = abortedRuns,
            ["guiding_stopped"] = guidingStopped,
            ["park_requested"] = parkRequested,
        }).ConfigureAwait(false);

        var deviceName = string.IsNullOrWhiteSpace(monitor?.Name) ? "The safety monitor" : monitor!.Name;
        var (severity, title, message) = action switch {
            UnsafeAction.Ignore => (NotificationSeverity.Warning, "Unsafe conditions reported",
                deviceName + " reports conditions are UNSAFE. Your safety policy is set to take no action — imaging continues. Check the sky."),
            UnsafeAction.ParkOnly => (NotificationSeverity.Critical, "Unsafe — mount parking",
                deviceName + " reports conditions are UNSAFE. Guiding was stopped and the mount was told to park; any running sequence was left as-is per your safety policy."),
            UnsafeAction.AbortAndPark => (NotificationSeverity.Critical, "Unsafe — sequence aborted, mount parking",
                deviceName + " reports conditions are UNSAFE. " + (abortedRuns > 0 ? "The running sequence was aborted, " : "No sequence was running, ")
                + "guiding was stopped, and the mount was told to park."),
            _ => (NotificationSeverity.Critical, "Unsafe — sequence paused, mount parking",
                deviceName + " reports conditions are UNSAFE. " + (pausedRuns > 0 ? "The running sequence pauses at the current instruction, " : "No sequence was running, ")
                + "guiding was stopped, and the mount was told to park."
                + (policy is { AutoResumeWhenSafe: true } && pausedRuns > 0
                    ? " It will auto-resume after conditions stay safe for " + policy.ResumeDelayMin + " min."
                    : string.Empty)),
        };
        await NotifyQuietlyAsync(severity, title, message).ConfigureAwait(false);
    }

    private static bool ParseActionIsFallback(string? token) =>
        token is not ("ignore" or "park_only" or "abort_and_park" or "pause_and_park");

    // ─── safe again ───

    private async Task OnBecameSafeAsync() {
        await PublishAsync(WsEventCatalog.SafetySafe, new JsonObject()).ConfigureAwait(false);

        var policy = ReadPolicies();
        bool armResume;
        lock (_lock) {
            armResume = _pausedRunIds.Count > 0 && policy is { AutoResumeWhenSafe: true } && _resumeCountdown is null;
        }
        if (!armResume) {
            if (policy is { AutoResumeWhenSafe: false }) {
                lock (_lock) { _pausedRunIds.Clear(); }
            }
            return;
        }

        var delay = ResumeDelayFromMinutes(policy!.ResumeDelayMin);
        lock (_lock) {
            if (_disposed || _resumeCountdown is not null) {
                return;
            }
            // Task.Run + stored worker (the §58.12 CountdownAsync launch pattern):
            // the countdown owns cts and disposes it in its finally.
            var cts = new CancellationTokenSource();
            _resumeCountdown = cts;
            _resumeWorker = Task.Run(() => ResumeCountdownAsync(cts, delay), CancellationToken.None);
        }
        LogResumeArmed(policy.ResumeDelayMin);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background countdown boundary: a fault is logged and the paused run simply stays paused for the user — never an unobserved task exception.")]
    private async Task ResumeCountdownAsync(CancellationTokenSource cts, TimeSpan delay) {
        try {
            try {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                LogResumeCancelled();
                return;
            }
            List<Guid> ids;
            lock (_lock) {
                if (_disposed || _lastSafe != true) {
                    return;
                }
                ids = new List<Guid>(_pausedRunIds);
                _pausedRunIds.Clear();
            }
            if (ids.Count == 0) {
                return;
            }

            var unparked = await UnparkAndWaitQuietlyAsync(cts.Token).ConfigureAwait(false);
            var resumed = await ResumeRunsAsync(ids).ConfigureAwait(false);
            LogAutoResumed(resumed, unparked);

            await PublishAsync(WsEventCatalog.SafetyActionTaken, new JsonObject {
                ["action"] = "auto_resume",
                ["runs_resumed"] = resumed,
                ["unparked"] = unparked,
            }).ConfigureAwait(false);
            await NotifyQuietlyAsync(NotificationSeverity.Warning, "Safe again — sequence resumed",
                "Conditions stayed safe through the configured delay, so the mount was unparked and the paused sequence resumed. "
                + "Verify the pointing: unless your sequence re-centers the target, frames after an unsafe pause may need the target re-slewed."
                + (unparked ? string.Empty : " NOTE: the mount did not confirm it unparked in time — check it before trusting new frames.")).ConfigureAwait(false);
        } catch (Exception ex) {
            LogResumeFailed(ex);
        } finally {
            lock (_lock) {
                if (ReferenceEquals(_resumeCountdown, cts)) {
                    _resumeCountdown = null;
                }
            }
            cts.Dispose();
        }
    }

    // ─── equipment rungs (each best-effort; a fault never blocks the rest) ───

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: pausing runs must proceed to guider-stop + park even if the sequencer faults; logged, never rethrown.")]
    private async Task<IReadOnlyList<Guid>> PauseActiveRunsAsync() {
        try {
            var sequencer = _sequencerResolver?.Invoke();
            if (sequencer is null) {
                return Array.Empty<Guid>();
            }
            return await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("pause", ex);
            return Array.Empty<Guid>();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: aborting runs must proceed to guider-stop + park even if the sequencer faults; logged, never rethrown.")]
    private async Task<int> AbortActiveRunsAsync() {
        try {
            var sequencer = _sequencerResolver?.Invoke();
            if (sequencer is null) {
                return 0;
            }
            return await sequencer.AbortActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("abort", ex);
            return 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a disconnected/faulting guider must not prevent the mount park; logged, never rethrown.")]
    private async Task<bool> StopGuidingQuietlyAsync() {
        try {
            if (_guider is null) {
                return false;
            }
            await _guider.StopGuidingAsync(null, CancellationToken.None).ConfigureAwait(false);
            return true;
        } catch (Exception ex) {
            LogRungFailed("stop_guiding", ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a disconnected mount (or one that can't park) must not abort the reaction; logged, never rethrown.")]
    private async Task<bool> ParkQuietlyAsync() {
        try {
            if (_telescope is null) {
                return false;
            }
            await _telescope.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None).ConfigureAwait(false);
            return true;
        } catch (Exception ex) {
            LogRungFailed("park", ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: an unpark fault degrades to 'resume anyway + warn' — the notification carries the caveat; logged, never rethrown.")]
    private async Task<bool> UnparkAndWaitQuietlyAsync(CancellationToken ct) {
        try {
            if (_telescope is null) {
                return false;
            }
            await _telescope.UnparkAsync(null, CancellationToken.None).ConfigureAwait(false);
            // Unpark is a 202 background op; resuming a run whose next instruction
            // slews a still-parked mount would fail it, so wait (bounded) for the
            // runtime to read back unparked, then restore tracking.
            var deadline = DateTimeOffset.UtcNow + UnparkTimeout;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested) {
                var dto = await _telescope.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is { State: EquipmentConnectionState.Connected, Runtime.Parked: false }) {
                    try {
                        await _telescope.SetTrackingAsync(true, CancellationToken.None).ConfigureAwait(false);
                    } catch (Exception ex) {
                        LogRungFailed("tracking_on", ex);
                    }
                    return true;
                }
                await Task.Delay(UnparkPollInterval, ct).ConfigureAwait(false);
            }
            return false;
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            LogRungFailed("unpark", ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a sequencer fault on resume leaves the run paused for the user; logged, never rethrown.")]
    private async Task<int> ResumeRunsAsync(IReadOnlyCollection<Guid> ids) {
        try {
            var sequencer = _sequencerResolver?.Invoke();
            if (sequencer is null) {
                return 0;
            }
            return await sequencer.ResumeRunsAsync(ids, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("resume", ex);
            return 0;
        }
    }

    // ─── plumbing ───

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort profile read: a store fault falls back to the default policy rather than skipping the reaction; logged via the rung path.")]
    private SafetyPoliciesDto? ReadPolicies() {
        try {
            return _profiles?.GetSafetyPolicies();
        } catch (Exception ex) {
            LogRungFailed("read_policy", ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never abort the safety reaction. Log-and-recover boundary.")]
    private async Task PublishAsync(string eventType, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            // ToJsonString()+Parse (not JsonSerializer.SerializeToElement) is the
            // AOT-safe JsonElement construction — matches GuiderService/SequencerService.
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogWsPublishFailed(eventType, ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the safety reaction itself (the §58.7 precedent). Log-and-recover boundary.")]
    private async Task NotifyQuietlyAsync(NotificationSeverity severity, string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: severity,
                Category: NotificationCategory.Safety,
                Title: title,
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("notify", ex);
        }
    }

    private void CancelResumeCountdownLocked() {
        try {
            _resumeCountdown?.Cancel();
        } catch (ObjectDisposedException) {
            // Countdown finished + disposed concurrently — already gone.
        }
    }

    // ─── lifecycle ───

    Task IHostedService.StartAsync(CancellationToken cancellationToken) {
        lock (_lock) {
            if (!_disposed && _safetyMonitor is not null) {
                _timer = new Timer(OnTick, null, PollInterval, PollInterval);
            }
        }
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            CancelResumeCountdownLocked();
        }
    }

    // ─── logging (source-gen per repo convention) ───

    [LoggerMessage(EventId = 3501, Level = LogLevel.Warning, Message = "§35 safety monitor read failed; treating state as unknown")]
    private partial void LogMonitorReadFailed(Exception exception);

    [LoggerMessage(EventId = 3502, Level = LogLevel.Error, Message = "§35 safety poll tick failed")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(EventId = 3503, Level = LogLevel.Warning, Message = "§35 unrecognized on_unsafe token '{Token}' — falling back to pause_and_park")]
    private partial void LogUnknownActionToken(string? token);

    [LoggerMessage(EventId = 3504, Level = LogLevel.Warning, Message = "§35 unsafe reaction executed: action={Action} pausedRuns={PausedRuns} abortedRuns={AbortedRuns} guidingStopped={GuidingStopped} parkRequested={ParkRequested}")]
    private partial void LogReacted(string action, int pausedRuns, int abortedRuns, bool guidingStopped, bool parkRequested);

    [LoggerMessage(EventId = 3505, Level = LogLevel.Information, Message = "§35 conditions safe — auto-resume armed for {DelayMinutes} min")]
    private partial void LogResumeArmed(int delayMinutes);

    [LoggerMessage(EventId = 3506, Level = LogLevel.Information, Message = "§35 auto-resume cancelled (conditions turned unsafe again or the daemon is stopping)")]
    private partial void LogResumeCancelled();

    [LoggerMessage(EventId = 3507, Level = LogLevel.Warning, Message = "§35 auto-resumed after safe delay: runsResumed={RunsResumed} unparkConfirmed={UnparkConfirmed}")]
    private partial void LogAutoResumed(int runsResumed, bool unparkConfirmed);

    [LoggerMessage(EventId = 3508, Level = LogLevel.Error, Message = "§35 auto-resume failed; the run stays paused for the user")]
    private partial void LogResumeFailed(Exception exception);

    [LoggerMessage(EventId = 3509, Level = LogLevel.Warning, Message = "§35 reaction rung '{Rung}' failed (best-effort; continuing)")]
    private partial void LogRungFailed(string rung, Exception exception);

    [LoggerMessage(EventId = 3510, Level = LogLevel.Warning, Message = "§35 WS publish of {EventType} failed (best-effort)")]
    private partial void LogWsPublishFailed(string eventType, Exception exception);
}
