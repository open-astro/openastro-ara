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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1901 — End Night: the user's one action that puts the rig to bed. In order: abort the run and
/// close its Session, stop guiding, Find Home when the mount can, park (or stop tracking), disconnect
/// the other devices, warm the cooler and switch it off, disconnect the camera, notify, and last the
/// host poweroff. The mount, cooler and disconnect steps are the <see cref="ShutdownLadder"/> the
/// unattended shutdown uses.
///
/// <see cref="Start"/> returns at once and the work runs on a background task, so a client that sleeps
/// or loses Wi-Fi does not stop it; <see cref="GetStatus"/> and the <c>end_night.progress</c> event
/// show where it is. Every step is best-effort: one that fails is reported and the next safe step
/// still runs. The camera disconnect waits for a finished warm-up (§58.12: a disconnect mid-warm-up
/// strands the TEC cold), so it is skipped when the warm-up failed or was cancelled.
///
/// <see cref="Cancel"/> takes effect at the next step boundary (the warm-up checks it between ramp
/// steps) and leaves the rig as it is at that point. The host poweroff runs only when the user chose
/// it AND the mount ended parked or with tracking off, and once it starts nothing cancels it.
/// A second <see cref="Start"/> while one runs is the <see cref="RefusalCatalog.EndNightInProgress"/> refusal.
/// It is not the Emergency Stop and does not go through it (ADR 0009 rule 3).
/// </summary>
public sealed partial class EndNightService : IDisposable {

    private readonly ShutdownLadder _ladder;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly INotificationService? _notifications;
    private readonly IHostPoweroffStarter _poweroff;
    private readonly IWsBroadcaster? _ws;
    private readonly UnattendedShutdownService? _unattended;
    private readonly TimeProvider _time;
    private readonly ILogger<EndNightService> _logger;

    private readonly object _lock = new();
    private string _state = EndNightStates.Idle;
    private Guid? _runId;
    private bool _shutDownHost;
    private bool _cancelRequested;
    // True from the moment the poweroff step commits to starting the host's poweroff: nothing cancels it after.
    private bool _poweroffStarted;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _endedUtc;
    private List<EndNightStepDto> _steps = [];
    private CancellationTokenSource? _cancel;
    private Task? _worker;

