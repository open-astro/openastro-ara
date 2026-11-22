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
using OpenAstroAra.Server.Contracts.Refusals;
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

/// <summary>§42.2 — the guider learns which runs (possibly none) the shared reaction paused for one of its
/// faults, so its star-lost recovery probes and re-centres only for the runs its own episode paused.</summary>
public interface IGuiderEpisodeListener {
    void Paused(EquipmentFaultEvent fault, IReadOnlyList<Guid> runIds);

    /// <summary>Counts the user's guider disconnects. A disconnect drops the open faults without
    /// <c>Recovered</c> and leaves no Disconnected state to read (the guider reads null), so the
    /// paused episode ends when this count moves. A count, not a flag: a Disconnect then Connect
    /// between two polls still ends it.</summary>
    int UserDisconnects => 0;

    /// <summary>#1589 — the shared reaction holds this guider fault no longer: its episode ended (it skipped,
    /// aborted or only notified, the fault was recovered, or the user took the guider back), or the fault
    /// joined a guider episode already running. A fault the guider still has open can then make way for a
    /// new one that runs the plan again. Called outside the reaction's lock.</summary>
    void Released(EquipmentFaultEvent fault) { }

    /// <summary>#2169 — the guider was guiding when this fault took guiding away (a link loss mid-guide):
    /// the pause also stops the LIGHT in progress, which is not saved or counted and is taken again on
    /// Resume. Called outside the reaction's lock.</summary>
    bool GuidedWhenLost(EquipmentFaultEvent fault) => false;

    /// <summary>#2169 — the episode's fault was one <see cref="GuidedWhenLost"/> said took guiding away; it is
    /// recovered and the runs it paused are about to resume. The guider starts guiding again and waits for it
    /// to settle first, so the run never resumes unguided. False = guiding did not come back: the runs stay
    /// paused and the reaction tries again later. Cancelled when the guider is lost again first. Called outside
    /// the reaction's lock.</summary>
    Task<bool> RestoreGuidingAsync(IReadOnlyList<Guid> runIds, CancellationToken ct) => Task.FromResult(true);

    /// <summary>#2169 — an unguided run's guide camera dropped: takes one guide frame. True = a real frame came
    /// (or guide frames came already) and the camera-drop fault is recovered; false = the camera is not back
    /// yet, the reaction tries again later. Called outside the reaction's lock.</summary>
    Task<bool> CheckGuideCameraAsync(CancellationToken ct) => Task.FromResult(true);

    /// <summary>#2169 — completes when the guider says a dropped device is back (only a hint: the next try
    /// decides). A hint that came since the last call is returned completed, once. Called outside the reaction's lock.</summary>
    Task RetryHint() => NeverHint;

    private static readonly Task NeverHint = new TaskCompletionSource().Task;
}

