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
public sealed partial class SequencerService : ISequencerService {

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

    public async Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) {
        // Atomically reserve the run slot BEFORE any async work. Two concurrent
        // starts for the same id otherwise both pass a separate guard and both
        // spawn a worker (TOCTOU). AddOrUpdate is atomic per key: an absent slot
        // takes this fresh run; a live (non-terminal) run keeps the slot; a
        // finished run is replaced (restart allowed). We own the run iff the
        // returned reference is ours.
        var run = TryReserveRun(id);
        if (run is null) {
            // A live run already owns the slot — idempotent.
            return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
        }
        PruneTerminalRuns();

        // We own the slot. Load + deserialize the saved body; frames-total comes
        // from the instruction count so WILMA can size its progress UI. Set the
        // count only once we have a runnable root, so a failed deserialize leaves
        // a Failed run with FramesTotal 0 rather than a misleading non-zero total.
        ISequenceRootContainer? root = null;
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is not null) {
            var dto = await sequences.GetAsync(id, ct);
            if (dto is not null) {
                root = ToRootContainer(dto.Body);
                if (root is not null) {
                    run.InstructionCount = SequenceBodyInspector.Inspect(dto.Body).InstructionCount;
                }
            }
        }

        if (root is null) {
            // No body / undeserializable: fail the run immediately rather than
            // spin a worker over nothing. The body validity is normally enforced
            // by the §38.5 validator at persist time.
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", id, run);
            return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
        }

        // §28 — checkpoint from start-of-run so the §28.2 reconciler can spot an
        // interrupted run, not just one that got past its first instruction.
        _checkpoint?.Write(run.ToDto(id));
        _ = Task.Run(() => RunWorkerAsync(id, run, root), CancellationToken.None);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
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
            if (_runs.TryRemove(kv.Key, out var removed)) removed.Dispose();
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
                    _checkpoint?.Write(run.ToDto(sequenceId));
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
                run.State = SequenceRunState.Completed;
                run.MarkCompleted();
                await EmitAsync("sequence.complete", sequenceId, run);
            }
        } catch (Exception ex) {
            LogRunFailed(ex, sequenceId);
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", sequenceId, run);
        } finally {
            // §28 — terminal transition clears active/current.json; a missing
            // file is the canonical "nothing running" signal for §28.2.
            _checkpoint?.Clear();
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

        public void SetDescription(string? description) {
            if (string.IsNullOrEmpty(description)) return;
            lock (_gate) { CurrentInstructionDescription = description; }
        }

        public void MarkCompleted() {
            lock (_gate) { CompletedUtc = DateTimeOffset.UtcNow; }
        }

        /// <summary>Dispose the CTS (idempotent) — the record itself stays queryable in _runs.</summary>
        public void DisposeCts() => Cts.Dispose();

        public void Dispose() => Cts.Dispose();

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
