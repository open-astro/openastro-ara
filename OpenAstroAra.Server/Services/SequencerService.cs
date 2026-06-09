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
using System.Text.Json;

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
        var existing = _runs.GetValueOrDefault(id);
        if (existing is not null && existing.State is SequenceRunState.Running or SequenceRunState.Paused or SequenceRunState.Starting) {
            // Already running — idempotent: return the same operation rather
            // than spawning a second worker over the same sequence.
            return PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey);
        }

        // Load + deserialize the saved body. Frames-total comes from the
        // instruction count so WILMA can size its progress UI.
        ISequenceRootContainer? root = null;
        int instructionCount = 0;
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is not null) {
            var dto = await sequences.GetAsync(id, ct);
            if (dto is not null) {
                instructionCount = SequenceBodyInspector.Inspect(dto.Body).InstructionCount;
                root = ToRootContainer(dto.Body);
            }
        }

        var run = new RunState(instructionCount);
        _runs[id] = run;

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
            run.State = SequenceRunState.Running;
            await EmitAsync("sequence.started", sequenceId, run);

            var progress = new Progress<ApplicationStatus>(status => {
                if (!string.IsNullOrEmpty(status.Status)) {
                    run.CurrentInstructionDescription = status.Status;
                }
                _ = EmitAsync("sequence.progress", sequenceId, run);
                _checkpoint?.Write(run.ToDto(sequenceId));
            });

            // Real execution. Sequencer.Start runs MainContainer.Run with
            // Initialize/Teardown and swallows OperationCanceledException, so
            // cancellation surfaces via run.Cts below rather than as a throw.
            var sequencer = new OpenAstroAra.Sequencer.Sequencer(root);
            await sequencer.Start(progress, skipIssuePrompt: true, run.Cts.Token);

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
            } else {
                run.State = SequenceRunState.Completed;
                run.CompletedUtc = DateTimeOffset.UtcNow;
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
        }
    }

    private async Task EmitAsync(string eventType, Guid sequenceId, RunState run) {
        if (_ws is null) return;
        try {
            var json = $$"""
                {"sequence_id":"{{sequenceId}}","run_id":"{{run.RunId}}","state":"{{run.State.ToString().ToLowerInvariant()}}","current_instruction_index":{{(run.CurrentInstructionIndex.HasValue ? run.CurrentInstructionIndex.Value.ToString() : "null")}},"frames_completed":{{run.FramesCompleted}},"frames_total":{{run.InstructionCount}}}
                """;
            using var doc = JsonDocument.Parse(json);
            await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ObjectDisposedException) {
            // WS is best-effort; a failed publish must not affect the run.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sequence run {SequenceId} failed")]
    private partial void LogRunFailed(Exception ex, Guid sequenceId);

    private sealed class RunState {
        public Guid RunId { get; } = Guid.NewGuid();
        public SequenceRunState State { get; set; } = SequenceRunState.Starting;
        public int? CurrentInstructionIndex { get; set; }
        public string? CurrentInstructionDescription { get; set; }
        public int FramesCompleted { get; set; }
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
            FramesCompleted: FramesCompleted,
            FramesTotal: InstructionCount,
            CurrentInstructionDescription: CurrentInstructionDescription);
    }
}
