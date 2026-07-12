#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §65.5 in-memory job tracker. ConcurrentDictionary keyed by Guid;
/// each entry holds a CancellationTokenSource that lets DELETE
/// /api/v1/jobs/{id} abort the worker.
/// </summary>
public sealed partial class InMemoryBatchJobService : IBatchJobService {
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, Guid> _activeByType = new();
    // Guards the single-job-per-type check-then-claim so two concurrent
    // Enqueue calls for the same type can't both slip past the "already
    // running?" test and spawn duplicate workers (the §65.5 policy is one
    // live job per type).
    private readonly object _enqueueLock = new();
    // How long a terminal job (complete/failed/cancelled) stays queryable
    // before it's evicted from the registry. Without a retention window the
    // registry grows unbounded for the lifetime of the daemon.
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);
    private readonly ILogger<InMemoryBatchJobService> _logger;

    public InMemoryBatchJobService(ILogger<InMemoryBatchJobService>? logger) {
        _logger = logger ?? NullLogger<InMemoryBatchJobService>.Instance;
    }

    // A worker that swallows any exception into the job's failed-state must
    // catch broadly (last-resort net) — a non-allowlisted throw would otherwise
    // strand the job at "running" forever and leak its slot.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget worker: every failure must land on the job as 'failed' rather than " +
            "faulting the unobserved task and leaving the job wedged at 'running'.")]
    public BatchJobDto Enqueue(string jobType, int totalSteps, Func<Action<int>, CancellationToken, Task> work) {
        var jobId = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        var state = new JobState {
            JobId = jobId,
            JobType = jobType,
            Total = Math.Max(0, totalSteps),
            Done = 0,
            State = "queued",
            StartedUtc = DateTimeOffset.UtcNow,
            FinishedUtc = null,
            ErrorMessage = null,
            Cts = cts,
        };

        // §65.5 single-job-per-type policy, claimed atomically. If one is already
        // queued/running for this type, surface its id and drop the just-built
        // (never-started) job rather than spawning a duplicate.
        lock (_enqueueLock) {
            PruneTerminal();
            if (_activeByType.TryGetValue(jobType, out var existing) &&
                _jobs.TryGetValue(existing, out var existingState)) {
                string existingPhase;
                lock (existingState) { existingPhase = existingState.State; }
                if (existingPhase is "queued" or "running") {
                    cts.Dispose();
                    return Snapshot(existingState);
                }
            }
            _jobs[jobId] = state;
            _activeByType[jobType] = jobId;
        }

        _ = Task.Run(async () => {
            try {
                lock (state) { state.State = "running"; }
                // Tick invariant for EVERY job: done is monotone (a delayed
                // Progress<T> callback queued to the pool can land after a later
                // tick — it must never regress the count) and clamped to Total
                // (a worker whose own step count drifted from the enqueue-time
                // total must never leave a terminal job showing done > total).
                await work(done => {
                    lock (state) {
                        var clamped = state.Total > 0 ? Math.Min(done, state.Total) : done;
                        if (clamped > state.Done) {
                            state.Done = clamped;
                        }
                    }
                }, cts.Token);
                // Work returned WITHOUT throwing → it ran to completion. Never
                // relabel that as cancelled just because a cancel raced in at the
                // finish line; only a thrown OperationCanceledException means the
                // work actually stopped early.
                lock (state) { state.State = "complete"; }
            } catch (OperationCanceledException) {
                lock (state) { state.State = "cancelled"; }
            } catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
                                          or SqliteException or JsonException or OpenAstroAra.Fits.FitsException) {
                // Batch work is DB + image + WS publish (see the §65.7 restretch job);
                // record the failure on the job rather than faulting the worker task.
                lock (state) {
                    state.State = "failed";
                    state.ErrorMessage = ex.Message;
                }
                LogJobFailed(ex, jobId, jobType);
            } catch (Exception ex) {
                // Last-resort net for anything outside the allowlist — a stray throw
                // must still land the job in 'failed', never leave it stuck 'running'.
                lock (state) {
                    state.State = "failed";
                    state.ErrorMessage = ex.Message;
                }
                LogJobFailed(ex, jobId, jobType);
            } finally {
                lock (state) { state.FinishedUtc = DateTimeOffset.UtcNow; }
                _activeByType.TryRemove(new KeyValuePair<string, Guid>(jobType, jobId));
                cts.Dispose();
            }
        });

        return Snapshot(state);
    }

    public BatchJobDto? GetJob(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var state) ? Snapshot(state) : null;

    // Evict jobs that reached a terminal state longer than the retention window
    // ago. Called under _enqueueLock. Their CTS was already disposed in the
    // worker's finally, so eviction is a plain dictionary removal.
    private void PruneTerminal() {
        var cutoff = DateTimeOffset.UtcNow - TerminalRetention;
        foreach (var kvp in _jobs) {
            var s = kvp.Value;
            bool evict;
            lock (s) {
                evict = s.State is "complete" or "failed" or "cancelled"
                    && s.FinishedUtc is { } finished && finished < cutoff;
            }
            if (evict) {
                _jobs.TryRemove(kvp.Key, out _);
            }
        }
    }

    public bool TryCancel(Guid jobId) {
        if (!_jobs.TryGetValue(jobId, out var state)) return false;
        lock (state) {
            if (state.State is "complete" or "failed" or "cancelled") return false;
        }
        try {
            state.Cts.Cancel();
            return true;
        } catch (ObjectDisposedException) {
            return false;
        }
    }

    // Read the mutable fields under the per-job lock so a caller can't observe a
    // torn view (e.g. State updated but FinishedUtc not yet, or a half-written
    // Done) while the worker is transitioning the job.
    private static BatchJobDto Snapshot(JobState s) {
        lock (s) {
            return new(
                JobId: s.JobId,
                JobType: s.JobType,
                State: s.State,
                Done: s.Done,
                Total: s.Total,
                StartedUtc: s.StartedUtc,
                FinishedUtc: s.FinishedUtc,
                ErrorMessage: s.ErrorMessage);
        }
    }

    private sealed class JobState {
        public Guid JobId { get; set; }
        public string JobType { get; set; } = "";
        public string State { get; set; } = "queued";
        public int Done { get; set; }
        public int Total { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public string? ErrorMessage { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Batch job {JobId} ({JobType}) failed")]
    private partial void LogJobFailed(Exception ex, Guid jobId, string jobType);
}