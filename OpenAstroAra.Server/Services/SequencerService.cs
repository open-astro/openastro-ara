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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
/// Deferred (tracked in PORT_TODO): <see cref="PauseAsync"/> /
/// <see cref="ResumeAsync"/> — the headless engine has no pause hook (NINA's
/// pause was WPF-coupled and stripped), so true mid-run suspension needs an
/// instruction-boundary pause gate added to the execution loop. Until then these
/// are accepted no-ops rather than faking a "paused" state the run wouldn't honor.
/// </summary>
public sealed partial class SequencerService : ISequencerService, IHostedService {

    private readonly IWsBroadcaster? _ws;
    // Lazy resolver — FileSequenceService (ISequenceService) and this service
    // reference each other (ListAsync surfaces run state per §38j-4; StartAsync
    // reads the stored body). A Func<> defers resolution past the DI cycle.
    private readonly Func<ISequenceService?>? _sequencesResolver;
    private readonly ActiveSequenceCheckpoint? _checkpoint;
    private readonly SequenceBodyDeserializer _deserializer;
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
            ILogger<SequencerService>? logger = null) {
        _deserializer = deserializer;
        _ws = ws;
        _sequencesResolver = sequencesResolver;
        _checkpoint = checkpoint;
        _logger = logger ?? NullLogger<SequencerService>.Instance;
    }

    public Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(id, out var run) ? run.ToDto(id) : null);

    public Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) {
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
        run.Worker = Task.Run(() => LoadAndRunAsync(id, run), CancellationToken.None);
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
        await RunWorkerAsync(id, run, root);
    }

    private async Task<ISequenceRootContainer?> LoadRootAsync(Guid id, RunState run) {
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is null) return null;
        // Use the run's own token (the request CT is gone once StartAsync
        // returned), so an abort during load still cancels it.
        var dto = await sequences.GetAsync(id, run.Cts.Token);
        if (dto is null) return null;
        var root = ToRootContainer(dto.Body);
        if (root is not null) {
            // frames-total comes from the instruction count so WILMA can size its
            // progress UI; set only once we have a runnable root.
            run.InstructionCount = SequenceBodyInspector.Inspect(dto.Body).InstructionCount;
        }
        return root;
    }

    public Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        // Deferred: no pause hook in the headless engine yet (see class remarks).
        // Accepted no-op — deliberately does NOT emit a "paused" event the run
        // would not actually honor.
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.resume", idempotencyKey));

    public async Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        await RequestCancelAsync(id, SequenceRunState.Aborting);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.abort", idempotencyKey);
    }

    public async Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        await RequestCancelAsync(id, SequenceRunState.Stopped);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.stop", idempotencyKey);
    }

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
    private async Task RequestCancelAsync(Guid id, SequenceRunState desired) {
        if (!_runs.TryGetValue(id, out var run) || IsTerminal(run.State)) return;
        run.State = desired;
        try {
            await run.Cts.CancelAsync();
        } catch (ObjectDisposedException) {
            // Worker reached terminal + disposed the CTS between the check above
            // and here — the run is already ending, nothing more to cancel.
        }
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

    // Sequence execution is a background log-and-recover boundary: an instruction
    // can throw anything (hardware/SDK/parse/domain), and an escaped exception
    // must mark the run Failed + notify, never crash the daemon. CA1031 sanctions
    // a general catch at exactly this kind of boundary.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Top-level sequence-run boundary: arbitrary instruction code executes here, so any escaped exception must be caught, logged, surfaced as a Failed run via WS, and contained — letting it propagate would fault the background worker and take down the daemon. CA1031's 'boundary that logs and recovers' exception applies.")]
    private async Task RunWorkerAsync(Guid sequenceId, RunState run, ISequenceRootContainer root) {
        try {
            // If an abort/stop landed before this worker started (in the window
            // between StartAsync spawning it and it running), skip both the
            // execution AND the misleading "sequence.started" event — fall
            // straight through to the terminal (aborted/stopped) emit below.
            if (run.State is not (SequenceRunState.Aborting or SequenceRunState.Stopped)) {
                run.State = SequenceRunState.Running;
                await EmitAsync("sequence.started", sequenceId, run);

                var progress = new Progress<ApplicationStatus>(status => {
                    run.SetDescription(status.Status);
                    _ = EmitAsync("sequence.progress", sequenceId, run);
                    WriteCheckpointIfOwner(run, sequenceId);
                });

                // Real execution. Sequencer.Start runs MainContainer.Run with
                // Initialize/Teardown and swallows OperationCanceledException, so
                // cancellation surfaces via run.Cts below rather than as a throw.
                var sequencer = new OpenAstroAra.Sequencer.Sequencer(root);
                await sequencer.Start(progress, skipIssuePrompt: true, run.Cts.Token);
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
            run.State = SequenceRunState.Stopped;
            await EmitAsync("sequence.stopped", sequenceId, run);
        } catch (Exception ex) {
            LogRunFailed(ex, sequenceId);
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", sequenceId, run);
        } finally {
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
                ["frames_completed"] = run.FramesCompleted,
                ["frames_total"] = run.InstructionCount,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // WS is best-effort; a failed publish must not affect the run.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sequence run {SequenceId} failed")]
    private partial void LogRunFailed(Exception ex, Guid sequenceId);

    private sealed class RunState : IDisposable {
        // State is written from Abort/Stop (request threads) and the background
        // worker; volatile makes the cross-thread visibility explicit.
        private volatile SequenceRunState _state = SequenceRunState.Starting;
        // The remaining telemetry fields are written on the worker and read via
        // ToDto on request threads (GetRunState / checkpoint). _gate guards both
        // sides so a request thread always sees a consistent snapshot.
        private readonly object _gate = new();

        public Guid RunId { get; } = Guid.NewGuid();
        public SequenceRunState State { get => _state; set => _state = value; }
        public int? CurrentInstructionIndex { get; private set; }   // wired with instruction-level hooks (deferred)
        public string? CurrentInstructionDescription { get; private set; }
        public int FramesCompleted { get; private set; }            // wired with instruction-level hooks (deferred)
        // Filled in after the body loads (StartAsync reserves the run slot before
        // the async body read, so the count isn't known at construction). Set once
        // before the run becomes observable; an int read/write is atomic.
        public int InstructionCount { get; set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; private set; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Worker { get; set; }   // the RunWorkerAsync task, so shutdown can await teardown

        public void SetDescription(string? description) {
            if (string.IsNullOrEmpty(description)) return;
            lock (_gate) { CurrentInstructionDescription = description; }
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
                    State: _state,
                    CurrentInstructionIndex: CurrentInstructionIndex,
                    CurrentTargetName: null,
                    StartedUtc: StartedUtc,
                    CompletedUtc: CompletedUtc,
                    FramesCompleted: FramesCompleted,
                    FramesTotal: InstructionCount,
                    CurrentInstructionDescription: CurrentInstructionDescription);
            }
        }
    }
}
