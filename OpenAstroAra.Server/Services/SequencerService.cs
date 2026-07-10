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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38 — the real <see cref="ISequencerService"/>. Replaces
/// <c>PlaceholderSequencerService</c>'s mock <c>Task.Delay</c> loop with actual
/// execution of a deserialized sequence body through NINA's inherited
/// <see cref="Sequencer"/> (per playbook §8.1, the sequencer engine is kept —
/// only the WPF threading/UI coupling is stripped). The container tree's own
/// <c>Run</c> preserves the full semantics: conditions, loops, triggers,
/// nested + parallel containers.
///
/// Lifecycle: <see cref="StartAsync"/> loads the saved body, deserializes it to
/// an <see cref="ISequenceRootContainer"/>, and drives it on a background worker
/// that emits the §60.9 WS events + maintains the §28 active-run checkpoint.
/// <see cref="AbortAsync"/> / <see cref="StopAsync"/> cancel the run.
///
/// Equipment is still the headless-stub set (every <c>Headless*Mediator</c>
/// reports "not connected"), so no-equipment instructions (waits, annotations,
/// conditions) execute for real while equipment-bound instructions no-op /
/// report failure cleanly. Real driving lands when the Alpaca stubs swap for
/// real drivers (§14e).
///
/// <see cref="PauseAsync"/> / <see cref="ResumeAsync"/> are REAL: each run owns a
/// <see cref="OpenAstroAra.Sequencer.Utility.PauseGate"/> attached to the root
/// container; the execution strategies await it at every instruction boundary
/// (the current instruction always finishes — NINA pause semantics). The run
/// reports <c>Paused</c> and emits <c>sequence.paused</c> only when the engine
/// ACTUALLY suspends (the gate's PauseEntered callback), never on the mere
/// request; Abort/Stop win over an active pause via token cancellation.
/// </summary>
public sealed partial class SequencerService : ISequencerService, IHostedService {

    private readonly IWsBroadcaster? _ws;
    // Lazy resolver — FileSequenceService (ISequenceService) and this service
    // reference each other (ListAsync surfaces run state per §38j-4; StartAsync
    // reads the stored body). A Func<> defers resolution past the DI cycle.
    private readonly Func<ISequenceService?>? _sequencesResolver;
    private readonly ActiveSequenceCheckpoint? _checkpoint;
    private readonly IFrameRepository? _frames;
    private readonly SequenceBodyDeserializer _deserializer;
    // §58.12 — the unattended-shutdown countdown. Notified when a run enters
    // PausedAwaitingUser (starts the clock) and on every explicit lifecycle
    // command (the user is back — cancels it). AbortActiveRunsAsync and the
    // hosted-service shutdown path deliberately do NOT notify: those are the
    // daemon's own automated actions, not user attention.
    private readonly UnattendedShutdownService? _unattendedShutdown;
    private readonly ILogger<SequencerService> _logger;
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    // Completed runs stay queryable via GetRunState after they finish (WILMA
    // polls run-state past completion); cap retention so the dictionary can't
    // grow unbounded over a long-lived daemon. Live (non-terminal) runs are
    // never evicted.
    private const int MaxRetainedRuns = 64;
    // §28 active/current.json is a single file (single-active-run by design —
    // one sequence at a time on shared equipment). Track which run currently
    // owns it so a finishing run can't clear/stomp a checkpoint a newer run wrote.
    private readonly object _checkpointGate = new();
    private Guid _checkpointOwner;

    public SequencerService(
            SequenceBodyDeserializer deserializer,
            IWsBroadcaster? ws = null,
            Func<ISequenceService?>? sequencesResolver = null,
            ActiveSequenceCheckpoint? checkpoint = null,
            ILogger<SequencerService>? logger = null,
            IFrameRepository? frames = null,
            UnattendedShutdownService? unattendedShutdown = null,
            IProfileStore? profileStore = null,
            Func<ICalibrationService?>? calibrationResolver = null,
            INotificationService? notifications = null) {
        _deserializer = deserializer;
        _ws = ws;
        _sequencesResolver = sequencesResolver;
        _checkpoint = checkpoint;
        _logger = logger ?? NullLogger<SequencerService>.Instance;
        _frames = frames;
        _unattendedShutdown = unattendedShutdown;
        _profileStore = profileStore;
        _calibrationResolver = calibrationResolver;
        _notifications = notifications;
    }

