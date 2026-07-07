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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §58.12 — the unattended-failure graceful-shutdown countdown.
///
/// When a run enters <see cref="SequenceRunState.PausedAwaitingUser"/> (an
/// urgent failure suspended it — e.g. a §58.9 meridian-flip abort with the
/// mount in safe rest), this service starts a countdown (profile
/// <c>safety.unattended_shutdown_wait_minutes</c>, default 10). Any sign of the
/// user coming back cancels it; if the full window elapses unattended, the rig
/// is put to bed gracefully so a sleeping user's equipment doesn't sit powered
/// and cold all night.
///
/// What counts as "the user came back" (per the playbook): dismissing or
/// marking-read a notification, any explicit sequence lifecycle command, an
/// equipment-control POST, a FRESH §27 control-slot claim (no prior session
/// id), or a WebSocket from a client without an existing session. What does
/// NOT: background REST polling (GETs), pre-existing quiet sockets, WILMA's
/// own automatic reconnect/re-claim after a network blip (an existing session
/// id marks those — flaky Wi-Fi must not defeat this safety net all night),
/// or the daemon's own automated actions (the §29 disk monitor's abort calls
/// <c>AbortActiveRunsAsync</c> directly, which never touches this service) —
/// the hooks live at the HTTP layer precisely so internal calls don't
/// masquerade as attention.
///
/// The shutdown ladder (each step best-effort, logged, never throws):
///   1. stop the guider (corrections on a mis-aimed scope drift it further)
///   2. park the mount when it can park (90 s cap), else stop tracking
///   3. disconnect filter wheel / focuser / rotator / flat panel
///   4. warm the cooler by stepping the set-point toward ambient at the
///      profile's <c>CoolerRampCPerMin</c> (hard 30-minute cap), then cooler off
///   5. disconnect the camera — only AFTER the warm-up, per §58.12 (a
///      disconnect mid-warm-up strands the TEC cold and the sensor warms
///      violently when power drops)
///   6. post a Warning summary notification. Deliberately NOT Critical: the
///      situation is now stable and a Critical would re-trigger the client's
///      §35.5 alarm the shutdown just resolved.
///
/// Deliberate deviations from the playbook's config block: the action ladder is
/// fixed (no per-action toggles — enabled + wait_minutes are the real dials),
/// the server always keeps running, and the run stays suspended with its §40
/// capture session open — the worker's terminal finally still owns the session,
/// and the §28.2 reconciler covers a daemon restart. "[Resume session]" the
/// next morning therefore keeps working: resume re-attempts the flip, whose
/// §58.9 flight check reports exactly which equipment needs reconnecting.
/// </summary>
public sealed partial class UnattendedShutdownService : IHostedService, IDisposable {

    private readonly IProfileStore? _profiles;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly IGuiderMediator? _guider;
    private readonly ITelescopeMediator? _telescope;
    private readonly ICameraService? _camera;
    private readonly IFilterWheelService? _filterWheel;
    private readonly IFocuserService? _focuser;
    private readonly IRotatorService? _rotator;
    private readonly IFlatDeviceService? _flatDevice;
    private readonly INotificationService? _notifications;
    private readonly ILogger<UnattendedShutdownService> _logger;

    private readonly object _lock = new();
    // Non-null while a countdown is pending. One at a time: a second
    // awaiting-user entry while a countdown is already running joins it (the
    // first failure started the clock; the user is equally absent for both).
    private CancellationTokenSource? _pending;
    // True from the moment an elapsed countdown disarms itself until its ladder
    // finishes. A second awaiting-user entry in that window is dropped: the
    // ladder already puts the SHARED rig to bed, and two concurrent ladders
    // would race the same mediators (and cross-stamp each other's summary).
    private bool _executing;
    // Non-null exactly while _executing: user activity mid-ladder cancels it so
    // the ladder stops taking equipment down under a user who just came back.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Owned by CountdownAsync, which disposes its local reference in a finally after nulling this field; disposing here would race that owner.")]
    private CancellationTokenSource? _ladderCts;
    private Task? _worker;
    private Guid _sequenceId;

