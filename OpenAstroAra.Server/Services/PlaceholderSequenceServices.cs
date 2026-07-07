#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.IO;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.13 — placeholder <see cref="ISequencerService"/> for §38
/// runtime control. GetRunState returns null (no run in progress);
/// every action returns 202 OperationAccepted with a sequencer-prefixed
/// operation_type. Real impl wraps the legacy NINA <c>ISequencer</c>
/// with a thread-safe lifecycle worker that emits §60.9 WS events.
/// </summary>
/// <summary>
/// §38 mock sequencer. No real equipment yet, but the run-state machine
/// + WS event emission are realistic so WILMA's sequencer UI can exercise
/// the full progression: idle → running → (paused →) running → complete.
/// Each "instruction" takes 1 second — gives the UI a visible progress
/// signal without monopolizing the daemon. Real equipment driving lands
/// when Alpaca driver placeholders get swapped to real impls.
/// </summary>
public sealed class PlaceholderSequencerService : ISequencerService {
    /// <summary>Default instruction count when no sequence body is found (DI-test paths).</summary>
    private const int DefaultMockInstructionCount = 5;
    private const int MockInstructionDurationMs = 1000;

    private readonly IWsBroadcaster? _ws;
    // Lazy resolver — both this service AND FileSequenceService now reference
    // each other (ISequenceService.ListAsync surfaces run state per §38j-4;
    // ISequencerService.StartAsync reads instruction count per §38j-5). Direct
    // constructor injection would deadlock the DI container; the Func defers
    // resolution to call time.
    private readonly Func<ISequenceService?>? _sequencesResolver;
    private readonly ActiveSequenceCheckpoint? _checkpoint;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RunState> _runs = new();

    public PlaceholderSequencerService(IWsBroadcaster? ws = null)
        : this(ws, sequencesResolver: null, checkpoint: null) { }

    public PlaceholderSequencerService(
            IWsBroadcaster? ws,
            Func<ISequenceService?>? sequencesResolver,
            ActiveSequenceCheckpoint? checkpoint = null) {
        _ws = ws;
        _sequencesResolver = sequencesResolver;
        _checkpoint = checkpoint;
    }