    public Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(id, out var run) ? run.ToDto(id) : null);

    public Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        StartCoreAsync(id, request, idempotencyKey, announceAutoFlats: true);

    // §48 — the auto-started end-of-session flats run must not itself announce
    // (a prompt asking whether to calibrate the calibration run, or a re-tagged
    // profile default re-executing against the flats-only session — #735 review).
    internal Task<OperationAcceptedDto> StartCoreAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, bool announceAutoFlats) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.start");
        // Atomically reserve the run slot BEFORE any async work. Two concurrent
        // starts for the same id otherwise both pass a separate guard and both
        // spawn a worker (TOCTOU). AddOrUpdate is atomic per key: an absent slot
        // takes this fresh run; a live (non-terminal) run keeps the slot; a
        // finished run is replaced (restart allowed). We own the run iff the
        // returned reference is ours.
        var run = TryReserveRun(id);
        if (run is null) {
            // A live run already owns the slot — idempotent.
            return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey));
        }
        PruneTerminalRuns();

        // Assign the worker synchronously — before any await — so
        // IHostedService.StopAsync always finds a Worker to await (closes the
        // null-Worker shutdown window). The body load + deserialize + §28
        // checkpoint claim run inside the worker, off the request path.
        // §48.1 — resolve the profile's capture default SYNCHRONOUSLY before the
        // worker exists: a fast run must never reach its completion check before
        // the choice lands (#735 review — the two-Task.Run race). Only the WS
        // announcement rides a background task.
        string? autoFlatsToken = null;
        if (announceAutoFlats) {
            autoFlatsToken = ResolveAutoFlatsToken();
            if (autoFlatsToken is ChoicePanelAtEnd or ChoiceSkyAtTwilight) {
                run.AutoFlatsChoice = autoFlatsToken;
            }
        }
        run.Worker = Task.Run(() => LoadAndRunAsync(id, run), CancellationToken.None);
        if (autoFlatsToken is not null) {
            var runId = run.RunId;
            _ = Task.Run(() => EmitAutoFlatsAnnouncementAsync(id, runId, autoFlatsToken), CancellationToken.None);
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey));
    }

    // Worker entry: load + deserialize the body, then drive it. Split from
    // StartAsync so the worker Task is assigned synchronously (no null-Worker
    // window during shutdown). Broad catch on the load boundary: a bad body or
    // store error must mark the run Failed + notify, never fault the worker task.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Body-load boundary on the background worker: deserialization / store access can throw IO/Json/argument/etc.; any escape must surface as a Failed run via WS and be contained, not fault the worker. CA1031's log-and-recover boundary exception applies.")]
    private async Task LoadAndRunAsync(Guid id, RunState run) {
        ISequenceRootContainer? root;
        try {
            root = await LoadRootAsync(id, run);
        } catch (OperationCanceledException) when (run.Cts.IsCancellationRequested) {
            // Abort/stop arrived during the body load (RequestCancelAsync already
            // set Aborting/Stopped + cancelled the token). Route through the
            // worker's terminal path with a throwaway root: it skips execution for
            // a non-Starting state and emits aborted/stopped (NOT failed), clears
            // any checkpoint, and disposes the CTS.
            await RunWorkerAsync(id, run, new SequenceRootContainer());
            return;
        } catch (Exception ex) {
            LogRunFailed(ex, id);
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", id, run);
            return;
        }

        if (root is null) {
            // No body / undeserializable: fail rather than spin over nothing. The
            // §38.5 validator normally enforces body validity at persist time.
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", id, run);
            return;
        }

        // §28 — checkpoint from start-of-run so the §28.2 reconciler can spot an
        // interrupted run. Claim ownership first so this run's writes/clear win.
        ClaimCheckpoint(run);
        WriteCheckpointIfOwner(run, id);
        // §38 pause — hang the run's gate off the root so the execution
        // strategies suspend on it at instruction boundaries, and observe actual
        // suspension (not the mere request) for the Paused state + WS event.
        if (root is OpenAstroAra.Sequencer.Utility.IPauseGateHost host) {
            host.PauseGate = run.Gate;
            run.Gate.PauseEntered += (_, _) => OnPauseEntered(id, run);
        }
        // Publish the running root so SkipAsync can reach it for the duration of the run.
        run.SetRoot(root);
        try {
            await RunWorkerAsync(id, run, root);
        } finally {
            // Release the sequence tree as soon as the run ends. The terminal RunState
            // lingers in _runs (up to MaxRetainedRuns) so it stays queryable, but it no
            // longer needs to pin every SequenceItem/container in memory. SkipAsync only
            // reaches Root for abortable (non-terminal) runs, so a null here is harmless.
            run.SetRoot(null);
        }
    }

    private async Task<ISequenceRootContainer?> LoadRootAsync(Guid id, RunState run) {
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is null) return null;
        // Use the run's own token (the request CT is gone once StartAsync
        // returned), so an abort during load still cancels it.
        var dto = await sequences.GetAsync(id, run.Cts.Token);
        if (dto is null) return null;
        // instructions_total + instructions_completed are derived from the deserialized leaf
        // instructions by the worker (UpdateProgress) — the precise denominator we
        // count completions against — so no separate inspector count is set here.
        return ToRootContainer(dto.Body);
    }

    public Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.pause");
        // Arm the run's instruction-boundary gate; the engine suspends at the next
        // boundary (SequentialStrategy awaits the gate before each item, so the
        // current instruction always finishes — NINA pause semantics). The state
        // flip to Paused + the sequence.paused event happen in OnPauseEntered when
        // the engine ACTUALLY suspends, never on the mere request. Idempotent, and
        // harmless for terminal/aborting runs (a cancelled token wins the gate).
        if (_runs.TryGetValue(id, out var run) && IsAbortableRun(run.State)) {
            run.Gate.RequestPause();
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.resume");
        if (_runs.TryGetValue(id, out var run)) {
            // CAS BEFORE releasing the gate: while the engine is suspended it
            // cannot advance, so a successful Paused→Running here always gets its
            // sequence.resumed out — the resumed run can't race to Completed/
            // Stopped and starve the event (it only moves once Resume() below
            // releases it). The CAS still keeps this from resurrecting a run an
            // Abort/Stop moved past Paused (their unconditional state write +
            // token cancel win; a released gate is then harmless). Resume covers
            // BOTH paused flavors — a §58.12 awaiting-user pause is cleared by
            // the same explicit user command (that IS the user coming back).
            if (run.TryTransition(SequenceRunState.Paused, SequenceRunState.Running)
                || run.TryTransition(SequenceRunState.PausedAwaitingUser, SequenceRunState.Running)) {
                _ = EmitAsync("sequence.resumed", id, run);
                WriteCheckpointIfOwner(run, id);
            }
            // Always disarm — this also cancels a pause that was requested but
            // never reached a boundary (state still Running, CAS above failed),
            // and is harmless on terminal runs.
            run.Gate.Resume();
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.resume", idempotencyKey));
    }

    // PauseGate.PauseEntered callback — runs on the engine worker at the moment a
    // strategy actually suspends. Running→Paused (or PausedAwaitingUser, when the
    // gate was armed by the engine itself after an urgent failure — §58.12) via
    // CAS so a concurrent Abort/Stop (which sets Aborting/Stopped unconditionally)
    // is never clobbered; parallel branches may fire this repeatedly — the CAS
    // also makes it idempotent. The Paused→PausedAwaitingUser escalation handles
    // a failure that arms an ALREADY-suspended run (a parallel branch's flip
    // failing while the user has the run paused): the next branch to reach its
    // boundary re-fires this callback and upgrades the state.
    private void OnPauseEntered(Guid sequenceId, RunState run) {
        var target = run.Gate.PendingKind == OpenAstroAra.Sequencer.Utility.PauseKind.AwaitingUser
            ? SequenceRunState.PausedAwaitingUser
            : SequenceRunState.Paused;
        if (run.TryTransition(SequenceRunState.Running, target)
            || (target == SequenceRunState.PausedAwaitingUser
                && run.TryTransition(SequenceRunState.Paused, SequenceRunState.PausedAwaitingUser))) {
            _ = EmitAsync("sequence.paused", sequenceId, run);
            WriteCheckpointIfOwner(run, sequenceId);
            if (target == SequenceRunState.PausedAwaitingUser) {
                // §58.12 — the rig needs a human; start the unattended-shutdown
                // clock. Runs on the engine thread: the call only arms a timer.
                _unattendedShutdown?.NotifyRunPausedAwaitingUser(sequenceId, run.RunId);
            }
        }
    }

    public Task<OperationAcceptedDto> SkipAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.skip");
        // Skip whatever the run is currently executing (e.g. a target that isn't well-positioned):
        // SkipCurrentRunningItems() cancels each running item so the sequence advances to the next.
        // A no-op for a non-running run — skipping nothing is harmless, so it's always Accepted.
        if (_runs.TryGetValue(id, out var run) && IsAbortableRun(run.State)) {
            run.Root?.SkipCurrentRunningItems();
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.skip", idempotencyKey));
    }

    public async Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.abort");
        await RequestCancelAsync(id, SequenceRunState.Aborting);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.abort", idempotencyKey);
    }

    public async Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.stop");
        await RequestCancelAsync(id, SequenceRunState.Stopped);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.stop", idempotencyKey);
    }

    public async Task<int> AbortActiveRunsAsync(CancellationToken ct) {
        var aborted = 0;
        // Snapshot the keys (ConcurrentDictionary.Keys copies) so a worker removing itself mid-iteration — while
        // we await below — can't matter; re-fetch each run by id since it may have changed by the time we reach it.
        foreach (var id in _runs.Keys) {
            ct.ThrowIfCancellationRequested();
            if (!_runs.TryGetValue(id, out var run)) {
                continue;
            }
            // Skip terminal AND already-Aborting runs: a re-entrant call (a fast Critical→Warn→Critical
            // oscillation) must not re-count / re-notify a run that's already being aborted.
            if (!IsAbortableRun(run.State)) {
                continue;
            }
            // Count only runs this call actually transitioned — a run that finished naturally in the TOCTOU
            // window between the check above and the cancel returns false and isn't counted.
            if (await RequestCancelAsync(id, SequenceRunState.Aborting)) {
                aborted++;
            }
        }
        return aborted;
    }

    public Task<IReadOnlyList<Guid>> PauseActiveRunsAsync(CancellationToken ct) {
        // §35 safety pause — daemon-automated, so no NotifyUserActivity (an automated
        // reaction must never masquerade as the user coming back per §58.12). Same
        // gate-arm semantics as PauseAsync: the engine suspends at the next instruction
        // boundary, the state flip + sequence.paused event fire in OnPauseEntered.
        var paused = new System.Collections.Generic.List<Guid>();
        foreach (var id in _runs.Keys) {
            ct.ThrowIfCancellationRequested();
            if (!_runs.TryGetValue(id, out var run)) {
                continue;
            }
            // Only arm runs that are actually advancing. An already-Paused (or
            // PausedAwaitingUser) run is excluded so the safety engine never
            // "adopts" a pause the user (or a flip failure) owns — auto-resume
            // must release only pauses this call created.
            var state = run.State;
            if (!IsAbortableRun(state) || state is SequenceRunState.Paused or SequenceRunState.PausedAwaitingUser) {
                continue;
            }
            run.Gate.RequestPause();
            paused.Add(id);
        }
        return Task.FromResult<IReadOnlyList<Guid>>(paused);
    }

    public Task<int> ResumeRunsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) {
        // §35 safety auto-resume — daemon-automated (no NotifyUserActivity), and it
        // deliberately does NOT clear PausedAwaitingUser: that state is a debt to the
        // user (§58.12) that only an explicit command settles. Mirrors ResumeAsync's
        // CAS-before-release ordering so the resumed event can't be starved.
        var resumed = 0;
        foreach (var id in ids) {
            ct.ThrowIfCancellationRequested();
            if (!_runs.TryGetValue(id, out var run)) {
                continue;
            }
            if (run.State == SequenceRunState.PausedAwaitingUser) {
                continue;
            }
            var released = false;
            if (run.TryTransition(SequenceRunState.Paused, SequenceRunState.Running)) {
                _ = EmitAsync("sequence.resumed", id, run);
                WriteCheckpointIfOwner(run, id);
                released = true;
            } else if (run.State == SequenceRunState.Running) {
                // Pause was requested but the run never reached a boundary — the
                // disarm below genuinely un-pauses it, so it counts as released.
                released = true;
            }
            // Always disarm — also cancels a pause that was requested but never
            // reached a boundary; harmless on terminal runs (which don't count:
            // the returned figure feeds the safety.action_taken runs_resumed
            // payload and must not overstate — #731 review).
            run.Gate.Resume();
            if (released) {
                resumed++;
            }
        }
        return Task.FromResult(resumed);
    }

    public Task<int> SkipActiveRunsAsync(CancellationToken ct) {
        // §42.2 skip_target — daemon-automated (no NotifyUserActivity). Mirrors
        // SkipAsync's semantics per run: cancel the currently-running items so
        // the engine advances; a no-op for suspended/terminal runs.
        var skipped = 0;
        foreach (var id in _runs.Keys) {
            ct.ThrowIfCancellationRequested();
            if (!_runs.TryGetValue(id, out var run)) {
                continue;
            }
            if (run.State != SequenceRunState.Running) {
                continue;
            }
            run.Root?.SkipCurrentRunningItems();
            skipped++;
        }
        return Task.FromResult(skipped);
    }

    /// <summary>
    /// Whether a run can still be aborted: not terminal (Completed/Failed/Stopped) and not already in the
    /// transient Aborting state. The §29 disk-space monitor's "abort on critical" path uses this (via
    /// <see cref="AbortActiveRunsAsync"/>) so a re-entrant abort doesn't double-count an in-flight abort.
    /// Note: in-memory runs don't survive a daemon restart, so a post-restart Ok→Critical transition finds no
    /// active runs and aborts nothing.
    /// </summary>
    public static bool IsAbortableRun(SequenceRunState state) =>
        !IsTerminal(state) && state != SequenceRunState.Aborting;

    // IHostedService — explicit impl so it doesn't collide with the public
    // StartAsync(Guid, ...) above. On daemon shutdown, cancel every live run so
    // in-flight workers stop promptly instead of being abandoned mid-execution
    // (the §28.2 reconciler still classifies whatever checkpoint survives).
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) {
        var workers = new System.Collections.Generic.List<Task>();
        foreach (var run in _runs.Values) {
            if (IsTerminal(run.State)) continue;
            // Narrow race: a run that finished its execution this instant (worker
            // about to set Completed) gets marked Stopped here and reports
            // "stopped" instead of "complete". Acceptable at shutdown — the run
            // did stop, and the §28.2 reconciler (which keys off the checkpoint,
            // cleared by the worker's finally) is unaffected.
            run.State = SequenceRunState.Stopped;
            try {
                await run.Cts.CancelAsync();
            } catch (ObjectDisposedException) {
                // Worker reached terminal + disposed concurrently — already ending.
            }
            if (run.Worker is { } w) workers.Add(w);
        }
        if (workers.Count == 0) return;
        try {
            // Let the cancelled workers run their finally (checkpoint clear + CTS
            // dispose) so a clean stop leaves no leftover checkpoint — but never
            // past the host's shutdown deadline; the §28.2 reconciler is the
            // safety net for anything that doesn't finish in time. RunWorkerAsync
            // never throws (it catches everything), so WhenAll only completes.
            await Task.WhenAll(workers).WaitAsync(cancellationToken);
        } catch (OperationCanceledException) {
            // Host shutdown deadline hit — leave the rest to the §28.2 reconciler.
        }
    }

    private static bool IsTerminal(SequenceRunState s) =>
        s is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped;

    /// <summary>
    /// Atomically reserve the run slot for <paramref name="id"/>. Returns the
    /// owned <see cref="RunState"/> if this caller won (absent slot, or replacing
    /// a finished run), or null if a live run already owns it. A run built but not
    /// installed (lost the race) is disposed here so its CTS never leaks — and
    /// because the disposal stays local to this helper, the run the caller uses is
    /// provably never disposed.
    /// </summary>
    private RunState? TryReserveRun(Guid id) {
        var run = new RunState();
        var reserved = _runs.AddOrUpdate(id, run, (_, existing) => IsTerminal(existing.State) ? run : existing);
        if (ReferenceEquals(reserved, run)) {
            return run;
        }
        run.Dispose();
        return null;
    }

    /// <summary>
    /// Transition a live run to Aborting/Stopped and cancel its token. No-ops on
    /// an already-terminal run so we never touch a disposed CTS; the CancelAsync
    /// is still guarded against the narrow worker-disposes-during-abort race.
    /// </summary>
    // Returns true when this call actually transitioned the run (false when the run was missing or already
    // terminal) — so AbortActiveRunsAsync counts only runs it really stopped, not ones that finished naturally
    // in the TOCTOU window between the abortability check and here.
    private async Task<bool> RequestCancelAsync(Guid id, SequenceRunState desired) {
        // Also bail on an already-Aborting run: this closes the TOCTOU window where a concurrent abort (a user
        // AbortAsync or a second AbortActiveRunsAsync) sets Aborting between the caller's abortability check and
        // here — the losing writer returns false and isn't counted (no inflated "Sequence halted" tally). A
        // run heading to a terminal state via Aborting still terminates; re-labelling it adds nothing.
        if (!_runs.TryGetValue(id, out var run) || IsTerminal(run.State) || run.State == SequenceRunState.Aborting) return false;
        run.State = desired;
        try {
            await run.Cts.CancelAsync();
        } catch (ObjectDisposedException) {
            // Worker reached terminal + disposed the CTS between the check above
            // and here — the run is already ending, nothing more to cancel.
        }
        return true;
    }

    // Checkpoint ownership — only the run that currently owns active/current.json
    // writes or clears it, so concurrent runs (should they occur) can't stomp each
    // other's §28 checkpoint. The latest run to start takes ownership.
    private void ClaimCheckpoint(RunState run) {
        lock (_checkpointGate) { _checkpointOwner = run.RunId; }
    }

    private void WriteCheckpointIfOwner(RunState run, Guid sequenceId) {
        if (_checkpoint is null) return;
        lock (_checkpointGate) {
            if (_checkpointOwner != run.RunId) return;
            _checkpoint.Write(run.ToDto(sequenceId));
        }
    }

    private void ClearCheckpointIfOwner(RunState run) {
        if (_checkpoint is null) return;
        lock (_checkpointGate) {
            if (_checkpointOwner != run.RunId) return;
            _checkpoint.Clear();
            _checkpointOwner = Guid.Empty;
        }
    }

    /// <summary>Evict the oldest terminal runs once retention exceeds the cap (live runs are kept).</summary>
    private void PruneTerminalRuns() {
        var count = _runs.Count;
        if (count <= MaxRetainedRuns) return;
        var terminal = _runs.Where(kv => IsTerminal(kv.Value.State))
                            .OrderBy(kv => kv.Value.StartedUtc)
                            .ToList();
        var toRemove = count - MaxRetainedRuns;
        foreach (var kv in terminal) {
            if (toRemove-- <= 0) break;
            // Value-matching removal: only evict if this exact RunState is still
            // stored, so a same-id restart between the snapshot and here can't
            // delete the new live run.
            if (_runs.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, RunState>(kv.Key, kv.Value))) {
                kv.Value.Dispose();
            }
        }
    }

    /// <summary>
    /// Deserialize the body and present it as a runnable
    /// <see cref="ISequenceRootContainer"/>. A body whose top node is already a
    /// root container is used directly; any other container is wrapped in a fresh
    /// root so the <see cref="Sequencer"/> can drive it. Returns null when the
    /// body can't be deserialized.
    /// </summary>
    private ISequenceRootContainer? ToRootContainer(JsonElement body) {
        if (!_deserializer.TryDeserialize(body, out var container, out _) || container is null) {
            return null;
        }
        if (container is ISequenceRootContainer root) {
            return root;
        }
        var wrapper = new SequenceRootContainer();
        wrapper.Add(container);
        return wrapper;
    }

    // Flatten the container tree to its ordered leaf instructions (containers are
    // themselves ISequenceItems; only non-containers are real instructions).
    internal static List<ISequenceItem> CollectLeaves(ISequenceItem root) {
        var leaves = new List<ISequenceItem>();
        void Walk(ISequenceItem item) {
            if (item is ISequenceContainer c) {
                foreach (var child in c.GetItemsSnapshot()) {
                    Walk(child);
                }
            } else {
                leaves.Add(item);
            }
        }
        Walk(root);
        return leaves;
    }

    internal static int CountTerminalLeaves(List<ISequenceItem> leaves) {
        var n = 0;
        foreach (var leaf in leaves) {
            // DISABLED is "done" too: SequentialStrategy only runs CREATED items, so
            // a disabled leaf never executes and stays DISABLED — counting it keeps
            // instructions_completed consistent with instructions_total on a successful run.
            if (leaf.Status is SequenceEntityStatus.FINISHED or SequenceEntityStatus.FAILED
                            or SequenceEntityStatus.SKIPPED or SequenceEntityStatus.DISABLED) {
                n++;
            }
        }
        return n;
    }

    // The first RUNNING leaf — current_instruction_index is a single value. Under a
    // ParallelStrategy container several leaves can be RUNNING at once; this reports
    // the first and drops the rest (acceptable for the single-index API).
    internal static int? RunningLeafIndex(List<ISequenceItem> leaves) {
        for (var i = 0; i < leaves.Count; i++) {
            if (leaves[i].Status == SequenceEntityStatus.RUNNING) {
                return i;
            }
        }
        return null;
    }

    // Sequence execution is a background log-and-recover boundary: an instruction
    // can throw anything (hardware/SDK/parse/domain), and an escaped exception
    // must mark the run Failed + notify, never crash the daemon. CA1031 sanctions
    // a general catch at exactly this kind of boundary.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Top-level sequence-run boundary: arbitrary instruction code executes here, so any escaped exception must be caught, logged, surfaced as a Failed run via WS, and contained — letting it propagate would fault the background worker and take down the daemon. CA1031's 'boundary that logs and recovers' exception applies.")]
    private async Task RunWorkerAsync(Guid sequenceId, RunState run, ISequenceRootContainer root) {
        // Declared ahead of the try so the catch blocks can seal+drain it before
        // their terminal emits; null until the run body constructs it.
        CoalescingAsyncPublisher? progressPublisher = null;
        // Same hoisting for the §42.4 failure-report drain: the exception/cancellation
        // paths below must also scan-and-await before their terminal emits, or an
        // instruction_failed could land after sequence.failed/stopped (review finding).
        Func<Task>? drainFailureReports = null;
        // The run's §40 catalog session; null when no frame repository is wired
        // or its creation failed. Closed in the finally on every terminal path.
        Guid? captureSession = null;
        try {
            // If an abort/stop landed before this worker started (in the window
            // between StartAsync spawning it and it running), skip both the
            // execution AND the misleading "sequence.started" event — fall
            // straight through to the terminal (aborted/stopped) emit below.
            if (run.State is not (SequenceRunState.Aborting or SequenceRunState.Stopped)) {
                run.State = SequenceRunState.Running;
                // Flatten the tree once to the ordered leaf instructions; their
                // Status drives precise instructions_completed / current_instruction_index.
                // (instructions_total = leaf count, the same denominator we count against.)
                var leaves = CollectLeaves(root);
                run.UpdateProgress(leaves.Count, completed: 0, runningIndex: null);
                await EmitAsync("sequence.started", sequenceId, run);

                // §42.4 — sequence.instruction_failed: each leaf whose Status turns FAILED is
                // reported exactly once (Progress<T> callbacks can run concurrently, so the
                // scan-and-mark is gated). Scanned on every progress tick AND at the final
                // snapshot — a fast failure may finish without ever firing a tick. This is the
                // instruction-level failure channel; the §42.4 equipment.fault publishes cover
                // the mediator degrade paths that complete "successfully" at this level.
                var reportedFailed = new HashSet<int>();
                var failedGate = new object();
                var failureEmits = new List<Task>(); // every emit ever started, guarded by failedGate
                Task ReportNewFailuresAsync() {
                    // Mark + start + register under ONE lock acquisition: if the mark and the
                    // emit registration were separate critical sections, the terminal drain
                    // below could snapshot failureEmits in the gap and miss an emit that a
                    // tick had marked but not yet registered — instruction_failed would then
                    // land after the terminal event.
                    lock (failedGate) {
                        List<Task>? emits = null;
                        for (var i = 0; i < leaves.Count; i++) {
                            if (leaves[i].Status == SequenceEntityStatus.FAILED && reportedFailed.Add(i)) {
                                var name = leaves[i].Name;
                                var label = string.IsNullOrWhiteSpace(name) ? leaves[i].GetType().Name : name;
                                LogInstructionFailed(sequenceId, i, label);
                                (emits ??= []).Add(EmitInstructionFailedAsync(sequenceId, run, i, label));
                            }
                        }
                        if (emits is null) {
                            return Task.CompletedTask;
                        }
                        var all = Task.WhenAll(emits);
                        failureEmits.Add(all);
                        return all;
                    }
                }
                // The terminal-ordering barrier: one last scan, then await every emit any tick
                // ever started — a tick-path fire-and-forget that already MARKED a leaf could
                // otherwise still be publishing while the terminal event goes out.
                async Task DrainFailureReportsAsync() {
                    await ReportNewFailuresAsync();
                    Task[] pending;
                    lock (failedGate) {
                        pending = [.. failureEmits];
                    }
                    await Task.WhenAll(pending);
                }
                drainFailureReports = DrainFailureReportsAsync;

                // §60.9 progress back-pressure: equipment instructions (TakeExposure et al.)
                // report at capture rates, and the old per-tick fire-and-forget queued one
                // publish task per tick with nothing bounding the backlog. The coalescing
                // single-flight pump keeps at most ONE sequence.progress publish in flight
                // and collapses a burst into one trailing publish carrying the freshest run
                // state (read at publish time). Lifecycle events stay unthrottled.
                progressPublisher = new CoalescingAsyncPublisher(
                    () => EmitAsync("sequence.progress", sequenceId, run));
                var progress = new Progress<ApplicationStatus>(status => {
                    run.SetDescription(status.Status);
                    run.UpdateProgress(leaves.Count, CountTerminalLeaves(leaves), RunningLeafIndex(leaves));
                    progressPublisher.Poke();
                    _ = ReportNewFailuresAsync(); // fire-and-forget on ticks; the emit never throws
                    WriteCheckpointIfOwner(run, sequenceId);
                });

                // §40/§50 — this run owns its own catalog session: frames its
                // instructions capture land grouped per-run instead of in the
                // shared manual bucket. The id rides CaptureSessionScope
                // (AsyncLocal), flowing through the whole awaited instruction
                // chain into the camera's frame-register step with no mediator
                // interface changes — and concurrent runs each see their own.
                // Catalog trouble must never block imaging: creation failure
                // logs and the run proceeds session-less (manual fallback).
                captureSession = await TryOpenRunSessionAsync(run.Cts.Token);
                if (captureSession is Guid sid) {
                    CaptureSessionScope.Enter(sid);
                    await EmitSessionAsync("session.started", sid);
                }

                // Real execution. Sequencer.Start runs MainContainer.Run with
                // Initialize/Teardown and swallows OperationCanceledException, so
                // cancellation surfaces via run.Cts below rather than as a throw.
                var sequencer = new OpenAstroAra.Sequencer.Sequencer(root);
                await sequencer.Start(progress, skipIssuePrompt: true, run.Cts.Token);

                // Final snapshot — fast instructions may finish without firing a
                // progress tick, so settle the completed count after the run.
                run.UpdateProgress(leaves.Count, CountTerminalLeaves(leaves), runningIndex: null);
                // Awaited (unlike the tick-path scans) so every instruction_failed for this
                // run is published before its terminal event below.
                await DrainFailureReportsAsync();

                // r1 ordering: seal (late queued Progress<T> ticks become no-ops — the
                // #648 lesson) and drain any in-flight/trailing progress publish BEFORE
                // the terminal event below, so sequence.progress can never arrive after
                // sequence.complete/stopped/aborted for this run.
                await progressPublisher.SealAndDrainAsync();
            }

            // Terminal transition is driven by the state Abort/Stop set before
            // cancelling (Sequencer.Start swallows OperationCanceledException, so
            // it returns normally on cancel). No discrete "Aborted" state — the
            // DTO enum folds abort + stop into Stopped; the WS event type carries
            // the distinction.
            if (run.State == SequenceRunState.Aborting) {
                run.State = SequenceRunState.Stopped;
                await EmitAsync("sequence.aborted", sequenceId, run);
            } else if (run.State == SequenceRunState.Stopped) {
                await EmitAsync("sequence.stopped", sequenceId, run);
            } else if (run.Cts.IsCancellationRequested) {
                // Defensive: cancelled but the Aborting/Stopped marker was lost
                // to a race — treat as a stop rather than reporting completion.
                run.State = SequenceRunState.Stopped;
                await EmitAsync("sequence.stopped", sequenceId, run);
            } else {
                // Set CompletedUtc (under the lock) before the volatile State
                // write, so a reader that observes Completed always also sees
                // CompletedUtc populated — never a Completed/null transient.
                run.MarkCompleted();
                run.State = SequenceRunState.Completed;
                await EmitAsync("sequence.complete", sequenceId, run);
            }
        } catch (OperationCanceledException) {
            // Today NINA's Sequencer.Start swallows OCE and returns normally, so
            // the terminal block above handles abort/stop. This guard keeps the
            // engine correct if a Sequencer subclass / future NINA version ever
            // lets OCE escape — a cancelled run is reported Stopped, not Failed.
            if (progressPublisher is not null) {
                await progressPublisher.SealAndDrainAsync();
            }
            if (drainFailureReports is not null) {
                await drainFailureReports();
            }
            run.State = SequenceRunState.Stopped;
            await EmitAsync("sequence.stopped", sequenceId, run);
        } catch (Exception ex) {
            LogRunFailed(ex, sequenceId);
            if (progressPublisher is not null) {
                await progressPublisher.SealAndDrainAsync();
            }
            if (drainFailureReports is not null) {
                await drainFailureReports();
            }
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", sequenceId, run);
        } finally {
            if (captureSession is Guid endSid) {
                // Scope off first (a late fire-and-forget register outside the
                // scope falls back to manual rather than a closed run session),
                // then stamp the end time — on EVERY terminal path.
                CaptureSessionScope.Exit();
                await TryEndRunSessionAsync(endSid);
                // §48.1 — a COMPLETED run with a "capture tonight" answer kicks off
                // the end-of-session flats (generated from this very session's
                // frames via the §39.5 machinery). Aborted/stopped/failed runs
                // never do: the user ended the night on their own terms.
                if (run.State == SequenceRunState.Completed && run.AutoFlatsChoice is { } flatsChoice) {
                    _ = Task.Run(() => ExecuteAutoFlatsAsync(sequenceId, endSid, flatsChoice), CancellationToken.None);
                }
            }
            // §28 — terminal transition clears active/current.json (only if this
            // run still owns it); a missing file is the canonical "nothing
            // running" signal for §28.2.
            ClearCheckpointIfOwner(run);
            // The run is terminal: dispose its CTS (the only IDisposable). The
            // RunState record stays in _runs for post-completion GetRunState
            // polling; Abort/Stop guard against touching a disposed CTS, and
            // PruneTerminalRuns bounds retention.
            run.DisposeCts();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The catalog is best-effort from the run's perspective: a sessions-table fault must log and leave the run imaging (frames fall back to the manual bucket), never fail or delay execution. CA1031's log-and-recover boundary applies.")]
    private async Task<Guid?> TryOpenRunSessionAsync(CancellationToken ct) {
        if (_frames is null) return null;
        try {
            return await _frames.CreateRunSessionAsync(ct);
        } catch (Exception ex) {
            LogRunSessionFailed(ex);
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Same best-effort catalog boundary as TryOpenRunSessionAsync, on the teardown side.")]
    private async Task TryEndRunSessionAsync(Guid sessionId) {
        try {
            await _frames!.EndSessionAsync(sessionId, CancellationToken.None);
            await EmitSessionAsync("session.ended", sessionId);
        } catch (Exception ex) {
            LogRunSessionFailed(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must not affect the run. Same boundary as EmitAsync.")]
    private async Task EmitSessionAsync(string eventType, Guid sessionId) {
        if (_ws is null) return;
        try {
            var payload = new JsonObject { ["session_id"] = sessionId.ToString() };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // Best-effort; the in-catalog session is the source of truth.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort and EmitAsync is called fire-and-forget from the progress callback (_ = EmitAsync(...)); ANY failure from a custom IWsBroadcaster (e.g. SocketException) must be swallowed here so it cannot surface as an unobserved task exception or affect the run. CA1031's 'boundary that logs and recovers' exception applies.")]
    private async Task EmitAsync(string eventType, Guid sequenceId, RunState run) {
        if (_ws is null) return;
        try {
            // Built with JsonObject (not string interpolation) so values are
            // always correctly escaped — robust even if a user-supplied string
            // field is added to the payload later.
            var payload = new JsonObject {
                ["sequence_id"] = sequenceId.ToString(),
                ["run_id"] = run.RunId.ToString(),
                ["state"] = run.State.ToString().ToLowerInvariant(),
                ["current_instruction_index"] = run.CurrentInstructionIndex,
                ["instructions_completed"] = run.InstructionsCompleted,
                ["instructions_total"] = run.InstructionCount,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // WS is best-effort; a failed publish must not affect the run.
        }
    }

    // §42.4 — the instruction-level failure event. Same payload as the run events plus the
    // failed leaf's index/name, so WILMA can pin the failure to a row without diffing statuses.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: a broadcaster/serialization fault must never affect the run — the leaf's FAILED status and the log line already carry the failure. CA1031's log-and-recover boundary applies.")]
    private async Task EmitInstructionFailedAsync(Guid sequenceId, RunState run, int index, string name) {
        if (_ws is null) return;
        try {
            var payload = new JsonObject {
                ["sequence_id"] = sequenceId.ToString(),
                ["run_id"] = run.RunId.ToString(),
                ["state"] = run.State.ToString().ToLowerInvariant(),
                ["failed_instruction_index"] = index,
                ["failed_instruction_name"] = name,
                ["instructions_completed"] = run.InstructionsCompleted,
                ["instructions_total"] = run.InstructionCount,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.SequenceInstructionFailed, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // WS is best-effort; a failed publish must not affect the run.
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Sequence run {SequenceId}: instruction {Index} ('{Name}') failed — sequence.instruction_failed published (§42.4)")]
    private partial void LogInstructionFailed(Guid sequenceId, int index, string name);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Sequence run {SequenceId} failed")]
    private partial void LogRunFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§40 run-session catalog operation failed — the run continues; frames fall back to the manual session")]
    private partial void LogRunSessionFailed(Exception ex);

    private sealed class RunState : IDisposable {
        // State is written from Abort/Stop (request threads), the background
        // worker, AND the pause-gate callback; int-backed so transitions that must
        // not clobber a concurrent abort (Running→Paused, Paused→Running) can CAS.
        private int _state = (int)SequenceRunState.Starting;
        // The remaining telemetry fields are written on the worker and read via
        // ToDto on request threads (GetRunState / checkpoint). _gate guards both
        // sides so a request thread always sees a consistent snapshot.
        private readonly object _gate = new();

        public Guid RunId { get; } = Guid.NewGuid();

        public SequenceRunState State {
            get => (SequenceRunState)Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, (int)value);
        }

        /// <summary>
        /// Atomically transition <paramref name="from"/> → <paramref name="to"/>;
        /// false when another writer got there first. Used by the pause paths so
        /// entering/leaving Paused can never clobber a concurrent Abort/Stop
        /// (whose unconditional write always wins over a failed CAS).
        /// </summary>
        public bool TryTransition(SequenceRunState from, SequenceRunState to) =>
            Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

        /// <summary>
        /// §38 pause — the run's instruction-boundary gate, attached to the root
        /// container before execution and driven by Pause/Resume.
        /// </summary>
        public OpenAstroAra.Sequencer.Utility.PauseGate Gate { get; } = new();
        // §48.1 — the run's "capture calibration tonight?" answer:
        // "panel_at_end" | "sky_at_twilight" | null (later / undecided / never).
        // Volatile: written by the start path / the HTTP decide handler, read by
        // the worker at completion (#735 review — cross-thread visibility).
        private string? _autoFlatsChoice;
        public string? AutoFlatsChoice {
            get => Volatile.Read(ref _autoFlatsChoice);
            set => Volatile.Write(ref _autoFlatsChoice, value);
        }
        public int? CurrentInstructionIndex { get; private set; }   // wired with instruction-level hooks (deferred)
        public string? CurrentInstructionDescription { get; private set; }
        public int InstructionsCompleted { get; private set; }            // wired with instruction-level hooks (deferred)
        // Filled in after the body loads (StartAsync reserves the run slot before
        // the async body read, so the count isn't known at construction). Set once
        // before the run becomes observable; an int read/write is atomic.
        public int InstructionCount { get; private set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; private set; }
        public CancellationTokenSource Cts { get; } = new();
        // The RunWorkerAsync task, so shutdown can await teardown. volatile: written
        // on the request thread (StartAsync) and read on the shutdown thread.
        private volatile Task? _worker;
        public Task? Worker { get => _worker; set => _worker = value; }

        // The running root container, so live controls (skip-current) can reach it. Set on the
        // worker once the body loads; read on request threads — guarded by _gate.
        private ISequenceRootContainer? _root;
        public ISequenceRootContainer? Root { get { lock (_gate) { return _root; } } }
        public void SetRoot(ISequenceRootContainer? root) { lock (_gate) { _root = root; } }

        public void SetDescription(string? description) {
            if (string.IsNullOrEmpty(description)) return;
            lock (_gate) { CurrentInstructionDescription = description; }
        }

        /// <summary>Set the instruction total + completed count + current index as one consistent snapshot.</summary>
        public void UpdateProgress(int total, int completed, int? runningIndex) {
            lock (_gate) {
                InstructionCount = total;
                InstructionsCompleted = Math.Min(completed, total); // clamp: a leaf subset count can't exceed the total
                CurrentInstructionIndex = runningIndex;
            }
        }

        public void MarkCompleted() {
            lock (_gate) { CompletedUtc = DateTimeOffset.UtcNow; }
        }

        private int _ctsDisposed;
        // Dispose the CTS exactly once — it's reached from both the worker's
        // finally (terminal) and PruneTerminalRuns eviction. CTS.Dispose is
        // documented idempotent, but the guard makes that explicit + cheap.
        private void DisposeCtsOnce() {
            if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0) {
                Cts.Dispose();
            }
        }

        /// <summary>Dispose the CTS once — the record itself stays queryable in _runs.</summary>
        public void DisposeCts() => DisposeCtsOnce();

        public void Dispose() => DisposeCtsOnce();

        public SequenceRunStateDto ToDto(Guid sequenceId) {
            lock (_gate) {
                return new(
                    SequenceId: sequenceId,
                    RunId: RunId,
                    State: State,
                    CurrentInstructionIndex: CurrentInstructionIndex,
                    CurrentTargetName: null,
                    StartedUtc: StartedUtc,
                    CompletedUtc: CompletedUtc,
                    InstructionsCompleted: InstructionsCompleted,
                    InstructionsTotal: InstructionCount,
                    CurrentInstructionDescription: CurrentInstructionDescription);
            }
        }
    }
}
