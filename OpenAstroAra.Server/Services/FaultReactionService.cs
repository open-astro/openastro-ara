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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.3 — executes the <see cref="FaultPolicyMatrix"/> plan for each detected equipment fault:
/// pause a running sequence immediately when a sequence-critical device (camera/mount) drops,
/// hot-reconnect on the 0/5/15/30/60 s ladder via <see cref="IEquipmentReconnector"/>, resume
/// the paused runs on recovery, and fall to the plan's terminal action (stay paused / abort +
/// best-effort park) when every attempt fails. One episode per device type at a time; each step
/// is best-effort (SafetyReactionService's rung pattern) and every action is observable via the
/// <c>equipment.fault_action_taken</c> WS event + a notification. The guider is never handled
/// here — GuiderService owns its own §42.2 reaction + §63.3 recovery.
/// </summary>
public sealed partial class FaultReactionService : IHostedService, IDisposable {

    private readonly EquipmentFaultHub? _hub;
    private readonly IEquipmentReconnector? _reconnector;
    private readonly IProfileStore? _profiles;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly ITelescopeService? _telescope;
    private readonly INotificationService? _notifications;
    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<FaultReactionService> _logger;
    private readonly IFaultLogService? _faultLog;

    private readonly object _gate = new();
    // Keyed by normalized DeviceType for recovery-running kinds (their ladder must not stack);
    // notify-only kinds get a unique key so they always run — see OnFault.
    private readonly Dictionary<object, Task> _episodes = [];
    // §42.2 items 2+5 — persistence counter for op-channel faults (mutated under _gate).
    private readonly OpFaultEscalator _escalator = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Test seams — compress the §42.3 ladder and the connect-confirm polling so unit tests
    // run in milliseconds (the SafetyReactionService knob pattern).
    internal Func<TimeSpan, TimeSpan> DelayShaper { get; set; } = static d => d;
    internal TimeSpan ConnectConfirmTimeout { get; set; } = TimeSpan.FromSeconds(12);
    internal TimeSpan ConnectPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    // §42.3 linger phase — slow-cadence post-give-up re-adoption (~an hour at the defaults).
    internal TimeSpan LingerInterval { get; set; } = TimeSpan.FromSeconds(120);
    internal int LingerMaxAttempts { get; set; } = 30;

