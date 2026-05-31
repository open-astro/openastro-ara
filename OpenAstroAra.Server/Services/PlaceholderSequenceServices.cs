#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.13 — placeholder <see cref="ISequenceService"/> covering the
/// §38 sequence CRUD surface. Backed by an in-memory dictionary so
/// create/update/delete actually round-trip during a single daemon
/// lifetime (resets on restart). Real §28-DB-backed impl + the §38
/// sequence orchestrator land in the real-infra phase (post-§60.9).
/// </summary>
public sealed class PlaceholderSequenceService : ISequenceService {
    private readonly object _lock = new();
    private readonly Dictionary<Guid, SequenceDto> _sequences = new();

    public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        lock (_lock) {
            var items = _sequences.Values
                .OrderByDescending(s => s.ModifiedUtc)
                .Take(Math.Max(1, limit))
                .Select(s => new SequenceListItemDto(
                    s.Id, s.Name, s.Description, s.CreatedUtc, s.ModifiedUtc,
                    CurrentRunState: null,
                    InstructionCount: 0,
                    TargetCount: 0,
                    TemplateOrigin: s.TemplateOrigin))
                .ToList();
            return Task.FromResult(new CursorPage<SequenceListItemDto>(items, NextCursor: null, HasMore: false));
        }
    }

    public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            return Task.FromResult<SequenceDto?>(_sequences.TryGetValue(id, out var seq) ? seq : null);
        }
    }

    public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) {
        var now = DateTimeOffset.UtcNow;
        var dto = new SequenceDto(
            Id: Guid.NewGuid(),
            Name: request.Name,
            Description: request.Description,
            CreatedUtc: now,
            ModifiedUtc: now,
            Body: request.Body,
            TemplateOrigin: request.TemplateOrigin);
        lock (_lock) { _sequences[dto.Id] = dto; }
        return Task.FromResult(dto);
    }

    public Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) {
        lock (_lock) {
            if (!_sequences.TryGetValue(id, out var existing)) return Task.FromResult<SequenceDto?>(null);
            var updated = existing with {
                Name = request.Name ?? existing.Name,
                Description = request.Description ?? existing.Description,
                Body = request.Body ?? existing.Body,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };
            _sequences[id] = updated;
            return Task.FromResult<SequenceDto?>(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) {
        lock (_lock) { return Task.FromResult(_sequences.Remove(id)); }
    }

    public Task<SequenceShareDto> ShareExportAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            if (!_sequences.TryGetValue(id, out var existing)) {
                // Real impl would 404; the endpoint catches null returns,
                // but ShareExportAsync's contract is non-nullable. Return
                // a synthetic share for unknown ids (placeholder semantic).
                existing = new SequenceDto(
                    Id: id, Name: "Unknown sequence", Description: null,
                    CreatedUtc: DateTimeOffset.UtcNow, ModifiedUtc: DateTimeOffset.UtcNow,
                    Body: JsonDocument.Parse("{}").RootElement.Clone(),
                    TemplateOrigin: null);
            }
            return Task.FromResult(new SequenceShareDto(
                SequenceId: existing.Id,
                SequenceName: existing.Name,
                ShareFormat: "openastroara.v1",
                Manifest: existing.Body,
                PayloadBytes: 4_096,
                DownloadUrl: $"/api/v1/sequences/{existing.Id}/share/payload"));
        }
    }
}

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
    private const int MockInstructionCount = 5;
    private const int MockInstructionDurationMs = 1000;

    private readonly IWsBroadcaster? _ws;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RunState> _runs = new();

    public PlaceholderSequencerService(IWsBroadcaster? ws = null) {
        _ws = ws;
    }

    public Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(id, out var run) ? run.ToDto(id) : null);

    public Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) {
        var existing = _runs.GetValueOrDefault(id);
        if (existing is not null && existing.State is SequenceRunState.Running or SequenceRunState.Paused or SequenceRunState.Starting) {
            // Already running — return the same operation id rather than
            // spawning a second worker.
            return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey));
        }
        var run = new RunState();
        _runs[id] = run;
        _ = Task.Run(() => RunWorkerAsync(id, run));
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey));
    }

    public Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run) && run.State == SequenceRunState.Running) {
            run.State = SequenceRunState.Paused;
            _ = EmitAsync("sequence.paused", id, run);
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run) && run.State == SequenceRunState.Paused) {
            run.State = SequenceRunState.Running;
            _ = EmitAsync("sequence.resumed", id, run);
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.resume", idempotencyKey));
    }

    public Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run)) {
            run.State = SequenceRunState.Aborting;
            run.Cts.Cancel();
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.abort", idempotencyKey));
    }

    public Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct) {
        if (_runs.TryGetValue(id, out var run)) {
            run.State = SequenceRunState.Stopped;
            run.Cts.Cancel();
        }
        return Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.stop", idempotencyKey));
    }

    private async Task RunWorkerAsync(Guid sequenceId, RunState run) {
        try {
            run.State = SequenceRunState.Running;
            await EmitAsync("sequence.started", sequenceId, run);
            for (var i = 0; i < MockInstructionCount; i++) {
                if (run.Cts.IsCancellationRequested) break;
                while (run.State == SequenceRunState.Paused && !run.Cts.IsCancellationRequested) {
                    await Task.Delay(100, run.Cts.Token).ContinueWith(_ => { });
                }
                if (run.Cts.IsCancellationRequested) break;

                run.CurrentInstructionIndex = i;
                run.CurrentInstructionDescription = $"capture #{i + 1}";
                await EmitAsync("sequence.instruction_started", sequenceId, run);

                try { await Task.Delay(MockInstructionDurationMs, run.Cts.Token); }
                catch (OperationCanceledException) { break; }

                run.FramesCompleted = i + 1;
                await EmitAsync("sequence.instruction_complete", sequenceId, run);
                await EmitAsync("sequence.progress", sequenceId, run);
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
        } catch (Exception) {
            run.State = SequenceRunState.Failed;
            await EmitAsync("sequence.failed", sequenceId, run);
        }
    }

    private async Task EmitAsync(string eventType, Guid sequenceId, RunState run) {
        if (_ws is null) return;
        try {
            var json = $$"""
                {"sequence_id":"{{sequenceId}}","run_id":"{{run.RunId}}","state":"{{run.State.ToString().ToLowerInvariant()}}","current_instruction_index":{{(run.CurrentInstructionIndex.HasValue ? run.CurrentInstructionIndex.Value.ToString() : "null")}},"frames_completed":{{run.FramesCompleted}},"frames_total":{{MockInstructionCount}}}
                """;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch {
            // WS best-effort.
        }
    }

    private sealed class RunState {
        public Guid RunId { get; } = Guid.NewGuid();
        public SequenceRunState State { get; set; } = SequenceRunState.Starting;
        public int? CurrentInstructionIndex { get; set; }
        public string? CurrentInstructionDescription { get; set; }
        public int FramesCompleted { get; set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        public SequenceRunStateDto ToDto(Guid sequenceId) => new(
            SequenceId: sequenceId,
            RunId: RunId,
            State: State,
            CurrentInstructionIndex: CurrentInstructionIndex,
            CurrentTargetName: null,
            StartedUtc: StartedUtc,
            CompletedUtc: CompletedUtc,
            FramesCompleted: FramesCompleted,
            FramesTotal: MockInstructionCount,
            CurrentInstructionDescription: CurrentInstructionDescription);
    }
}