    // ─── test seams (the ClientSessionService knob pattern) ───
    /// <summary>Maps the profile's wait_minutes to the actual delay; tests compress it.</summary>
    internal Func<int, TimeSpan> WaitFromMinutes { get; set; } = m => TimeSpan.FromMinutes(m);
    /// <summary>Time scale for the warm-up loop: the set-point steps ramp °C
    /// once per (1 minute / scale). Production stays 1.0 — the camera sees the
    /// profile's true °C/min; tests raise it to compress minutes into ms.</summary>
    internal double WarmTimeScale { get; set; } = 1.0;
    /// <summary>Hard cap on the whole warm-up, per §58.12 step 5.</summary>
    internal TimeSpan WarmHardCap { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Cap on the park attempt (mirrors §58.9 SafeRest's bound).</summary>
    internal TimeSpan ParkTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>The set-point the warm-up ramps toward. +10 °C is a safely
    /// "warm" TEC target for any climate; the cooler turns OFF once reached, so
    /// the sensor finishes settling to true ambient passively.</summary>
    internal const double WarmTargetC = 10.0;

    public UnattendedShutdownService(
            IProfileStore? profiles = null,
            Func<ISequencerService?>? sequencerResolver = null,
            IGuiderMediator? guider = null,
            ITelescopeMediator? telescope = null,
            ICameraService? camera = null,
            IFilterWheelService? filterWheel = null,
            IFocuserService? focuser = null,
            IRotatorService? rotator = null,
            IFlatDeviceService? flatDevice = null,
            INotificationService? notifications = null,
            ILogger<UnattendedShutdownService>? logger = null) {
        _profiles = profiles;
        _sequencerResolver = sequencerResolver;
        _guider = guider;
        _telescope = telescope;
        _camera = camera;
        _filterWheel = filterWheel;
        _focuser = focuser;
        _rotator = rotator;
        _flatDevice = flatDevice;
        _notifications = notifications;
        _logger = logger ?? NullLogger<UnattendedShutdownService>.Instance;
    }

    /// <summary>Whether a countdown is currently pending (armed, not yet fired).</summary>
    public bool IsCountingDown {
        get { lock (_lock) { return _pending is not null; } }
    }

    /// <summary>
    /// A run just suspended awaiting the user — start the countdown (no-op when
    /// the profile disables the shutdown, or a countdown is already pending).
    /// Called from the engine thread (SequencerService.OnPauseEntered), so it
    /// must return fast: the wait runs on a background task.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown/countdown.")]
    public void NotifyRunPausedAwaitingUser(Guid sequenceId, Guid runId) {
        SafetyPoliciesDto? safety = null;
        try {
            safety = _profiles?.GetSafetyPolicies();
        } catch (Exception ex) {
            // Best-effort like the §58.10 escalation shim: a profile fault must
            // not break the pause itself. No policy readable → default-enabled
            // (the safe direction for an unattended failure) with the default wait.
            LogPolicyReadFailed(ex);
        }
        if (safety is { UnattendedShutdownEnabled: false }) {
            LogCountdownDisabled(sequenceId);
            return;
        }
        var wait = WaitFromMinutes(Math.Max(1, safety?.UnattendedShutdownWaitMinutes ?? 10));

        lock (_lock) {
            // First failure already started the clock, or its ladder is still
            // putting the rig to bed — either way the user is equally absent
            // and the (idempotent) ladder covers the shared equipment once.
            // DELIBERATE: a run that re-enters awaiting-user WHILE the ladder
            // executes (a resume racing the ladder, the flip re-failing fast
            // against half-disconnected equipment) is dropped without a fresh
            // countdown — the rig is already being put to bed by the running
            // ladder, and that failure's own Critical notification still
            // reaches the user; re-arming here would re-run a mostly-no-op
            // ladder (and post a fresh summary) every wait-window forever.
            if (_pending is not null || _executing) return;
            var cts = new CancellationTokenSource();
            _pending = cts;
            _sequenceId = sequenceId;
            _worker = Task.Run(() => CountdownAsync(sequenceId, runId, wait, cts), CancellationToken.None);
        }
        LogCountdownStarted(sequenceId, runId, wait);
    }

    /// <summary>
    /// Evidence the user is back (an explicit API command, a notification
    /// dismiss/mark-read, a fresh WS connection, a §27 connect). Cancels any
    /// pending countdown; harmless when none is running.
    /// </summary>
    public void NotifyUserActivity(string source) {
        CancellationTokenSource? toCancel;
        CancellationTokenSource? toAbandon;
        Guid cancelledSequenceId;
        lock (_lock) {
            toCancel = _pending;
            _pending = null;
            cancelledSequenceId = _sequenceId;
            toAbandon = _executing ? _ladderCts : null;
        }
        if (toCancel is not null) {
            LogCountdownCancelled(source, cancelledSequenceId);
            try {
                toCancel.Cancel();
            } finally {
                toCancel.Dispose();
            }
        }
        if (toAbandon is not null) {
            // The user came back MID-LADDER (the teardown can run ~30 min
            // through the cooler ramp). Abandon the remaining steps so the
            // ladder never takes equipment down underneath a user who is now
            // driving it — the in-flight step finishes, nothing further runs.
            LogLadderAbandoned(source, cancelledSequenceId);
            try {
                toAbandon.Cancel();
            } catch (ObjectDisposedException) {
                // The ladder finished (and disposed its CTS) in this instant —
                // there is nothing left to abandon.
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown/countdown.")]
    private async Task CountdownAsync(Guid sequenceId, Guid runId, TimeSpan wait, CancellationTokenSource ownCts) {
        try {
            await Task.Delay(wait, ownCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // the user came back
        }

        // The window elapsed. Disarm the pending slot but hold _executing so a
        // later awaiting-user entry can NOT start a second countdown while this
        // ladder (up to ~30 min of cooler warm-up) is still running against the
        // shared rig; a fresh countdown becomes possible again once it ends.
        // Identity check closes the elapse-vs-activity TOCTOU: if a concurrent
        // NotifyUserActivity disarmed the slot in the instant AFTER the delay
        // completed but BEFORE this lock, the user came back — honor it (their
        // Cancel() was a no-op on an already-completed delay, so this check is
        // the only thing that stops the ladder for state-preserving activity
        // like a notification dismiss).
        CancellationTokenSource ladderCts;
        lock (_lock) {
            if (!ReferenceEquals(_pending, ownCts)) {
                return; // activity won the race — no ladder
            }
            _pending.Dispose();
            _pending = null;
            _executing = true;
            ladderCts = new CancellationTokenSource();
            _ladderCts = ladderCts;
        }

        try {
            // Re-verify the run is STILL awaiting the user: a resume/abort that
            // raced the timer's last tick (or any path that didn't route through
            // NotifyUserActivity) must not shut a now-running rig down.
            try {
                var state = _sequencerResolver?.Invoke() is { } seq
                    ? await seq.GetRunStateAsync(sequenceId, CancellationToken.None).ConfigureAwait(false)
                    : null;
                if (state is null || state.RunId != runId || state.State != SequenceRunState.PausedAwaitingUser) {
                    LogCountdownStale(sequenceId, state?.State);
                    return;
                }
            } catch (Exception ex) {
                // Can't confirm the run still needs the shutdown → do nothing. A
                // wrong no-op leaves equipment running (recoverable, and the §58.10
                // escalation already made the failure loud); a wrong shutdown
                // tears down a rig that resumed imaging.
                LogStateRecheckFailed(sequenceId, ex);
                return;
            }

            await ExecuteShutdownAsync(sequenceId, ladderCts.Token).ConfigureAwait(false);
        } finally {
            lock (_lock) {
                _executing = false;
                _ladderCts = null;
            }
            ladderCts.Dispose();
        }
    }

    /// <summary>
    /// The §58.12 ladder. Every step is best-effort: a dead device mid-ladder
    /// must not stop the remaining equipment from being put to bed. Between
    /// steps the ladder honors <paramref name="abandon"/> — user activity
    /// mid-ladder cancels it, and the teardown must never continue underneath
    /// a user who just came back and started driving the rig (the resumed
    /// sequence believes it is Running; disconnecting its camera under it is a
    /// race, not safety). The in-flight step always finishes.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown/countdown.")]
    internal async Task ExecuteShutdownAsync(Guid sequenceId, CancellationToken abandon = default) {
        LogShutdownStarted(sequenceId);
        var summary = new System.Text.StringBuilder();
        var abandoned = false;
        bool Abandoned() {
            if (abandoned) return true;
            if (!abandon.IsCancellationRequested) return false;
            abandoned = true;
            summary.Append("Remaining shutdown steps abandoned — you came back. ");
            return true;
        }

        // 1 — stop the guider. GetInfo is only an optimization to skip a
        // disconnected guider — if the read itself throws, the state is
        // UNKNOWN and the stop is still attempted (an unknown guider quietly
        // correcting a safe-rested mount is the failure mode, not a wasted
        // no-op stop call).
        if (_guider is not null) {
            bool? guiderConnected = null;
            try {
                guiderConnected = _guider.GetInfo().Connected;
            } catch (Exception ex) {
                LogStepFailed("guider-info", ex);
            }
            if (guiderConnected != false) {
                try {
                    await _guider.StopGuiding(CancellationToken.None).ConfigureAwait(false);
                    summary.Append("Guider stopped. ");
                } catch (Exception ex) {
                    LogStepFailed("stop-guider", ex);
                    summary.Append("Guider stop FAILED. ");
                }
            }
        }

        // 2 — park the mount (or stop tracking). Same moves as §58.9 SafeRest,
        // (skipped, like every later step, once the user is back — see Abandoned)
        // including its two-stage shape: the park attempt gets its OWN catch so
        // a park that fails by THROWING (the 90 s timeout cancels the token)
        // still falls through to the tracking-stop — a tracking, unattended
        // mount is the exact outcome this feature exists to prevent. Both moves
        // are idempotent when safe rest already ran.
        try {
            if (!Abandoned() && _telescope is not null) {
                OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo? mount = null;
                try {
                    mount = _telescope.GetInfo();
                } catch (Exception ex) {
                    LogStepFailed("mount-info", ex);
                }
                if (mount is null) {
                    // State unreadable — the one thing still worth trying blind
                    // is the tracking stop: an unattended TRACKING mount is the
                    // outcome this feature exists to prevent, and a stop sent
                    // to a parked/disconnected mount is a harmless no-op.
                    _telescope.SetTrackingEnabled(false);
                    summary.Append("Mount state unreadable — tracking stop attempted. ");
                } else if (mount.CanPark && !mount.AtPark) {
                    var parked = false;
                    try {
                        using var parkCts = new CancellationTokenSource(ParkTimeout);
                        var progress = new Progress<OpenAstroAra.Core.Model.ApplicationStatus>();
                        parked = await _telescope.ParkTelescope(progress, parkCts.Token).ConfigureAwait(false);
                    } catch (Exception ex) {
                        LogStepFailed("park-mount", ex);
                    }
                    if (parked) {
                        summary.Append("Mount parked. ");
                    } else {
                        _telescope.SetTrackingEnabled(false);
                        summary.Append("Park failed — tracking stopped instead. ");
                    }
                } else if (mount.AtPark) {
                    summary.Append("Mount already parked. ");
                } else if (mount.Connected) {
                    _telescope.SetTrackingEnabled(false);
                    summary.Append("Mount cannot park — tracking stopped. ");
                }
            }
        } catch (Exception ex) {
            LogStepFailed("mount-rest", ex);
            summary.Append("Mount park/tracking-stop FAILED. ");
        }

        // 3 — disconnect the ancillary devices. The camera deliberately waits
        // for the warm-up (step 5).
        if (!Abandoned()) await DisconnectQuietly("filter wheel", _filterWheel is null ? null : ct => _filterWheel.DisconnectAsync(null, ct), summary).ConfigureAwait(false);
        if (!Abandoned()) await DisconnectQuietly("focuser", _focuser is null ? null : ct => _focuser.DisconnectAsync(null, ct), summary).ConfigureAwait(false);
        if (!Abandoned()) await DisconnectQuietly("rotator", _rotator is null ? null : ct => _rotator.DisconnectAsync(null, ct), summary).ConfigureAwait(false);
        if (!Abandoned()) await DisconnectQuietly("flat panel", _flatDevice is null ? null : ct => _flatDevice.DisconnectAsync(null, ct), summary).ConfigureAwait(false);

        // 4 — warm the cooler, then 5 — disconnect the camera. An abandon
        // during the (long) ramp leaves the cooler ON at its current set-point
        // and the camera CONNECTED: the returning user may resume imaging, and
        // killing their cooling or yanking the camera is exactly the race this
        // signal exists to prevent.
        try {
            if (!Abandoned() && _camera is not null) {
                var rampCompleted = await WarmCoolerAsync(summary, abandon).ConfigureAwait(false);
                if (rampCompleted && !Abandoned()) {
                    await _camera.DisconnectAsync(null, CancellationToken.None).ConfigureAwait(false);
                    summary.Append("Camera disconnected. ");
                }
            }
        } catch (Exception ex) {
            LogStepFailed("camera", ex);
            summary.Append("Camera warm-up/disconnect FAILED. ");
        }

        var summaryText = summary.ToString();
        LogShutdownComplete(sequenceId, summaryText);

        // 6 — tell the (eventual) morning user what happened. Warning, not
        // Critical: stable now, no new siren (§58.10 may bump it to Error
        // during darkness — still below the alarm threshold). An abandoned
        // ladder posts Info — the user is demonstrably present and this is a
        // record of what already ran, not news that needs a morning surface.
        try {
            if (_notifications is not null) {
                await _notifications.CreateAsync(new NotificationDto(
                    Id: Guid.NewGuid(),
                    PostedUtc: DateTimeOffset.UtcNow,
                    Severity: abandoned ? NotificationSeverity.Info : NotificationSeverity.Warning,
                    Category: NotificationCategory.Safety,
                    Title: abandoned ? "Unattended shutdown abandoned" : "Unattended shutdown executed",
                    Message: (abandoned
                        ? "The unattended-shutdown ladder started, then you came back — the remaining steps were skipped. Steps that already ran: "
                        : "The sequence paused awaiting your attention and nobody responded within the "
                            + "configured window, so the equipment was put to bed: ")
                        + summaryText
                        + "The run stays paused — resume it after reconnecting the equipment, or stop it.",
                    Read: false,
                    Dismissed: false,
                    DismissedUtc: null,
                    Payload: null,
                    RelatedEntityType: "sequence",
                    RelatedEntityId: sequenceId.ToString()), CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogStepFailed("summary-notification", ex);
        }
    }

    /// <summary>
    /// §58.12 step "warm cooler": step the set-point from the current CCD
    /// temperature toward <see cref="WarmTargetC"/> at the profile's
    /// <c>CoolerRampCPerMin</c> (default 1 °C/min), then switch the cooler off.
    /// Bounded by <see cref="WarmHardCap"/>. A camera with the cooler already
    /// off (or no readable temperature) skips the ramp entirely.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown/countdown.")]
    private async Task<bool> WarmCoolerAsync(System.Text.StringBuilder summary, CancellationToken abandon) {
        if (_camera is null) return true;
        var dto = await _camera.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var runtime = dto?.Runtime;
        if (runtime is not { CoolerOn: true }) return true; // nothing to warm
        if (runtime.CcdTemperature is not double startTemp || startTemp >= WarmTargetC) {
            // No readable temperature to ramp from (or already warm): just cut
            // the cooler — with the TEC near ambient there's no thermal shock.
            await _camera.SetCoolerAsync(false, null, CancellationToken.None).ConfigureAwait(false);
            summary.Append("Cooler switched off. ");
            return true;
        }

        double rampPerMin = 1.0;
        try {
            var ramp = _profiles?.GetImagingDefaults().CoolerRampCPerMin;
            if (ramp is > 0) rampPerMin = ramp.Value;
        } catch (Exception ex) {
            LogPolicyReadFailed(ex);
        }
        // One ramp-°C step per scaled minute: the slope the hardware sees is
        // exactly the profile's °C/min at scale 1; a test scale only shortens
        // the sleeps, never changes the per-step temperature delta.
        var stepC = rampPerMin;
        var stepSleep = TimeSpan.FromMinutes(1.0 / WarmTimeScale);
        var setpoint = startTemp;
        var started = DateTimeOffset.UtcNow;
        while (setpoint < WarmTargetC && DateTimeOffset.UtcNow - started < WarmHardCap) {
            setpoint = Math.Min(WarmTargetC, setpoint + stepC);
            await _camera.SetCoolerAsync(true, setpoint, CancellationToken.None).ConfigureAwait(false);
            if (setpoint >= WarmTargetC) break;
            try {
                await Task.Delay(stepSleep, abandon).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // User came back mid-ramp: leave the cooler ON at the current
                // set-point (they may resume imaging — cutting their cooling is
                // the race the abandon signal prevents) and keep the camera
                // connected. False tells the ladder to skip the disconnect.
                summary.Append($"Cooler warm-up abandoned at {setpoint:F0}°C set-point — cooler left ON. ");
                return false;
            }
        }
        await _camera.SetCoolerAsync(false, null, CancellationToken.None).ConfigureAwait(false);
        summary.Append($"Cooler warmed from {startTemp:F0}°C and switched off. ");
        return true;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the rest of the shutdown/countdown.")]
    private async Task DisconnectQuietly(string name, Func<CancellationToken, Task<OperationAcceptedDto>>? disconnect,
            System.Text.StringBuilder summary) {
        if (disconnect is null) return;
        try {
            await disconnect(CancellationToken.None).ConfigureAwait(false);
            summary.Append($"{char.ToUpperInvariant(name[0])}{name[1..]} disconnected. ");
        } catch (Exception ex) {
            LogStepFailed($"disconnect-{name}", ex);
            summary.Append($"{char.ToUpperInvariant(name[0])}{name[1..]} disconnect FAILED. ");
        }
    }

    // IHostedService — nothing to start; on daemon shutdown cancel a pending
    // countdown so the host isn't held up and no ladder fires mid-teardown.
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        // A deliberate daemon stop counts as attention: cancel a still-pending
        // countdown AND abandon an in-flight ladder at its next step boundary
        // (an abandoned mid-ramp cooler stays ON at its set-point — the camera
        // keeps its own TEC powered across the daemon exiting, so this is no
        // worse than ARA never having started the warm-up).
        NotifyUserActivity("daemon-shutdown");
        Task? worker;
        lock (_lock) { worker = _worker; }
        // Then await the worker inside the host's shutdown window (mirroring
        // SequencerService): the abandoning ladder finishes its in-flight step
        // and posts its record instead of being killed mid-await. The host
        // deadline still bounds the wait.
        return worker is null ? Task.CompletedTask : AwaitWorkerAsync(worker, cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Shutdown must never throw; the worker is fire-and-forget by design.")]
    private static async Task AwaitWorkerAsync(Task worker, CancellationToken ct) {
        try {
            await worker.WaitAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Host shutdown deadline hit — nothing more graceful is possible.
        } catch (Exception) {
            // CountdownAsync catches everything itself; belt-and-braces only.
        }
    }

    public void Dispose() => NotifyUserActivity("dispose");

    [LoggerMessage(Level = LogLevel.Information, Message = "UNATTENDED_SHUTDOWN countdown started for sequence {SequenceId} run {RunId}: {Wait} unattended before the rig is put to bed")]
    private partial void LogCountdownStarted(Guid sequenceId, Guid runId, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Information, Message = "UNATTENDED_SHUTDOWN countdown cancelled — user activity via {Source} (sequence {SequenceId})")]
    private partial void LogCountdownCancelled(string source, Guid sequenceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UNATTENDED_SHUTDOWN ladder abandoned mid-flight — user activity via {Source} (sequence {SequenceId}); remaining steps skipped")]
    private partial void LogLadderAbandoned(string source, Guid sequenceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "UNATTENDED_SHUTDOWN disabled by profile — no countdown for sequence {SequenceId}")]
    private partial void LogCountdownDisabled(Guid sequenceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "UNATTENDED_SHUTDOWN countdown for sequence {SequenceId} expired but the run is no longer awaiting the user (state {State}) — no action")]
    private partial void LogCountdownStale(Guid sequenceId, SequenceRunState? state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UNATTENDED_SHUTDOWN could not re-verify the run state for sequence {SequenceId} — declining to shut down")]
    private partial void LogStateRecheckFailed(Guid sequenceId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UNATTENDED_SHUTDOWN profile read failed — using defaults")]
    private partial void LogPolicyReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UNATTENDED_SHUTDOWN executing for sequence {SequenceId}: the awaiting-user window elapsed with no response")]
    private partial void LogShutdownStarted(Guid sequenceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UNATTENDED_SHUTDOWN step {Step} failed")]
    private partial void LogStepFailed(string step, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "UNATTENDED_SHUTDOWN complete for sequence {SequenceId}: {Summary}")]
    private partial void LogShutdownComplete(Guid sequenceId, string summary);
}