    public FaultReactionService(
            EquipmentFaultHub? hub = null,
            IEquipmentReconnector? reconnector = null,
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            ITelescopeService? telescope = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            ILogger<FaultReactionService>? logger = null,
            IFaultLogService? faultLog = null) {
        _hub = hub;
        _reconnector = reconnector;
        _profiles = profiles;
        _sequencerResolver = sequencerResolver;
        _telescope = telescope;
        _notifications = notifications;
        _ws = ws;
        _logger = logger ?? NullLogger<FaultReactionService>.Instance;
        _faultLog = faultLog;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _hub?.Subscribe(OnFault);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _cts.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_gate) {
            running = [.. _episodes.Values];
        }
        if (running.Length > 0) {
            // Episodes observe the cancel at their next delay/poll; give them a moment to
            // unwind, but never hold up daemon shutdown.
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Hub callback — invoked synchronously from a device refresh tick, so it only
    /// hands off: one RECOVERY episode per device type at a time (detection publishes once per
    /// episode; a device that recovers and drops again starts a fresh one). Notify-only kinds
    /// (§42.4 op/state-channel faults) never hold the slot and never dedupe — a switch port
    /// mismatch must not lose its one notification just because the same device type happens
    /// to be mid-reconnect (review finding).</summary>
    internal void OnFault(EquipmentFaultEvent fault) {
        lock (_gate) {
            if (_disposed || _cts.IsCancellationRequested) {
                return;
            }
            var recoverable = fault.Kind is EquipmentFaultKind.Disconnected or EquipmentFaultKind.TrackingLost;
            var key = recoverable ? (object)NormalizeType(fault.DeviceType) : new object();
            if (recoverable && _episodes.ContainsKey(key)) {
                LogEpisodeAlreadyActive(fault.DeviceType, fault.Kind);
                return;
            }
            // §42.2 items 2+5 — non-recoverable kinds also feed the persistence counter: the
            // fault that tips its device over the threshold escalates (its episode runs the
            // matrix's escalation plan instead of the plain notify).
            var escalated = !recoverable
                && _escalator.Observe(NormalizeType(fault.DeviceType), fault.Kind, DateTimeOffset.UtcNow);
            _episodes[key] = Task.Run(() => RunEpisodeAsync(key, fault, escalated), CancellationToken.None);
        }
    }

    /// <summary>Completion of every in-flight episode (tests await this after OnFault).</summary>
    internal Task WhenIdleAsync() {
        Task[] running;
        lock (_gate) {
            running = [.. _episodes.Values];
        }
        return Task.WhenAll(running);
    }

    // FlatDevice/CoverCalibrator are one physical device under two tokens — one episode
    // covers both (shared collapse: DeviceTypeExtensions.Canonical).
    private static DeviceType NormalizeType(DeviceType t) => t.Canonical();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Episode boundary: a reaction episode runs on a fire-and-forget task; any escape must be logged and contained, never surface as an unobserved task exception. CA1031's log-and-recover boundary applies.")]
    private async Task RunEpisodeAsync(object key, EquipmentFaultEvent fault, bool escalated = false) {
        try {
            var plan = FaultPolicyMatrix.Resolve(fault.DeviceType, fault.Kind, ReadPolicies(), escalated);
            if (plan is null) {
                return; // the guider's — GuiderService.FaultReaction owns it
            }
            var label = fault.DeviceName ?? fault.DeviceId ?? fault.DeviceType.ToString();
            LogEpisodeStarted(fault.DeviceType, fault.Kind, label, plan.RetrySchedule.Count, plan.TerminalAction);

            if (plan.Escalated) {
                await ExecuteEscalationAsync(fault, plan, label).ConfigureAwait(false);
                return;
            }

            if (plan.RetrySchedule.Count == 0 && plan.TerminalAction == FaultTerminalAction.None) {
                await PublishActionAsync(fault, "notify_only").ConfigureAwait(false);
                await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{fault.DeviceType} fault: {EquipmentFaultHub.WireToken(fault.Kind)}",
                    $"'{label}' reported {EquipmentFaultHub.WireToken(fault.Kind)}{Detail(fault)}.").ConfigureAwait(false);
                return;
            }

            // Pause FIRST (§42.3 flagged decision): an instruction-boundary pause is graceful —
            // the in-flight instruction completes — and a sequence must not keep exposing into a
            // dead camera or slewing an unwatched mount while the ladder runs.
            IReadOnlyList<Guid> pausedRuns = plan.PauseWhileRecovering
                ? await PauseActiveRunsAsync().ConfigureAwait(false)
                : [];
            if (pausedRuns.Count > 0) {
                await PublishActionAsync(fault, "sequence_paused", extra: p => p["paused_runs"] = pausedRuns.Count)
                    .ConfigureAwait(false);
            }

            if (plan.RetrySchedule.Count > 0) {
                await PublishActionAsync(fault, "reconnecting", extra: p => p["max_attempts"] = plan.RetrySchedule.Count)
                    .ConfigureAwait(false);
                await NotifyQuietlyAsync(NotificationSeverity.Warning, $"{fault.DeviceType} lost — recovering",
                    $"'{label}' {EquipmentFaultHub.WireToken(fault.Kind)}{Detail(fault)}; attempting recovery "
                    + $"(up to {plan.RetrySchedule.Count} tries{(pausedRuns.Count > 0 ? ", sequence paused" : "")}).")
                    .ConfigureAwait(false);
                for (var attempt = 1; attempt <= plan.RetrySchedule.Count; attempt++) {
                    await DelayAsync(plan.RetrySchedule[attempt - 1]).ConfigureAwait(false);
                    if (_cts.IsCancellationRequested) {
                        return;
                    }
                    var recovered = fault.Kind == EquipmentFaultKind.TrackingLost
                        ? await TryReenableTrackingAsync().ConfigureAwait(false)
                        : await TryReconnectAsync(fault.DeviceType).ConfigureAwait(false);
                    if (recovered) {
                        var resumed = pausedRuns.Count > 0 ? await ResumeRunsAsync(pausedRuns).ConfigureAwait(false) : 0;
                        var n = attempt;
                        await PublishActionAsync(fault, "recovered", extra: p => {
                            p["attempts"] = n;
                            p["resumed_runs"] = resumed;
                        }).ConfigureAwait(false);
                        await NotifyQuietlyAsync(NotificationSeverity.Info, $"{fault.DeviceType} recovered",
                            $"'{label}' recovered on attempt {attempt}"
                            + $"{(resumed > 0 ? $"; {resumed} paused sequence run(s) resumed" : "")}.").ConfigureAwait(false);
                        LogRecovered(fault.DeviceType, attempt);
                        return;
                    }
                }
            }

            var stillPaused = await ExecuteTerminalAsync(fault, plan, label, pausedRuns).ConfigureAwait(false);

            // §42.3 — the ladder is ~2 min end-to-end, but real outages (a USB hub off for ten
            // minutes, an AlpacaBridge host rebooting) outlive it. After the terminal action a
            // DISCONNECT episode lingers at a slow cadence and re-adopts the device if it comes
            // back — no user surgery. The episode keeps holding its device-type slot so a
            // re-fire can't stack a second ladder on top of the linger.
            if (fault.Kind == EquipmentFaultKind.Disconnected) {
                await LingerForReadoptionAsync(fault, label, stillPaused).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // daemon shutting down — the episode just stops
        } catch (Exception ex) {
            LogEpisodeFailed(fault.DeviceType, ex);
        } finally {
            lock (_gate) {
                _episodes.Remove(key);
            }
        }
    }

    // §42.2 items 2+5 — a persistent-op-fault escalation is not a failed recovery: the device is
    // still connected, it just keeps failing its ops, so the terminal action runs immediately and
    // the record/notification say "escalated", not "did not recover after N attempts".
    private async Task ExecuteEscalationAsync(EquipmentFaultEvent fault, FaultPlan plan, string label) {
        var terminal = plan.TerminalAction == FaultTerminalAction.AbortAndPark ? "abort_and_park" : "pause_sequence";
        var paused = 0;
        var aborted = 0;
        var parked = false;
        if (plan.TerminalAction == FaultTerminalAction.AbortAndPark) {
            aborted = await AbortActiveRunsAsync().ConfigureAwait(false);
            // Unlike the give-up park, the mount is still answering here (its faults are op
            // failures, not a disconnect) — but a mount that keeps failing slews may fail the
            // park too, so it stays best-effort and the outcome is reported honestly.
            parked = await ParkQuietlyAsync().ConfigureAwait(false);
        } else {
            paused = (await PauseActiveRunsAsync().ConfigureAwait(false)).Count;
        }
        await PublishActionAsync(fault, "escalated", persistAs: $"escalated:{terminal}", extra: p => {
            p["terminal"] = terminal;
            p["threshold"] = OpFaultEscalator.DefaultThreshold;
            if (aborted > 0) {
                p["aborted_runs"] = aborted;
            }
            if (paused > 0) {
                p["paused_runs"] = paused;
            }
            if (plan.TerminalAction == FaultTerminalAction.AbortAndPark) {
                p["parked"] = parked;
            }
        }).ConfigureAwait(false);
        var outcome = plan.TerminalAction == FaultTerminalAction.AbortAndPark
            ? $"; {aborted} run(s) aborted, park {(parked ? "dispatched" : "failed")}"
            : $"; sequence paused ({paused} run(s))";
        await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{fault.DeviceType}: persistent op faults",
            $"'{label}' has failed {OpFaultEscalator.DefaultThreshold} operations within "
            + $"{OpFaultEscalator.DefaultWindow.TotalMinutes:0} min (latest: {EquipmentFaultHub.WireToken(fault.Kind)}{Detail(fault)})"
            + $"{outcome}.").ConfigureAwait(false);
        LogEscalated(fault.DeviceType, terminal);
    }

