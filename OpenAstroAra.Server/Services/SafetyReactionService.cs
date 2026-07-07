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
using System.Globalization;
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
    private readonly IObservingConditionsService? _weather;
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
    // Set when an unsafe reaction executes; cleared only by a genuine safe
    // reading. A transient monitor read fault resets _lastSafe to unknown, and
    // without this latch the next unsafe reading (unknown→unsafe) would re-fire
    // the whole reaction — pause/park commands + a fresh Critical notification —
    // on every read hiccup of a persistently-unsafe flaky driver (#731 round-2).
    private bool _unsafeReactionLatched;
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
            IObservingConditionsService? weather = null,
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            IGuiderService? guider = null,
            ITelescopeService? telescope = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            ILogger<SafetyReactionService>? logger = null) {
        _safetyMonitor = safetyMonitor;
        _weather = weather;
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
        if (_safetyMonitor is null && _weather is null) {
            return;
        }
        try {
            SafetyMonitorDto? dto = null;
            if (_safetyMonitor is not null) {
                try {
                    dto = await _safetyMonitor.GetAsync(CancellationToken.None).ConfigureAwait(false);
                } catch (Exception ex) {
                    LogMonitorReadFailed(ex);
                    dto = null;
                }
            }
            bool? monitorSafe = dto is { State: EquipmentConnectionState.Connected } ? dto.Safe : null;

            // §35.1 — granular weather thresholds over the ObservingConditions
            // device. A breach makes conditions UNSAFE through the same
            // transition classifier + reaction + auto-resume machinery as the
            // SafetyMonitor: thresholds decide WHEN, on_unsafe decides WHAT.
            bool? weatherSafe = null;
            IReadOnlyList<string> breaches = Array.Empty<string>();
            var tickPolicy = ReadPolicies();
            if (tickPolicy is { WeatherTriggersEnabled: true } && _weather is not null) {
                ObservingConditionsDto? weather = null;
                try {
                    weather = await _weather.GetAsync(CancellationToken.None).ConfigureAwait(false);
                } catch (Exception ex) {
                    LogWeatherReadFailed(ex);
                }
                if (weather is { State: EquipmentConnectionState.Connected }) {
                    breaches = EvaluateWeatherBreaches(weather, tickPolicy);
                    weatherSafe = breaches.Count == 0;
                }
            }

            // Combined verdict: unknown only when NO source has a reading;
            // otherwise any unsafe source wins (auto-resume therefore requires
            // monitor AND weather to read safe, which is the conservative side).
            bool? nowSafe = (monitorSafe, weatherSafe) switch {
                (null, null) => null,
                _ => (monitorSafe ?? true) && (weatherSafe ?? true),
            };

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
                if (transition == Transition.BecameSafe) {
                    _unsafeReactionLatched = false;
                }
                if (transition == Transition.BecameUnsafe) {
                    if (_reacting) {
                        // A reaction is still executing (e.g. a slow park from a
                        // fast safe→unsafe→safe→unsafe flap) — don't stack another.
                        transition = Transition.None;
                    } else if (_unsafeReactionLatched) {
                        // unknown→unsafe after a read hiccup, with no genuine safe
                        // reading in between — the reaction already ran; don't
                        // re-park/re-notify per flake (#731 round-2 debounce).
                        transition = Transition.None;
                        LogUnsafeDebounced();
                    } else {
                        _reacting = true;
                        _unsafeReactionLatched = true;
                    }
                }
            }

            switch (transition) {
                case Transition.BecameUnsafe:
                    try {
                        await ReactToUnsafeAsync(dto, breaches, monitorSafe == false).ConfigureAwait(false);
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

    /// <summary>
    /// §35.1 threshold evaluation — pure so tests drive it directly. A sensor
    /// the device doesn't report (null) skips its check: no data is not a
    /// breach. Wind uses the worse of sustained speed and gust.
    /// </summary>
    internal static IReadOnlyList<string> EvaluateWeatherBreaches(ObservingConditionsDto weather, SafetyPoliciesDto policy) {
        var breaches = new List<string>();
        double? windMs = (weather.WindSpeedMs, weather.WindGustMs) switch {
            (double s, double g) => Math.Max(s, g),
            (double s, null) => s,
            (null, double g) => g,
            _ => null,
        };
        if (windMs is double w && w * 3.6 > policy.MaxWindKmh) {
            breaches.Add(string.Create(CultureInfo.InvariantCulture,
                $"wind {w * 3.6:F0} km/h over the {policy.MaxWindKmh} km/h limit"));
        }
        if (weather.HumidityPct is double h && h > policy.MaxHumidityPct) {
            breaches.Add(string.Create(CultureInfo.InvariantCulture,
                $"humidity {h:F0}% over the {policy.MaxHumidityPct}% limit"));
        }
        if (weather.TemperatureC is double t && weather.DewPointC is double d && t - d < policy.MinDewDeltaC) {
            breaches.Add(string.Create(CultureInfo.InvariantCulture,
                $"dew delta {t - d:F1}\u00b0C under the {policy.MinDewDeltaC:F1}\u00b0C minimum"));
        }
        return breaches;
    }

    // breaches/monitorUnsafe ride in as the FIRING tick's own locals — an
    // overlapping slow tick must not be able to re-attribute the reasons
    // (round-2 review race).
    private async Task ReactToUnsafeAsync(SafetyMonitorDto? monitor, IReadOnlyList<string> breaches, bool monitorUnsafe) {
        var policy = ReadPolicies();
        var action = ParseAction(policy?.OnUnsafe);
        if (policy is not null && ParseActionIsFallback(policy.OnUnsafe)) {
            LogUnknownActionToken(policy.OnUnsafe);
        }

        var reasons = new List<string>();
        if (monitorUnsafe) { reasons.Add("safety monitor reports unsafe"); }
        reasons.AddRange(breaches);

        // §35.4: the unsafe event fires BEFORE the action so the client can alarm
        // while the daemon reacts.
        var reasonsJson = new JsonArray();
        foreach (var reason in reasons) { reasonsJson.Add(reason); }
        await PublishAsync(WsEventCatalog.SafetyUnsafe, new JsonObject {
            ["device_name"] = monitor?.Name,
            ["action"] = ActionToken(action),
            ["reasons"] = reasonsJson,
        }).ConfigureAwait(false);

        var pausedRuns = 0;
        var abortedRuns = 0;
        if (action is UnsafeAction.PauseAndPark) {
            var ids = await PauseActiveRunsAsync().ConfigureAwait(false);
            pausedRuns = ids.Count;
            lock (_lock) {
                // UNION, never clear: on a second unsafe reaction (unsafe → safe,
                // resume armed → unsafe again) the still-paused run is excluded
                // from the fresh ids (the bulk pause skips already-paused runs),
                // so clearing here would orphan it from auto-resume tracking
                // forever (#731 review).
                foreach (var id in ids) {
                    if (!_pausedRunIds.Contains(id)) {
                        _pausedRunIds.Add(id);
                    }
                }
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

        // §35.1: when weather thresholds tripped the verdict, say WHICH ones —
        // "wind 41 km/h over the 36 km/h limit" beats a bare "unsafe".
        var deviceName = reasons.Count > 0 && !monitorUnsafe
            ? "The weather station (" + string.Join("; ", breaches) + ")"
            : string.IsNullOrWhiteSpace(monitor?.Name) ? "The safety monitor" : monitor!.Name;
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
        if (monitorUnsafe && breaches.Count > 0) {
            // Combined breach: the monitor drove the deviceName, so the weather
            // reasons must still reach the operator — never a bare "unsafe".
            message += " Weather thresholds also breached: " + string.Join("; ", breaches) + ".";
        }
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

            // Drop ids that are no longer resumable BEFORE touching the mount: a
            // tracked run the user aborted/stopped during the countdown (or one a
            // later abort_and_park reaction took down) must not trigger an unpark
            // + tracking-on + "resumed" notification with nothing to resume
            // (#731 round-3). Terminal/unknown ids are dropped permanently; a
            // transient state-read fault keeps the id (the release path is
            // guarded and honest, and a hiccup must not strand a resumable run).
            ids = await FilterStillPausedAsync(ids).ConfigureAwait(false);
            if (ids.Count == 0) {
                LogResumeNothingLeft();
                return;
            }

            var unparked = await UnparkAndWaitQuietlyAsync(cts.Token).ConfigureAwait(false);

            // Re-check AFTER the (up to 90 s) unpark wait: a relapse to unsafe in
            // that window cancels cts / flips _lastSafe, and resuming then would
            // release the run into unsafe conditions — the exact failure this
            // feature exists to prevent (#731 review). On abort: restore the ids
            // to tracking (union — the relapse reaction's own bulk pause skipped
            // these already-paused runs) and counter our own unpark with a fresh
            // best-effort park; the run stays paused for the next safe window.
            bool proceed;
            lock (_lock) {
                proceed = !_disposed && !cts.IsCancellationRequested && _lastSafe == true;
                if (!proceed) {
                    foreach (var id in ids) {
                        if (!_pausedRunIds.Contains(id)) {
                            _pausedRunIds.Add(id);
                        }
                    }
                }
            }
            if (!proceed) {
                await ParkQuietlyAsync().ConfigureAwait(false);
                LogResumeCancelled();
                return;
            }

            var resumed = await ResumeRunsAsync(ids).ConfigureAwait(false);
            LogAutoResumed(resumed, unparked);

            await PublishAsync(WsEventCatalog.SafetyActionTaken, new JsonObject {
                ["action"] = "auto_resume",
                ["runs_resumed"] = resumed,
                ["unparked"] = unparked,
            }).ConfigureAwait(false);
            if (resumed > 0) {
                await NotifyQuietlyAsync(NotificationSeverity.Warning, "Safe again — sequence resumed",
                    "Conditions stayed safe through the configured delay, so the mount was unparked and the paused sequence resumed. "
                    + "Verify the pointing: unless your sequence re-centers the target, frames after an unsafe pause may need the target re-slewed."
                    + (unparked ? string.Empty : " NOTE: the mount did not confirm it unparked in time — check it before trusting new frames.")).ConfigureAwait(false);
            } else {
                // The run ended in the tiny window between the liveness filter and
                // the release — the mount is unparked but nothing resumed; say so
                // honestly instead of claiming a resume.
                await NotifyQuietlyAsync(NotificationSeverity.Warning, "Safe again — nothing left to resume",
                    "Conditions cleared and the mount was unparked, but the paused sequence had already ended, so nothing was resumed.").ConfigureAwait(false);
            }
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
            // Fresh gate immediately before commanding the unpark: a relapse to
            // unsafe between the countdown's check and this call must not move
            // the mount at all (#731 round-2) — the post-wait re-check then only
            // covers a relapse landing DURING the wait.
            lock (_lock) {
                if (_disposed || ct.IsCancellationRequested || _lastSafe != true) {
                    return false;
                }
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
        Justification = "Best-effort filter: a transient run-state read fault keeps the id (the release path is guarded); logged, never rethrown.")]
    private async Task<List<Guid>> FilterStillPausedAsync(List<Guid> ids) {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return ids;
        }
        var resumable = new List<Guid>(ids.Count);
        foreach (var id in ids) {
            try {
                var state = await sequencer.GetRunStateAsync(id, CancellationToken.None).ConfigureAwait(false);
                if (state is { State: SequenceRunState.Paused }) {
                    resumable.Add(id);
                } else {
                    LogStaleRunDropped(id, state?.State);
                }
            } catch (Exception ex) {
                LogRungFailed("filter_paused", ex);
                resumable.Add(id);
            }
        }
        return resumable;
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
            // Weather-only operation is first-class (§35.1): the timer runs
            // when EITHER source exists, mirroring TickAsync's own bail-out.
            if (!_disposed && (_safetyMonitor is not null || _weather is not null)) {
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

    [LoggerMessage(EventId = 3511, Level = LogLevel.Debug, Message = "§35 unsafe reading after a monitor read hiccup — reaction already latched, not re-firing")]
    private partial void LogUnsafeDebounced();

    [LoggerMessage(EventId = 3512, Level = LogLevel.Information, Message = "§35 auto-resume: tracked run {RunId} is no longer paused (state {State}) — dropped from resume tracking")]
    private partial void LogStaleRunDropped(Guid runId, SequenceRunState? state);

    [LoggerMessage(EventId = 3514, Level = LogLevel.Warning, Message = "§35.1 weather read failed; weather thresholds treated as unknown this tick")]
    private partial void LogWeatherReadFailed(Exception exception);

    [LoggerMessage(EventId = 3513, Level = LogLevel.Information, Message = "§35 auto-resume: no tracked run is still paused — nothing to resume, mount stays parked")]
    private partial void LogResumeNothingLeft();
}