    // ─── test seams (the UnattendedShutdownService knob pattern) ───
    /// <summary>How long the abort step waits for the aborted run to finish and close its Session.</summary>
    internal TimeSpan AbortWait { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How often the abort step looks for the run to be over.</summary>
    internal TimeSpan AbortPoll { get; set; } = TimeSpan.FromMilliseconds(250);

    public EndNightService(
            ShutdownLadder ladder,
            IHostPoweroffStarter poweroff,
            Func<ISequencerService?>? sequencerResolver = null,
            INotificationService? notifications = null,
            IWsBroadcaster? ws = null,
            UnattendedShutdownService? unattended = null,
            ILogger<EndNightService>? logger = null,
            TimeProvider? time = null) {
        _ladder = ladder;
        _poweroff = poweroff;
        _sequencerResolver = sequencerResolver;
        _notifications = notifications;
        _ws = ws;
        _unattended = unattended;
        _logger = logger ?? NullLogger<EndNightService>.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The task of the action in progress (or the last one); tests await it.</summary>
    internal Task? Worker { get { lock (_lock) { return _worker; } } }

    public EndNightStatusDto GetStatus() {
        lock (_lock) { return StatusLocked(); }
    }

    private EndNightStatusDto StatusLocked() => new(
        _state, _runId, _shutDownHost, CancellableLocked(), _cancelRequested, _startedUtc, _endedUtc, _steps.ToList());

    private bool CancellableLocked() => _state == EndNightStates.Running && !_poweroffStarted;

    /// <summary>#2143 — End Night's reading in <see cref="RigOccupancy"/>.</summary>
    public bool IsRunning {
        get { lock (_lock) { return _state == EndNightStates.Running; } }
    }

    /// <summary>Start the action and return its status at once. Refused while one runs.</summary>
    public EndNightStatusDto Start(bool shutDownHost) {
        Guid runId;
        EndNightStatusDto status;
        lock (_lock) {
            if (_state == EndNightStates.Running) {
                throw new RefusalException(RefusalCatalog.EndNightInProgress,
                    "End Night is already running; read GET /api/v1/end-night/status, or cancel it with POST /api/v1/end-night/cancel.");
            }
            runId = Guid.NewGuid();
            _runId = runId;
            _state = EndNightStates.Running;
            _shutDownHost = shutDownHost;
            _cancelRequested = false;
            _poweroffStarted = false;
            _startedUtc = _time.GetUtcNow();
            _endedUtc = null;
            _steps = EndNightStepIds.All.Select(id => new EndNightStepDto(id, EndNightStepStates.Pending, null)).ToList();
            _cancel?.Dispose();
            var cancel = _cancel = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(runId, shutDownHost, cancel.Token), CancellationToken.None);
            status = StatusLocked();
        }
        // An explicit user command: the unattended countdown (or its ladder) must not race this one on the rig.
        _unattended?.NotifyUserActivity("end-night");
        LogStarted(runId, shutDownHost);
        return status;
    }

    /// <summary>Ask the action to stop at the next step boundary. A no-op when nothing runs or the host
    /// poweroff has started; the returned status says whether it can still be cancelled.</summary>
    public EndNightStatusDto Cancel() {
        CancellationTokenSource? toCancel = null;
        EndNightStatusDto status;
        Guid runId;
        lock (_lock) {
            if (CancellableLocked() && !_cancelRequested) {
                _cancelRequested = true;
                toCancel = _cancel;
            }
            runId = _runId ?? Guid.Empty;
            status = StatusLocked();
        }
        if (toCancel is not null) {
            LogCancelRequested(runId);
            toCancel.Cancel();
            _ = PublishAsync(runId, null, null, null);
        }
        return status;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort by design: a fault in one step must not stop the remaining steps, and the worker is fire-and-forget.")]
    private async Task RunAsync(Guid runId, bool shutDownHost, CancellationToken cancel) {
        var summary = new System.Text.StringBuilder();
        var rest = MountRest.Unknown;
        var warmFinished = false;
        var failures = 0;
        var cancelled = false;
        await PublishAsync(runId, null, null, null).ConfigureAwait(false);

        async Task<bool> StepAsync(string id, Func<Task<(string State, string? Detail)>> body) {
            // A step boundary: a cancel asked for earlier ends the action here, and the rest never starts.
            if (IsCancelRequested()) {
                cancelled = true;
                return false;
            }
            SetStep(id, EndNightStepStates.Running, null);
            await PublishAsync(runId, id, EndNightStepStates.Running, null).ConfigureAwait(false);
            string state;
            string? detail;
            try {
                (state, detail) = await body().ConfigureAwait(false);
            } catch (Exception ex) {
                LogStepFailed(id, ex);
                (state, detail) = (EndNightStepStates.Failed, "The step failed; see the daemon log.");
            }
            if (state == EndNightStepStates.Failed) failures++;
            if (!string.IsNullOrEmpty(detail)) summary.Append(detail).Append(' ');
            SetStep(id, state, detail);
            await PublishAsync(runId, id, state, detail).ConfigureAwait(false);
            return true;
        }

        static (string, string?) Of(LadderStep s) => (s.Outcome switch {
            LadderOutcome.Done => EndNightStepStates.Done,
            LadderOutcome.Failed => EndNightStepStates.Failed,
            _ => EndNightStepStates.Skipped,
        }, string.IsNullOrWhiteSpace(s.Text) ? null : s.Text.Trim());

        try {
            await StepAsync(EndNightStepIds.AbortRun, AbortRunAsync).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.StopGuiding, async () => Of(await _ladder.StopGuidingAsync().ConfigureAwait(false))).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.FindHome, async () => Of(await _ladder.FindHomeAsync().ConfigureAwait(false))).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.ParkMount, async () => {
                var mount = await _ladder.RestMountAsync().ConfigureAwait(false);
                rest = mount.Rest;
                return Of(mount.Step);
            }).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.DisconnectDevices, async () => {
                var steps = new List<LadderStep>();
                foreach (var device in Enum.GetValues<AncillaryDevice>()) {
                    steps.Add(await _ladder.DisconnectAsync(device).ConfigureAwait(false));
                }
                var state = steps.Any(s => s.Outcome == LadderOutcome.Failed) ? EndNightStepStates.Failed
                    : steps.Any(s => s.Outcome == LadderOutcome.Done) ? EndNightStepStates.Done : EndNightStepStates.Skipped;
                return (state, string.Concat(steps.Select(s => s.Text)).Trim());
            }).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.WarmCooler, async () => {
                var warm = await _ladder.WarmCoolerAsync(cancel).ConfigureAwait(false);
                // Done or nothing to warm: the camera may go. Failed or abandoned: it stays connected.
                warmFinished = warm.Outcome is LadderOutcome.Done or LadderOutcome.Skipped;
                if (warm.Outcome == LadderOutcome.Cancelled) cancelled = true;
                return Of(warm);
            }).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.DisconnectCamera, async () => warmFinished
                ? Of(await _ladder.DisconnectCameraAsync().ConfigureAwait(false))
                : (EndNightStepStates.Skipped, "Camera left connected: the cooler was not warmed.")).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.Notify, () => NotifyAsync(summary.ToString(), failures)).ConfigureAwait(false);
            if (!cancelled) await StepAsync(EndNightStepIds.HostPoweroff, () => PoweroffAsync(shutDownHost, rest)).ConfigureAwait(false);
        } finally {
            await FinishAsync(runId, cancelled || IsCancelRequested()).ConfigureAwait(false);
        }
    }

    private bool IsCancelRequested() {
        lock (_lock) { return _cancelRequested; }
    }

    private void SetStep(string id, string state, string? detail) {
        lock (_lock) {
            var i = _steps.FindIndex(s => s.Id == id);
            if (i >= 0) _steps[i] = new EndNightStepDto(id, state, detail);
        }
    }

    private async Task FinishAsync(Guid runId, bool cancelled) {
        lock (_lock) {
            _state = cancelled ? EndNightStates.Cancelled : EndNightStates.Complete;
            _endedUtc = _time.GetUtcNow();
            // The steps that never ran say so, rather than reading as pending forever.
            for (var i = 0; i < _steps.Count; i++) {
                if (_steps[i].State == EndNightStepStates.Pending) {
                    _steps[i] = new EndNightStepDto(_steps[i].Id, EndNightStepStates.Skipped, "Cancelled before this step.");
                }
            }
        }
        LogFinished(runId, cancelled);
        await PublishAsync(runId, null, null, null).ConfigureAwait(false);
    }

    /// <summary>Abort the run and wait until it is over, so its Session is closed before the mount moves.</summary>
    private async Task<(string, string?)> AbortRunAsync() {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) return (EndNightStepStates.Done, null);
        var aborted = await sequencer.AbortActiveRunsAsync(CancellationToken.None).ConfigureAwait(false);
        var deadline = _time.GetUtcNow() + AbortWait;
        while ((await sequencer.UnfinishedRunIdsAsync(CancellationToken.None).ConfigureAwait(false)).Count > 0) {
            if (_time.GetUtcNow() >= deadline) return (EndNightStepStates.Failed, "The run did not finish aborting in time; End Night went on.");
            await Task.Delay(AbortPoll, CancellationToken.None).ConfigureAwait(false);
        }
        return (EndNightStepStates.Done, aborted > 0 ? "Run aborted and its Session closed." : "No run was running.");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort notice: failing to post it must not stop the host poweroff decision behind it.")]
    private async Task<(string, string?)> NotifyAsync(string summary, int failures) {
        if (_notifications is null) return (EndNightStepStates.Skipped, null);
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: failures > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
                Category: NotificationCategory.Safety,
                Title: failures > 0 ? "End Night finished with problems" : "End Night finished",
                Message: summary.Length == 0 ? "End Night ran; there was nothing to do." : summary.Trim(),
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
            return (EndNightStepStates.Done, null);
        } catch (Exception ex) {
            LogStepFailed(EndNightStepIds.Notify, ex);
            return (EndNightStepStates.Failed, "The summary notification could not be posted.");
        }
    }

    private async Task<(string, string?)> PoweroffAsync(bool shutDownHost, MountRest rest) {
        if (!shutDownHost) return (EndNightStepStates.Skipped, "The Pi stays on, as chosen.");
        if (rest == MountRest.Unknown) {
            return (EndNightStepStates.Skipped, "The Pi stays on: the mount did not end parked or with tracking off.");
        }
        lock (_lock) {
            // The last boundary: a cancel that arrived while the notice was posting still wins; after this it cannot.
            if (_cancelRequested) return (EndNightStepStates.Skipped, "Cancelled before the shutdown.");
            _poweroffStarted = true;
        }
        var result = await _poweroff.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return result.Started
            ? (EndNightStepStates.Done, "The Pi is shutting down; it is safe to switch the power off once it is unreachable.")
            : (EndNightStepStates.Failed, "The Pi did not shut down: " + result.Message);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A progress event is best-effort: a broadcaster fault must not stop the rig being put to bed.")]
    private async Task PublishAsync(Guid runId, string? step, string? stepState, string? detail) {
        if (_ws is null) return;
        try {
            string state;
            bool cancellable;
            lock (_lock) { state = _state; cancellable = CancellableLocked(); }
            var payload = new EndNightProgressPayload(runId.ToString(), state, step, stepState, detail, cancellable);
            await _ws.PublishAsync(WsEventCatalog.EndNightProgress,
                JsonSerializer.SerializeToElement(payload, AraJsonSerializerContext.Default.EndNightProgressPayload),
                CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogPublishFailed(ex);
        }
    }

    public void Dispose() {
        lock (_lock) {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "END_NIGHT {RunId} started (shut down host: {ShutDownHost})")]
    private partial void LogStarted(Guid runId, bool shutDownHost);

    [LoggerMessage(Level = LogLevel.Information, Message = "END_NIGHT {RunId} cancel requested; it stops at the next step boundary")]
    private partial void LogCancelRequested(Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "END_NIGHT {RunId} finished (cancelled: {Cancelled})")]
    private partial void LogFinished(Guid runId, bool cancelled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "END_NIGHT step {Step} failed")]
    private partial void LogStepFailed(string step, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "END_NIGHT could not publish a progress event")]
    private partial void LogPublishFailed(Exception ex);
}