    // Returns the runs left paused by the terminal action (empty for abort/none), so the §42.3
    // linger phase can resume exactly those on a late re-adoption.
    private async Task<IReadOnlyList<Guid>> ExecuteTerminalAsync(EquipmentFaultEvent fault, FaultPlan plan, string label,
            IReadOnlyList<Guid> pausedRuns) {
        var terminal = plan.TerminalAction switch {
            FaultTerminalAction.PauseSequence => "pause_sequence",
            FaultTerminalAction.AbortAndPark => "abort_and_park",
            _ => "none",
        };
        IReadOnlyList<Guid> stillPaused = [];
        var pausedNow = 0;
        var aborted = 0;
        var parked = false;
        switch (plan.TerminalAction) {
            case FaultTerminalAction.PauseSequence:
                // Usually already paused up-front; this covers plans that skipped the
                // pause (policy "pause" with no retries) or a run started mid-episode.
                stillPaused = pausedRuns.Count > 0 ? pausedRuns : await PauseActiveRunsAsync().ConfigureAwait(false);
                pausedNow = stillPaused.Count;
                break;
            case FaultTerminalAction.AbortAndPark:
                aborted = await AbortActiveRunsAsync().ConfigureAwait(false);
                // Best-effort: a mount that never reconnected usually can't park either, but
                // trying costs nothing and covers driver-side drops where transport survives.
                parked = await ParkQuietlyAsync().ConfigureAwait(false);
                break;
            default:
                break;
        }
        // The stored action carries the terminal outcome inline (gave_up:abort_and_park)
        // — the DB row has no extras object like the WS payload does.
        await PublishActionAsync(fault, "gave_up", persistAs: $"gave_up:{terminal}", extra: p => {
            p["terminal"] = terminal;
            p["attempts"] = plan.RetrySchedule.Count;
            if (aborted > 0) {
                p["aborted_runs"] = aborted;
            }
            if (pausedNow > 0) {
                p["paused_runs"] = pausedNow;
            }
            if (plan.TerminalAction == FaultTerminalAction.AbortAndPark) {
                p["parked"] = parked;
            }
        }).ConfigureAwait(false);
        var outcome = plan.TerminalAction switch {
            FaultTerminalAction.PauseSequence when pausedNow > 0 => $"; sequence paused ({pausedNow} run(s))",
            FaultTerminalAction.AbortAndPark => $"; {aborted} run(s) aborted, park {(parked ? "dispatched" : "failed")}",
            _ => "",
        };
        await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{fault.DeviceType} not recovered",
            $"'{label}' did not recover"
            + $"{(plan.RetrySchedule.Count > 0 ? $" after {plan.RetrySchedule.Count} attempts" : "")}{outcome}.")
            .ConfigureAwait(false);
        LogGaveUp(fault.DeviceType, plan.RetrySchedule.Count, terminal);
        return stillPaused;
    }

    // §42.3 — the post-give-up linger: poll the device type's state at LingerInterval for up to
    // LingerMaxAttempts (~an hour at the defaults). While the service reports Error the remembered
    // device is re-dispatched through the same TryReconnectAsync the ladder uses; the moment the
    // type reads Connected — whether the linger's own dispatch landed or the user re-seated a
    // cable and the daemon auto-connected — the device is re-adopted: runs the terminal pause left
    // paused are resumed, and the recovery is published + notified. A Disconnected state stops the
    // linger silently: that is the shape of a DELIBERATE user disconnect (the services only park
    // in Error on a lost device), and the daemon must not fight the user for the device.
    private async Task LingerForReadoptionAsync(EquipmentFaultEvent fault, string label,
            IReadOnlyList<Guid> pausedRuns) {
        for (var attempt = 1; attempt <= LingerMaxAttempts; attempt++) {
            await DelayAsync(LingerInterval).ConfigureAwait(false);
            if (_cts.IsCancellationRequested) {
                return;
            }
            var state = await GetConnectionStateQuietlyAsync(fault.DeviceType).ConfigureAwait(false);
            switch (state) {
                case EquipmentConnectionState.Connected:
                    break; // re-adopted (by us or by the user) — fall through to the celebration
                case EquipmentConnectionState.Connecting:
                    continue; // someone's connect is in flight — just wait for the verdict
                case EquipmentConnectionState.Error:
                    if (!await TryReconnectAsync(fault.DeviceType).ConfigureAwait(false)) {
                        continue;
                    }
                    break; // the linger's own dispatch landed
                default:
                    // Disconnected (a deliberate user disconnect) or null (nothing remembered /
                    // no service) — the device is no longer ours to chase.
                    LogLingerStopped(fault.DeviceType, state);
                    return;
            }
            var resumed = pausedRuns.Count > 0 ? await ResumeRunsAsync(pausedRuns).ConfigureAwait(false) : 0;
            var n = attempt;
            await PublishActionAsync(fault, "readopted", extra: p => {
                p["linger_attempts"] = n;
                p["resumed_runs"] = resumed;
            }).ConfigureAwait(false);
            await NotifyQuietlyAsync(NotificationSeverity.Info, $"{fault.DeviceType} re-adopted",
                $"'{label}' came back after the recovery ladder had given up and was reconnected"
                + $"{(resumed > 0 ? $"; {resumed} paused sequence run(s) resumed" : "")}.").ConfigureAwait(false);
            LogReadopted(fault.DeviceType, attempt);
            return;
        }
        LogLingerExhausted(fault.DeviceType, LingerMaxAttempts);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort linger poll: a state read fault counts as 'still gone' and the linger moves on; it must never abort the episode. CA1031's log-and-recover boundary applies.")]
    private async Task<EquipmentConnectionState?> GetConnectionStateQuietlyAsync(DeviceType type) {
        if (_reconnector is null) {
            return null;
        }
        try {
            return await _reconnector.GetConnectionStateAsync(type, _cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogAttemptFailed("linger_state_poll", type, ex);
            return EquipmentConnectionState.Error;
        }
    }

    // ─── recovery attempts ───

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort recovery attempt: any reconnect/poll fault counts as a failed attempt and the ladder moves on; it must never abort the episode. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> TryReconnectAsync(DeviceType type) {
        if (_reconnector is null) {
            return false;
        }
        try {
            var outcome = await _reconnector.ReconnectAsync(type, _cts.Token).ConfigureAwait(false);
            if (outcome.Dispatched == 0) {
                return false; // nothing remembered, or every dispatch failed on the spot
            }
            // The dispatch connects in the background — poll the service state to a verdict.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < ConnectConfirmTimeout) {
                var state = await _reconnector.GetConnectionStateAsync(type, _cts.Token).ConfigureAwait(false);
                if (state == EquipmentConnectionState.Connected) {
                    return true;
                }
                if (state == EquipmentConnectionState.Error) {
                    return false; // the background connect already failed — next rung
                }
                await Task.Delay(ConnectPollInterval, _cts.Token).ConfigureAwait(false);
            }
            return false;
        } catch (OperationCanceledException) {
            throw; // unwind the episode on shutdown
        } catch (Exception ex) {
            LogAttemptFailed("reconnect", type, ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort recovery attempt: a SetTracking/read fault counts as a failed attempt; it must never abort the episode. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> TryReenableTrackingAsync() {
        if (_telescope is null) {
            return false;
        }
        try {
            await _telescope.SetTrackingAsync(true, _cts.Token).ConfigureAwait(false);
            var dto = await _telescope.GetAsync(_cts.Token).ConfigureAwait(false);
            return dto?.State == EquipmentConnectionState.Connected && dto.Runtime.Tracking;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogAttemptFailed("reenable_tracking", DeviceType.Telescope, ex);
            return false;
        }
    }

    private async Task DelayAsync(TimeSpan delay) {
        var shaped = DelayShaper(delay);
        if (shaped > TimeSpan.Zero) {
            await Task.Delay(shaped, _cts.Token).ConfigureAwait(false);
        }
    }

    // ─── terminal rungs (SafetyReactionService's best-effort pattern) ───

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a sequencer fault must not stop the remaining reaction steps. CA1031's log-and-recover boundary applies.")]
    private async Task<IReadOnlyList<Guid>> PauseActiveRunsAsync() {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return [];
        }
        try {
            return await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("pause", ex);
            return [];
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a sequencer fault must not stop the remaining reaction steps. CA1031's log-and-recover boundary applies.")]
    private async Task<int> ResumeRunsAsync(IReadOnlyList<Guid> ids) {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return 0;
        }
        try {
            return await sequencer.ResumeRunsAsync(ids.ToArray(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("resume", ex);
            return 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a sequencer fault must not stop the remaining reaction steps. CA1031's log-and-recover boundary applies.")]
    private async Task<int> AbortActiveRunsAsync() {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return 0;
        }
        try {
            return await sequencer.AbortActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("abort", ex);
            return 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: the give-up park frequently fails (the mount is the lost device); the failure is logged and reported in the WS payload, never thrown. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> ParkQuietlyAsync() {
        if (_telescope is null) {
            return false;
        }
        try {
            await _telescope.ParkAsync(new ParkRequestDto("equipment.fault"), null, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        } catch (Exception ex) {
            LogRungFailed("park", ex);
            return false;
        }
    }

    // ─── plumbing ───

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort profile read: a store fault falls back to the default policy rather than skipping the reaction. CA1031's log-and-recover boundary applies.")]
    private SafetyPoliciesDto? ReadPolicies() {
        try {
            return _profiles?.GetSafetyPolicies();
        } catch (Exception ex) {
            LogRungFailed("read_policy", ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never abort the reaction. CA1031's log-and-recover boundary applies.")]
    private async Task PublishActionAsync(EquipmentFaultEvent fault, string action, Action<JsonObject>? extra = null,
            string? persistAs = null) {
        // §42.5 — every reaction outcome is stamped onto the fault's history row
        // (last write wins across the episode); 'recovered' also resolves it.
        await RecordActionQuietlyAsync(fault, persistAs ?? action).ConfigureAwait(false);
        if (_ws is null) {
            return;
        }
        try {
            var payload = new JsonObject {
                ["device_type"] = fault.DeviceType.ToString().ToLowerInvariant(),
                ["device_id"] = fault.DeviceId,
                ["device_name"] = fault.DeviceName,
                ["kind"] = EquipmentFaultHub.WireToken(fault.Kind),
                ["action"] = action,
            };
            extra?.Invoke(payload);
            // ToJsonString()+Parse (not JsonSerializer.SerializeToElement) is the AOT-safe
            // JsonElement construction — matches EquipmentFaultHub/SafetyReactionService.
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.EquipmentFaultActionTaken, doc.RootElement.Clone(), CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("ws_publish", ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "§42.5 fault-log persistence is best-effort; a store fault must never abort the reaction. CA1031's log-and-recover boundary applies.")]
    private async Task RecordActionQuietlyAsync(EquipmentFaultEvent fault, string action) {
        if (_faultLog is null) {
            return;
        }
        try {
            var resolvedUtc = action == "recovered" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            await _faultLog.RecordActionAsync(fault, action, resolvedUtc, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("fault_log", ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask the reaction itself. CA1031's log-and-recover boundary applies.")]
    private async Task NotifyQuietlyAsync(NotificationSeverity severity, string title, string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: severity,
                Category: NotificationCategory.Equipment,
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

    private static string Detail(EquipmentFaultEvent fault) =>
        string.IsNullOrWhiteSpace(fault.Details) ? "" : $" ({fault.Details})";

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
        }
        _cts.Cancel();
        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: {DeviceType} {Kind} on '{Device}' — plan has {Attempts} recovery attempt(s), terminal {Terminal} (§42.3)")]
    private partial void LogEpisodeStarted(DeviceType deviceType, EquipmentFaultKind kind, string device, int attempts, FaultTerminalAction terminal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fault reaction: episode already active for {DeviceType} — {Kind} folded into it")]
    private partial void LogEpisodeAlreadyActive(DeviceType deviceType, EquipmentFaultKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: {DeviceType} recovered on attempt {Attempt} (§42.3)")]
    private partial void LogRecovered(DeviceType deviceType, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: {DeviceType} not recovered after {Attempts} attempt(s) — terminal action '{Terminal}' executed (§42.3)")]
    private partial void LogGaveUp(DeviceType deviceType, int attempts, string terminal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: persistent op faults on {DeviceType} — escalated to '{Terminal}' (§42.2)")]
    private partial void LogEscalated(DeviceType deviceType, string terminal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: {DeviceType} re-adopted on linger attempt {Attempt} (§42.3)")]
    private partial void LogReadopted(DeviceType deviceType, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: linger for {DeviceType} stopped — state {State} means the device is no longer ours to chase (§42.3)")]
    private partial void LogLingerStopped(DeviceType deviceType, EquipmentConnectionState? state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: linger for {DeviceType} exhausted after {Attempts} attempts — a manual reconnect will still be resolved by the §42.5 reconnect hook")]
    private partial void LogLingerExhausted(DeviceType deviceType, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: {Stage} attempt for {DeviceType} failed; the ladder moves on")]
    private partial void LogAttemptFailed(string stage, DeviceType deviceType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: best-effort rung '{Rung}' failed; remaining steps still run")]
    private partial void LogRungFailed(string rung, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fault reaction: episode for {DeviceType} failed unexpectedly")]
    private partial void LogEpisodeFailed(DeviceType deviceType, Exception ex);
}