    public Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(id, out var run) ? run.ToDto(id) : null);

    public async Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) {
        var existing = _runs.GetValueOrDefault(id);
        if (existing is not null && existing.State is SequenceRunState.Running or SequenceRunState.Paused or SequenceRunState.Starting) {
            // Already running — return the same operation id rather than
            // spawning a second worker.
            return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
        }

        // §38j-5 — inspect the saved sequence body so the mock progresses
        // through the real instruction count instead of a hardcoded 5.
        // Falls back to the mock default when no stored body exists (lets
        // unit tests construct PlaceholderSequencerService without ISequenceService).
        var instructionCount = DefaultMockInstructionCount;
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is not null) {
            var dto = await sequences.GetAsync(id, ct);
            if (dto is not null) {
                var stats = SequenceBodyInspector.Inspect(dto.Body);
                if (stats.InstructionCount > 0) instructionCount = stats.InstructionCount;
            }
        }

        var run = new RunState(instructionCount);
        _runs[id] = run;
        // §38j-6 — write the checkpoint immediately so the active/current.json
        // exists from start-of-run, not just after the first instruction
        // completes. The §28.2 reconciler can spot interrupted runs.
        _checkpoint?.Write(run.ToDto(id));
        _ = Task.Run(() => RunWorkerAsync(id, run), CancellationToken.None);
        return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
    }

    public Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run) && run.State == SequenceRunState.Running) {
            run.State = SequenceRunState.Paused;
            _ = EmitAsync("sequence.paused", id, run);
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", idempotencyKey));
    }

    public Task<OperationAcceptedDto> SkipAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        // Placeholder engine has no running container to skip; Accepted no-op.
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.skip", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run) && run.State == SequenceRunState.Paused) {
            run.State = SequenceRunState.Running;
            _ = EmitAsync("sequence.resumed", id, run);
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.resume", idempotencyKey));
    }

    public async Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run)) {
            run.State = SequenceRunState.Aborting;
            await run.Cts.CancelAsync();
        }
        return PlaceholderEquipmentHelpers.Accepted("sequencer.abort", idempotencyKey);
    }

    public async Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run)) {
            run.State = SequenceRunState.Stopped;
            await run.Cts.CancelAsync();
        }
        return PlaceholderEquipmentHelpers.Accepted("sequencer.stop", idempotencyKey);
    }

    public async Task<int> AbortActiveRunsAsync(CancellationToken ct) {
        var aborted = 0;
        foreach (var (_, run) in _runs) {
            ct.ThrowIfCancellationRequested();
            // Shared abortability rule (skips terminal + already-Aborting) so this can't diverge from the real
            // SequencerService if its terminal-state set changes.
            if (!SequencerService.IsAbortableRun(run.State)) {
                continue;
            }
            run.State = SequenceRunState.Aborting;
            await run.Cts.CancelAsync();
            aborted++;
        }
        return aborted;
    }

    public Task<IReadOnlyList<Guid>> PauseActiveRunsAsync(CancellationToken ct) {
        var paused = new List<Guid>();
        foreach (var (id, run) in _runs) {
            ct.ThrowIfCancellationRequested();
            if (run.State == SequenceRunState.Running) {
                run.State = SequenceRunState.Paused;
                paused.Add(id);
            }
        }
        return Task.FromResult<IReadOnlyList<Guid>>(paused);
    }

    public Task<int> ResumeRunsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) {
        var resumed = 0;
        foreach (var id in ids) {
            ct.ThrowIfCancellationRequested();
            if (_runs.TryGetValue(id, out var run) && run.State == SequenceRunState.Paused) {
                run.State = SequenceRunState.Running;
                resumed++;
            }
        }
        return Task.FromResult(resumed);
    }

    private async Task RunWorkerAsync(Guid sequenceId, RunState run) {
        try {
            run.State = SequenceRunState.Running;
            await EmitAsync("sequence.started", sequenceId, run);
            for (var i = 0; i < run.InstructionCount; i++) {
                if (run.Cts.IsCancellationRequested) break;
                while (run.State == SequenceRunState.Paused && !run.Cts.IsCancellationRequested) {
                    try { await Task.Delay(100, run.Cts.Token); } catch (OperationCanceledException) { break; }
                }
                if (run.Cts.IsCancellationRequested) break;

                run.CurrentInstructionIndex = i;
                run.CurrentInstructionDescription = $"capture #{i + 1}";
                await EmitAsync("sequence.instruction_started", sequenceId, run);

                try { await Task.Delay(MockInstructionDurationMs, run.Cts.Token); } catch (OperationCanceledException) { break; }

                run.InstructionsCompleted = i + 1;
                await EmitAsync("sequence.instruction_complete", sequenceId, run);
                await EmitAsync("sequence.progress", sequenceId, run);
                // §38j-6 — refresh the active/current.json on each progress
                // step so a daemon crash leaves a recent snapshot for the
                // §28.2 reconciler.
                _checkpoint?.Write(run.ToDto(sequenceId));
            }
            // No discrete "Aborted" state — `Stopped` covers both abort
            // and stop per the DTO enum. Source event-type differs by
            // whether the transition started in Aborting vs Stopped.
            if (run.State == SequenceRunState.Aborting) {
                run.State = SequenceRunState.Stopped;
                await EmitAsync("sequence.aborted", sequenceId, run);
            } else if (run.State == SequenceRunState.Stopped) {
                await EmitAsync("sequence.stopped", sequenceId, run);
            } else {
                run.State = SequenceRunState.Completed;
                run.CompletedUtc = DateTimeOffset.UtcNow;
                await EmitAsync("sequence.complete", sequenceId, run);
            }
        } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or JsonException) {
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", sequenceId, run);
        } finally {
            // §38j-6 — terminal transition clears active/current.json (a
            // missing file is the canonical "nothing was running" signal
            // for §28.2 reconciliation).
            _checkpoint?.Clear();
        }
    }

    private async Task EmitAsync(string eventType, Guid sequenceId, RunState run) {
        if (_ws is null) return;
        try {
            var json = $$"""
                {"sequence_id":"{{sequenceId}}","run_id":"{{run.RunId}}","state":"{{run.State.ToString().ToLowerInvariant()}}","current_instruction_index":{{(run.CurrentInstructionIndex.HasValue ? run.CurrentInstructionIndex.Value.ToString() : "null")}},"instructions_completed":{{run.InstructionsCompleted}},"instructions_total":{{run.InstructionCount}}}
                """;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ObjectDisposedException) {
            // WS best-effort.
        }
    }

    private sealed class RunState {
        public Guid RunId { get; } = Guid.NewGuid();
        public SequenceRunState State { get; set; } = SequenceRunState.Starting;
        public int? CurrentInstructionIndex { get; set; }
        public string? CurrentInstructionDescription { get; set; }
        public int InstructionsCompleted { get; set; }
        public int InstructionCount { get; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        public RunState(int instructionCount) {
            InstructionCount = instructionCount;
        }

        public SequenceRunStateDto ToDto(Guid sequenceId) => new(
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