/// <summary>
/// §42.3 — executes the <see cref="FaultPolicyMatrix"/> plan for each detected equipment fault:
/// pause a running sequence immediately when a sequence-critical device (camera/mount) drops,
/// hot-reconnect on the 0/5/15/30/60 s ladder via <see cref="IEquipmentReconnector"/>, resume
/// the paused runs on recovery, and fall to the plan's terminal action (stay paused / abort +
/// best-effort park) when every attempt fails. One episode per device type at a time; each step
/// is best-effort (SafetyReactionService's rung pattern) and every action is observable via the
/// <c>equipment.fault_action_taken</c> WS event + a notification. The guider's faults take the same
/// path with its own row (<c>on_guider_lost</c>) and no Recovery rung yet: GuiderService runs its own
/// §63.3 / star-lost recoveries and reports <see cref="EquipmentFaultHub.Recovered"/>, which ends the
/// episode and resumes exactly the runs it paused.
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
    private readonly Func<IGuiderEpisodeListener?>? _guiderEpisodes;
    // #2066 — the give-up park stops a running polar alignment and the Polar Aligner first, as the safety park does.
    // Func<>: polar alignment is built from services registered around this one.
    private readonly Func<IPolarAlignService?>? _polarAlignResolver;
    private readonly IPolarAlignerService? _polarAligner;

    private readonly object _gate = new();
    // Keyed by normalized DeviceType for recovery-running kinds (their ladder must not stack);
    // notify-only kinds get a unique key so they always run — see OnFault.
    private readonly Dictionary<object, Task> _episodes = [];
    // A guider episode waits here for the hub's Recovered (the guider service's own recoveries are
    // not behind a Recovery rung yet). Registered in OnFault, before the episode task starts, so a
    // Recovered that lands while the pause is still being placed is not lost. Mutated under _gate.
    private readonly Dictionary<object, (EquipmentFaultEvent Fault, TaskCompletionSource Recovered)> _recoveryWaiters = [];
    // #2169 review — a guider episode whose fault recovered and that is now restoring guiding or resuming.
    // A guider fault that lands in that window is the episode's follow-up (the runs it paused stay paused
    // and it waits for that fault's recovery) instead of being let go with no episode. Mutated under _gate.
    private readonly Dictionary<object, GuiderRestore> _guiderRestores = [];
    // §42.2 items 2+5 — persistence counter for op-channel faults (mutated under _gate).
    private readonly OpFaultEscalator _escalator = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Test seams — compress the §42.3 ladder and the connect-confirm polling so unit tests
    // run in milliseconds (the SafetyReactionService knob pattern).
    internal Func<TimeSpan, TimeSpan> DelayShaper { get; set; } = static d => d;
    internal TimeSpan ConnectConfirmTimeout { get; set; } = TimeSpan.FromSeconds(12);
    // A connect the service still reports as Connecting is waited out to the Alpaca connect budget
    // (plus slack for the background dispatch) instead of the confirm window: a bridge searching
    // for its hardware answers late, and walking the next rung would only count a false failure.
    internal TimeSpan ConnectInFlightTimeout { get; set; } = AlpacaConnect.Timeout + TimeSpan.FromSeconds(5);
    internal TimeSpan ConnectPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    // §42.3 linger phase — slow-cadence post-give-up re-adoption (~an hour at the defaults).
    // How often a guider episode that waits for Recovered checks whether the user disconnected the guider.
    internal TimeSpan ReleasePollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>#2169 — the waits between tries to bring guiding (or a dropped guide camera) back; the last
    /// one repeats. Internal so tests can shorten it.</summary>
    internal IReadOnlyList<TimeSpan> GuiderRetryBackoff { get; set; } = [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
    ];
    internal TimeSpan LingerInterval { get; set; } = TimeSpan.FromSeconds(120);
    internal int LingerMaxAttempts { get; set; } = 30;
    /// <summary>#2066 — how long the park waits for polar alignment and the Polar Aligner to stop first. A routine that
    /// hangs must not hold the park back: the park also counts as a stop, so the routine starts no move after it.</summary>
    internal TimeSpan StopPolarAlignBeforeParkTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public FaultReactionService(
            EquipmentFaultHub? hub = null,
            IEquipmentReconnector? reconnector = null,
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            ITelescopeService? telescope = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            ILogger<FaultReactionService>? logger = null,
            IFaultLogService? faultLog = null,
            Func<IGuiderEpisodeListener?>? guiderEpisodes = null,
            Func<IPolarAlignService?>? polarAlignResolver = null,
            IPolarAlignerService? polarAligner = null) {
        _polarAlignResolver = polarAlignResolver;
        _polarAligner = polarAligner;
        _hub = hub;
        _reconnector = reconnector;
        _profiles = profiles;
        _sequencerResolver = sequencerResolver;
        _telescope = telescope;
        _notifications = notifications;
        _ws = ws;
        _logger = logger ?? NullLogger<FaultReactionService>.Instance;
        _faultLog = faultLog;
        _guiderEpisodes = guiderEpisodes;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _hub?.Subscribe(OnFault);
        _hub?.SubscribeRecovered(OnRecovered);
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
        // #1589 — the guider publishes its faults while it holds its own _gate, and its listener
        // methods (Paused, Released) take that _gate. So never call _guiderEpisodes
        // inside this service's lock(_gate): a guider thread holding its _gate would wait here for
        // ours while we wait for its _gate — a deadlock. Call the listener after the lock is released.
        if (!TryStartEpisode(fault) && !TryFollowUpGuiderEpisode(fault) && fault.DeviceType == DeviceType.Guider) {
            // #1589 — joined the running guider episode (or none started): no episode holds this fault.
            _guiderEpisodes?.Invoke()?.Released(fault);
        }
    }

    // #2169 review — the guider episode is restoring guiding after its fault recovered: a new guider loss
    // (the link dropped again, the star lost in the restart's settle) becomes its follow-up. The restore in
    // flight is cut short and the episode waits for the new fault's recovery, with the runs still paused.
    private bool TryFollowUpGuiderEpisode(EquipmentFaultEvent fault) {
        if (fault.DeviceType != DeviceType.Guider || fault.Kind is not (EquipmentFaultKind.Disconnected or EquipmentFaultKind.TrackingLost)) {
            return false;
        }
        lock (_gate) {
            var key = (object)NormalizeType(fault.DeviceType);
            if (_disposed || _cts.IsCancellationRequested || !_guiderRestores.TryGetValue(key, out var restore)
                    || restore.FollowUp is not null) {
                return false;
            }
            restore.FollowUp = fault;
            _recoveryWaiters[key] = (fault, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            // Asynchronous continuations: the restore stops on the pool, never on the guider's publishing thread.
            restore.LostAgain.TrySetResult();
            return true;
        }
    }

    private sealed class GuiderRestore {
        public readonly TaskCompletionSource LostAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EquipmentFaultEvent? FollowUp;
    }

    private bool TryStartEpisode(EquipmentFaultEvent fault) {
        lock (_gate) {
            if (_disposed || _cts.IsCancellationRequested) {
                return false;
            }
            var recoverable = fault.Kind is EquipmentFaultKind.Disconnected or EquipmentFaultKind.TrackingLost;
            var key = recoverable ? (object)NormalizeType(fault.DeviceType) : new object();
            if (recoverable && _episodes.ContainsKey(key)) {
                LogEpisodeAlreadyActive(fault.DeviceType, fault.Kind);
                return false;
            }
            // §42.2 items 2+5 — non-recoverable kinds also feed the persistence counter: the
            // fault that tips its device over the threshold escalates (its episode runs the
            // matrix's escalation plan instead of the plain notify).
            if (recoverable && fault.DeviceType == DeviceType.Guider) {
                _recoveryWaiters[key] = (fault, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            var escalated = !recoverable
                && _escalator.Observe(NormalizeType(fault.DeviceType), fault.Kind, DateTimeOffset.UtcNow);
            _episodes[key] = Task.Run(() => RunEpisodeAsync(key, fault, escalated), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Hub callback for <see cref="EquipmentFaultHub.Recovered"/>: ends the guider episode
    /// that fault started. A recovery of a fault folded into the running episode (a duplicate) does
    /// not end it.</summary>
    internal void OnRecovered(EquipmentFaultEvent fault) {
        lock (_gate) {
            var key = (object)NormalizeType(fault.DeviceType);
            if (_recoveryWaiters.TryGetValue(key, out var waiter) && waiter.Fault == fault && waiter.Recovered.TrySetResult()) {
                // #2169 review — from here a new guider fault is this episode's follow-up (TryFollowUpGuiderEpisode).
                // A camera drop's episode has one already: it tries to bring the camera back before Recovered.
                _guiderRestores.TryAdd(key, new GuiderRestore());
            }
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
            var label = fault.DeviceName ?? fault.DeviceId ?? fault.DeviceType.ToString();
            LogEpisodeStarted(fault.DeviceType, fault.Kind, label, plan.RetrySchedule.Count, plan.TerminalAction);

            if (plan.Escalated) {
                await ExecuteEscalationAsync(fault, plan, label).ConfigureAwait(false);
                return;
            }

            Task? guiderRecovered = null;
            lock (_gate) {
                if (_recoveryWaiters.TryGetValue(key, out var waiter)) {
                    guiderRecovered = waiter.Recovered.Task;
                }
            }
            if (guiderRecovered is not null) {
                await RunGuiderEpisodeAsync(key, fault, plan, label, guiderRecovered).ConfigureAwait(false);
                return;
            }

            if (plan.RetrySchedule.Count == 0 && plan.TerminalAction == FaultTerminalAction.None) {
                await PublishActionAsync(fault, "notify_only").ConfigureAwait(false);
                await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{fault.DeviceType} fault: {EquipmentFaultHub.WireToken(fault.Kind)}",
                    $"'{label}' reported {EquipmentFaultHub.WireToken(fault.Kind)}{Detail(fault)}.").ConfigureAwait(false);
                return;
            }

            // Pause FIRST (§42.3 flagged decision): the pause lets a frame in progress finish
            // and stops any other instruction, to run again on Resume — and a sequence must
            // not keep exposing into a dead camera or slewing an unwatched mount while the
            // ladder runs.
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
                    // #1307 — a user who disconnected the device while the ladder ran has taken it
                    // back: the rung must not re-dispatch (that connect would overwrite their
                    // Disconnected with Error and the linger could no longer tell it from a lost
                    // device). Checked before the attempt AND after a failed one, so a disconnect
                    // that lands mid-confirm is seen before the next rung.
                    if (fault.Kind == EquipmentFaultKind.Disconnected
                            && await ReleasedByUserAsync(fault, label, pausedRuns).ConfigureAwait(false)) {
                        return;
                    }
                    var recovered = fault.Kind == EquipmentFaultKind.TrackingLost
                        ? await TryReenableTrackingAsync().ConfigureAwait(false)
                        : await TryReconnectAsync(fault.DeviceType).ConfigureAwait(false);
                    if (!recovered && fault.Kind == EquipmentFaultKind.Disconnected
                            && await ReleasedByUserAsync(fault, label, pausedRuns).ConfigureAwait(false)) {
                        return;
                    }
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

            // #1307 — a plan with no retries reaches here straight from the pause; the same release
            // check applies before the terminal action parks a mount the user just let go of.
            if (fault.Kind == EquipmentFaultKind.Disconnected
                    && await ReleasedByUserAsync(fault, label, pausedRuns).ConfigureAwait(false)) {
                return;
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
            bool guiderEpisode;
            EquipmentFaultEvent? lastHeld = null;
            lock (_gate) {
                _episodes.Remove(key);
                guiderEpisode = _recoveryWaiters.Remove(key, out var waiter);
                lastHeld = waiter.Fault;
                _guiderRestores.Remove(key);
            }
            // #1589 — after the slot is free, so a fault the guider publishes in answer starts its own episode.
            // #2169 review — a follow-up the episode took over is let go too.
            if (guiderEpisode) {
                _guiderEpisodes?.Invoke()?.Released(fault);
                if (lastHeld is not null && !ReferenceEquals(lastHeld, fault)) {
                    _guiderEpisodes?.Invoke()?.Released(lastHeld);
                }
            }
        }
    }

    // §42.2 guider row (FaultPolicyMatrix.ResolveGuider): skip / abort act and end; a pause waits for
    // the guider service's Recovered (or the user taking the guider back) and resumes exactly the runs
    // it paused. No runs: the fault is recorded and broadcast, the action is notify_only.
    private async Task RunGuiderEpisodeAsync(object key, EquipmentFaultEvent fault, FaultPlan plan, string label, Task recovered) {
        var disconnectsAtFault = _guiderEpisodes?.Invoke()?.UserDisconnects ?? 0;
        var what = fault.Details switch {
            "star_lost" => "Guide star lost",
            "equipment_disconnected" => "Guide camera dropped",
            "dec_runaway" => "Dec guiding ran away",
            _ => "Guiding lost",
        };
        // #1589 — the daemon stopped guiding itself: guiding on the same calibration would run away again.
        var advice = fault.Details == "dec_runaway"
            ? " Guiding was stopped. Calibrate the guider again (after a meridian flip, check the guider's 'Meridian flip reverses Dec' setting) before you resume."
            : "";
        if (plan.Skip || plan.TerminalAction == FaultTerminalAction.Abort) {
            var acted = plan.Skip ? await SkipActiveRunsAsync().ConfigureAwait(false) : await AbortActiveRunsAsync().ConfigureAwait(false);
            if (acted == 0) {
                await PublishActionAsync(fault, "notify_only").ConfigureAwait(false);
                await NotifyQuietlyAsync(plan.GiveUpSeverity, what, $"'{label}' reported {fault.Details}; no sequence was running.{advice}").ConfigureAwait(false);
            } else if (plan.Skip) {
                await PublishActionAsync(fault, "skipped", extra: p => p["skipped_runs"] = acted).ConfigureAwait(false);
                await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{what} — current target skipped",
                    $"'{label}' reported {fault.Details} and your safety policy is set to skip: the current instructions were skipped.{advice}").ConfigureAwait(false);
            } else {
                await PublishActionAsync(fault, "gave_up", persistAs: "gave_up:abort", extra: p => {
                    p["terminal"] = "abort";
                    p["aborted_runs"] = acted;
                }).ConfigureAwait(false);
                await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{what} — sequence aborted",
                    $"'{label}' reported {fault.Details} and your safety policy is set to abort: {acted} run(s) aborted, the mount was not parked.{advice}").ConfigureAwait(false);
            }
            return;
        }

        // Outside _gate: the guider takes its own lock (lock order guider, hub, reaction).
        // #2169 — guiding was on when the link went: the light in progress is stopped, not finished unguided,
        // and guiding is restarted before the runs resume.
        var guided = _guiderEpisodes?.Invoke()?.GuidedWhenLost(fault) == true;
        var interruptFrames = plan.Adoptable || guided;
        var paused = await PauseActiveRunsAsync(plan.Adoptable, interruptFrames).ConfigureAwait(false);
        _guiderEpisodes?.Invoke()?.Paused(fault, paused);
        if (paused.Count == 0) {
            await PublishActionAsync(fault, "notify_only").ConfigureAwait(false);
            await NotifyQuietlyAsync(plan.GiveUpSeverity, what, $"'{label}' reported {fault.Details}; no sequence was running.{advice}").ConfigureAwait(false);
            return;
        }
        await PublishActionAsync(fault, "sequence_paused", extra: p => p["paused_runs"] = paused.Count).ConfigureAwait(false);
        await NotifyQuietlyAsync(plan.GiveUpSeverity, $"{what} — sequence paused",
            $"'{label}' reported {fault.Details}: {paused.Count} run(s) paused so no unguided frame burns sky time. "
            + $"They resume on their own once the guider is back; otherwise Resume them.{advice}").ConfigureAwait(false);

        var current = fault;
        var retry = new GuiderRetry();
        while (true) {
            // #2169 rig check — a camera drop is over only when real frames come: the guider's "camera back"
            // is a hint. So its episode tries to bring the camera back first, and frames recover the fault.
            var framesDecide = current.Details == "equipment_disconnected";
            if (!framesDecide) {
                while (!recovered.IsCompleted) {
                    await Task.WhenAny(recovered, Task.Delay(ReleasePollInterval, _cts.Token)).ConfigureAwait(false);
                    if (_cts.IsCancellationRequested) {
                        return; // daemon shutdown: the runs stay paused, as the ladder leaves them
                    }
                    var userDisconnected = (_guiderEpisodes?.Invoke()?.UserDisconnects ?? 0) != disconnectsAtFault;
                    if (!recovered.IsCompleted && await ReleasedByUserAsync(current, label, paused, userDisconnected).ConfigureAwait(false)) {
                        return;
                    }
                }
            }
            GuiderRestore restore;
            lock (_gate) {
                if (!_guiderRestores.TryGetValue(key, out restore!)) {
                    restore = new GuiderRestore();
                    _guiderRestores[key] = restore;
                }
            }
            // #2169 — guiding first: a run that was guided resumes only once guiding is back and settled. If it
            // does not come back yet, the runs stay paused (never resumed unguided) and Ara tries again.
            var outcome = guided || framesDecide
                ? await RetryUntilBackAsync(current, label, paused, guided, framesDecide, restore.LostAgain.Task,
                    disconnectsAtFault, retry).ConfigureAwait(false)
                : GuiderRetryOutcome.Back;
            EquipmentFaultEvent? followUp = null;
            lock (_gate) {
                if (_guiderRestores.Remove(key)) {
                    followUp = restore.FollowUp;
                    if (followUp is not null && _recoveryWaiters.TryGetValue(key, out var waiter)) {
                        recovered = waiter.Recovered.Task;
                    }
                }
            }
            if (followUp is not null) {
                // #2169 review — guider lost again before the runs resumed: they stay paused for the new fault.
                // A link or camera loss during a guided restore still needs guiding back; a star loss
                // relocks on its own.
                guided = followUp.Kind == EquipmentFaultKind.Disconnected
                    ? guided || _guiderEpisodes?.Invoke()?.GuidedWhenLost(followUp) == true
                    : _guiderEpisodes?.Invoke()?.GuidedWhenLost(followUp) == true;
                _guiderEpisodes?.Invoke()?.Paused(followUp, paused);
                LogGuiderFollowUp(followUp.Details ?? EquipmentFaultHub.WireToken(followUp.Kind));
                current = followUp;
                continue;
            }
            if (outcome != GuiderRetryOutcome.Back) {
                return; // daemon shutdown, or the user took the runs or the guider over
            }
            break;
        }
        var resumed = await ResumeRunsAsync(paused).ConfigureAwait(false);
        // The hub's Recovered already broadcast `recovered` and resolved the fault-log row.
        if (resumed == 0) {
            return; // the user (or safety) already settled the pause: nothing to announce
        }
        var back = fault.Details switch {
            "star_lost" => "Guide star back",
            "equipment_disconnected" => "Guide camera back",
            _ => "Guiding restored",
        };
        await NotifyQuietlyAsync(NotificationSeverity.Info, $"{back} — sequence resumed",
            $"'{label}' is {(guided || fault.Details != "equipment_disconnected" ? "guiding" : "sending guide frames")} again; {resumed} paused sequence run(s) resumed.").ConfigureAwait(false);
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

    // #1307 — a Disconnected state during a DISCONNECT episode is the shape of a deliberate user (or
    // API) disconnect: the services only park a LOST device in Error, and only DisconnectAsync /
    // Forget write Disconnected. The device is no longer ours to chase: the episode ends here with
    // no further reconnect dispatch and no terminal park. Runs the up-front pause left paused stay
    // paused — the user chose to take the device away, so resuming would be wrong — and the
    // outcome is recorded and notified so the sequence state is not a mystery. Same convention
    // the linger has always used; the ladder and the terminal action now honour it too.
    // #1539 — a service with the lifecycle module records the user's disconnect, and that answer
    // comes first; the Disconnected state stays the signal for the services without it.
    // The guider passes its own answer ([userDisconnected], IGuiderEpisodeListener.UserDisconnects).
    private async Task<bool> ReleasedByUserAsync(EquipmentFaultEvent fault, string label, IReadOnlyList<Guid> pausedRuns,
            bool userDisconnected = false) {
        if (!userDisconnected && _reconnector?.GetDesiredConnection(fault.DeviceType) != DesiredConnection.DisconnectedByUser) {
            var state = await GetConnectionStateQuietlyAsync(fault.DeviceType).ConfigureAwait(false);
            if (state != EquipmentConnectionState.Disconnected) {
                return false;
            }
        }
        await PublishActionAsync(fault, "released", persistAs: "released:user_disconnect", extra: p => {
            p["paused_runs"] = pausedRuns.Count;
        }).ConfigureAwait(false);
        await NotifyQuietlyAsync(NotificationSeverity.Info, $"{fault.DeviceType} released",
            $"'{label}' was disconnected by hand while recovery was running; recovery stopped"
            + $"{(pausedRuns.Count > 0 ? $", {pausedRuns.Count} paused sequence run(s) left paused" : "")}.")
            .ConfigureAwait(false);
        LogReleasedByUser(fault.DeviceType);
        return true;
    }

    // §42.3 — the post-give-up linger: poll the device type's state at LingerInterval for up to
    // LingerMaxAttempts (~an hour at the defaults). While the service reports Error the remembered
    // device is re-dispatched through the same TryReconnectAsync the ladder uses; the moment the
    // type reads Connected — whether the linger's own dispatch landed or the user re-seated a
    // cable and the daemon auto-connected — and, for a device on the lifecycle module, has had a
    // clean read since that connect (#1246: a latched bridge answers Connected while every read
    // fails, and gets the re-dispatch instead) — the device is re-adopted: runs the terminal pause left
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
                case EquipmentConnectionState.Connected when !IsCleanlyConnected(fault.DeviceType):
                    // #1246 — Connected with no clean read since the connect (a latched bridge, failing
                    // reads): not back. Re-dispatch like the Error branch; the lifecycle's recovery
                    // dispatch sends the disconnect/connect pair (DeviceLifecycle.Dispatch).
                    if (!await TryReconnectAsync(fault.DeviceType).ConfigureAwait(false)) {
                        continue;
                    }
                    break;
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
            // #1539 — RecoverAsync, not ReconnectAsync: a device the user disconnected is refused in
            // the same step as the dispatch, so a disconnect after the release check is not undone.
            var outcome = await _reconnector.RecoverAsync(type, _cts.Token).ConfigureAwait(false);
            if (outcome.Dispatched == 0) {
                return false; // nothing remembered, the user let it go, or every dispatch failed on the spot
            }
            // The dispatch connects in the background — poll the service state to a verdict.
            var sw = Stopwatch.StartNew();
            TimeSpan? landedAt = null; // when the state first left Connecting
            while (true) {
                var state = await _reconnector.GetConnectionStateAsync(type, _cts.Token).ConfigureAwait(false);
                // #1246 — Connected is not recovered: a bridge still latched after the reconnect answers
                // Connected while every read fails. Wait for one clean tick, or let the confirm window run
                // out and walk the next rung.
                if (state == EquipmentConnectionState.Connected && IsCleanlyConnected(type)) {
                    return true;
                }
                if (state == EquipmentConnectionState.Error) {
                    return false; // the background connect already failed — next rung
                }
                if (state == EquipmentConnectionState.Connecting) {
                    if (sw.Elapsed >= ConnectInFlightTimeout) {
                        return false;
                    }
                } else {
                    // The confirm window (e.g. the mount's clean tick) starts when the connect
                    // lands; timed from the dispatch, a slow connect would leave it already spent.
                    landedAt ??= sw.Elapsed;
                    if (sw.Elapsed - landedAt >= ConnectConfirmTimeout) {
                        return false;
                    }
                }
                await Task.Delay(ConnectPollInterval, _cts.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            throw; // unwind the episode on shutdown
        } catch (Exception ex) {
            LogAttemptFailed("reconnect", type, ex);
            return false;
        }
    }

    // #1246 — Connected is not "recovered": a bridge can answer Connected while every read fails after a
    // latch. The device's lifecycle says whether a clean read followed the connect (ADR 0011 rule 2); a
    // guider (PHD2, not on the module) answers true (EquipmentReconnector.IsCleanlyConnected).
    private bool IsCleanlyConnected(DeviceType type) => _reconnector?.IsCleanlyConnected(type) ?? true;

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
    private async Task<IReadOnlyList<Guid>> PauseActiveRunsAsync(bool adoptable = false, bool interruptFrames = false) {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return [];
        }
        try {
            // An adoptable pause lets the safety engine take the runs over (a parked guider sends no
            // guide step to resume on), and stops the light in progress. #2169 — a guided link loss stops
            // the light too, without handing the pause to anyone.
            if (adoptable) {
                return await sequencer.PauseActiveRunsAsync(adoptable: true, CancellationToken.None).ConfigureAwait(false);
            }
            return interruptFrames
                ? await sequencer.PauseActiveRunsAsync(adoptable: false, interruptFrames: true, CancellationToken.None).ConfigureAwait(false)
                : await sequencer.PauseActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
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

    private enum GuiderRetryOutcome { Back, Stopped }

    private sealed class GuiderRetry {
        public int Waits;   // waits so far this episode: picks the next backoff step
        public bool Told;   // the one "still trying" notice was sent
    }

    // #2169 — Joey's rule: Ara recovers on its own and never gives up after one try. Tries to bring guiding
    // back (guided) or the guide camera (a camera drop) until it works, the user takes the runs or the guider
    // over, the daemon stops, or the guider is lost again (the caller picks up that follow-up). The runs stay
    // paused throughout; the user hears it once. A camera drop waits first (the camera has just gone) unless
    // the guider already said it is back.
    private async Task<GuiderRetryOutcome> RetryUntilBackAsync(EquipmentFaultEvent fault, string label, IReadOnlyList<Guid> paused,
            bool guided, bool waitFirst, Task lostAgain, int disconnectsAtFault, GuiderRetry retry) {
        var wait = waitFirst;
        while (true) {
            if (wait) {
                var backoff = GuiderRetryBackoff;
                var delay = backoff.Count == 0 ? TimeSpan.Zero : backoff[Math.Min(retry.Waits, backoff.Count - 1)];
                retry.Waits++;
                var hint = _guiderEpisodes?.Invoke()?.RetryHint() ?? Task.CompletedTask;
                var deadline = DateTime.UtcNow + delay;
                while (!hint.IsCompleted && !lostAgain.IsCompleted) {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) {
                        break;
                    }
                    await Task.WhenAny(hint, lostAgain, Task.Delay(left < ReleasePollInterval ? left : ReleasePollInterval, _cts.Token))
                        .ConfigureAwait(false);
                    if (_cts.IsCancellationRequested) {
                        return GuiderRetryOutcome.Stopped;
                    }
                    if (await UserTookOverAsync(fault, label, paused, disconnectsAtFault).ConfigureAwait(false)) {
                        return GuiderRetryOutcome.Stopped;
                    }
                }
                if (hint.IsCompleted) {
                    _ = _guiderEpisodes?.Invoke()?.RetryHint(); // used: the next wait needs a new hint
                }
            }
            wait = true;
            if (lostAgain.IsCompleted) {
                return GuiderRetryOutcome.Stopped; // the caller takes the follow-up
            }
            if (_cts.IsCancellationRequested || await UserTookOverAsync(fault, label, paused, disconnectsAtFault).ConfigureAwait(false)) {
                return GuiderRetryOutcome.Stopped;
            }
            var back = guided
                ? await RestoreGuidingQuietlyAsync(paused, lostAgain).ConfigureAwait(false)
                : await CheckGuideCameraQuietlyAsync(lostAgain).ConfigureAwait(false);
            if (lostAgain.IsCompleted || _cts.IsCancellationRequested) {
                return GuiderRetryOutcome.Stopped;
            }
            if (back) {
                return GuiderRetryOutcome.Back;
            }
            if (!retry.Told && await AnyStillDaemonPausedAsync(paused).ConfigureAwait(false)) {
                retry.Told = true;
                var title = guided ? "Guiding not back yet — still trying, sequence stays paused"
                    : "Guide camera not back yet — still trying, sequence stays paused";
                await NotifyQuietlyAsync(NotificationSeverity.Warning, title, guided
                    ? $"'{label}' did not restart guiding and settle yet, so {paused.Count} run(s) stay paused and no unguided frame is taken. "
                      + "Ara keeps trying and resumes them once guiding is back. You can also Resume them yourself."
                    : $"'{label}' sends no guide frames yet, so {paused.Count} run(s) stay paused. "
                      + "Ara keeps checking and resumes them once the guide camera is back. You can also Resume them yourself.")
                    .ConfigureAwait(false);
            }
        }
    }

    // The user disconnected the guider, or resumed, paused or stopped the runs: the retries stop.
    private async Task<bool> UserTookOverAsync(EquipmentFaultEvent fault, string label, IReadOnlyList<Guid> paused, int disconnectsAtFault) {
        var userDisconnected = (_guiderEpisodes?.Invoke()?.UserDisconnects ?? 0) != disconnectsAtFault;
        if (await ReleasedByUserAsync(fault, label, paused, userDisconnected).ConfigureAwait(false)) {
            return true;
        }
        return !await AnyStillDaemonPausedAsync(paused).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a guider fault while checking the camera leaves the runs paused for the next try. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> CheckGuideCameraQuietlyAsync(Task lostAgain) {
        var listener = _guiderEpisodes?.Invoke();
        if (listener is null) {
            return true;
        }
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try {
            var check = listener.CheckGuideCameraAsync(ct.Token);
            if (await Task.WhenAny(check, lostAgain).ConfigureAwait(false) == lostAgain) {
                await ct.CancelAsync().ConfigureAwait(false);
            }
            return await check.ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false;
        } catch (Exception ex) {
            LogRungFailed("check_guide_camera", ex);
            return false;
        }
    }

    // #2169 — the guider brings guiding back before its runs resume (IGuiderEpisodeListener.RestoreGuidingAsync).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a guider fault while restarting guiding leaves the runs paused, the safe outcome. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> RestoreGuidingQuietlyAsync(IReadOnlyList<Guid> paused, Task? lostAgain) {
        var listener = _guiderEpisodes?.Invoke();
        if (listener is null) {
            return true;
        }
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try {
            var restore = listener.RestoreGuidingAsync(paused, ct.Token);
            if (lostAgain is not null && await Task.WhenAny(restore, lostAgain).ConfigureAwait(false) == lostAgain) {
                await ct.CancelAsync().ConfigureAwait(false); // the guider was lost again: stop waiting on this restore
            }
            return await restore.ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false; // daemon shutdown, or the guider was lost again (the episode's follow-up)
        } catch (Exception ex) {
            LogRungFailed("restore_guiding", ex);
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort read: a sequencer fault counts as still paused, so the user hears why. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> AnyStillDaemonPausedAsync(IReadOnlyList<Guid> ids) {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return false;
        }
        try {
            return (await sequencer.DaemonPausedRunsAsync(ids.ToArray(), CancellationToken.None).ConfigureAwait(false)).Count > 0;
        } catch (Exception ex) {
            LogRungFailed("custody", ex);
            return true;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort rung: a sequencer fault must not stop the remaining reaction steps. CA1031's log-and-recover boundary applies.")]
    private async Task<int> SkipActiveRunsAsync() {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return 0;
        }
        try {
            return await sequencer.SkipActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogRungFailed("skip", ex);
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
            // #2066 — a polar alignment turning the RA motor (and its Polar Aligner) is stopped before the park: the stop
            // its turn sends when it ends would otherwise land on the park.
            await StopPolarAlignQuietlyAsync().ConfigureAwait(false);
            await _telescope.ParkAsync(new ParkRequestDto("equipment.fault"), null, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        } catch (RefusalException refusal) when (refusal.Cause == RefusalCatalog.MountPositionUnverified) {
            // #2070 — a park from a false zero drives the tube to the wrong place: the mount stops instead.
            await StopTrackingInsteadOfParkAsync(refusal).ConfigureAwait(false);
            return false;
        } catch (Exception ex) {
            LogRungFailed("park", ex);
            return false;
        }
    }

    /// <summary>#2066 — the park's first step (<see cref="ReactionMountStops.StopPolarAlignBeforeParkAsync"/>, shared with the
    /// other reaction's park so the two stay in step), each step bounded by <see cref="StopPolarAlignBeforeParkTimeout"/>.</summary>
    private Task StopPolarAlignQuietlyAsync() =>
        ReactionMountStops.StopPolarAlignBeforeParkAsync(_polarAlignResolver, _polarAligner, MountStopReasons.FaultPark,
            StopPolarAlignBeforeParkTimeout, LogRungFailed);

    // #2070 — the park was refused because the mount's position is unverified: stop its motion and its
    // tracking (best effort) and say so loudly, since the run ended with the mount unparked. The abort
    // also ends any axis lease (a pad move): the refused park no longer cancels them itself.
    private async Task StopTrackingInsteadOfParkAsync(RefusalException refusal) {
        // #2107 — counted under its own reason: a polar alignment this stop ends names this reaction, not Stop Mount.
        var (motionStopped, stopped) = await ReactionMountStops.StopMotionAndTrackingAsync(_telescope, MountStopReasons.FaultReaction, LogRungFailed)
            .ConfigureAwait(false);
        LogParkRefusedUnverified(stopped);
        await NotifyQuietlyAsync(NotificationSeverity.Critical, "Mount NOT parked (position unverified)",
            "An equipment fault asked for a park, but the mount was not parked: its position is unverified (it may have lost power), so a park could drive it to the wrong place. "
            + (motionStopped ? "Its motion was stopped. " : "Stopping its motion FAILED. ")
            + (stopped ? "Tracking was stopped instead. " : "Stopping tracking FAILED too. ")
            + "Check the rig. " + refusal.Message).ConfigureAwait(false);
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
        // (last write wins across the episode); 'recovered' and 'released:*' also resolve it.
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
            // 'recovered' resolves the row; so does a #1307 release — the user took the device back,
            // nothing is pending, and the §42.5 reconnect hook must not later report it as an
            // unresolved disconnect it "fixed".
            var resolvedUtc = action == "recovered" || action.StartsWith("released", StringComparison.Ordinal)
                ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: {DeviceType} was disconnected by the user during recovery — episode released, no reconnect or park (#1307)")]
    private partial void LogReleasedByUser(DeviceType deviceType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: linger for {DeviceType} stopped — state {State} means the device is no longer ours to chase (§42.3)")]
    private partial void LogLingerStopped(DeviceType deviceType, EquipmentConnectionState? state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fault reaction: linger for {DeviceType} exhausted after {Attempts} attempts — a manual reconnect will still be resolved by the §42.5 reconnect hook")]
    private partial void LogLingerExhausted(DeviceType deviceType, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: {Stage} attempt for {DeviceType} failed; the ladder moves on")]
    private partial void LogAttemptFailed(string stage, DeviceType deviceType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: best-effort rung '{Rung}' failed; remaining steps still run")]
    private partial void LogRungFailed(string rung, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fault reaction park refused: the mount's position is unverified (#2070); tracking stopped instead: {Stopped}")]
    private partial void LogParkRefusedUnverified(bool stopped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fault reaction: episode for {DeviceType} failed unexpectedly")]
    private partial void LogEpisodeFailed(DeviceType deviceType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault reaction: the guider was lost again ({Details}) before its paused runs resumed; they stay paused until it is back")]
    private partial void LogGuiderFollowUp(string details);
}