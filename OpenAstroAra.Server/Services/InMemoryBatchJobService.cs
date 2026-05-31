#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §65.5 in-memory job tracker. ConcurrentDictionary keyed by Guid;
/// each entry holds a CancellationTokenSource that lets DELETE
/// /api/v1/jobs/{id} abort the worker.
/// </summary>
public sealed class InMemoryBatchJobService : IBatchJobService {
    private readonly ConcurrentDictionary<Guid, JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, Guid> _activeByType = new();
    private readonly ILogger<InMemoryBatchJobService>? _logger;

    public InMemoryBatchJobService(ILogger<InMemoryBatchJobService>? logger) {
        _logger = logger;
    }

    public BatchJobDto Enqueue(string jobType, int totalSteps, Func<Action<int>, CancellationToken, Task> work) {
        // §65.5 single-job-per-type policy. If one is already running for
        // this type, surface the running job's id rather than spawning a
        // duplicate.
        if (_activeByType.TryGetValue(jobType, out var existing) &&
            _jobs.TryGetValue(existing, out var existingState) &&
            (existingState.State == "queued" || existingState.State == "running")) {
            return Snapshot(existingState);
        }

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
        _jobs[jobId] = state;
        _activeByType[jobType] = jobId;

        _ = Task.Run(async () => {
            try {
                state.State = "running";
                await work(done => state.Done = done, cts.Token);
                if (cts.Token.IsCancellationRequested) {
                    state.State = "cancelled";
                } else {
                    state.State = "complete";
                }
            } catch (OperationCanceledException) {
                state.State = "cancelled";
            } catch (Exception ex) {
                state.State = "failed";
                state.ErrorMessage = ex.Message;
                _logger?.LogError(ex, "Batch job {JobId} ({JobType}) failed", jobId, jobType);
            } finally {
                state.FinishedUtc = DateTimeOffset.UtcNow;
                _activeByType.TryRemove(new KeyValuePair<string, Guid>(jobType, jobId));
            }
        });

        return Snapshot(state);
    }

    public BatchJobDto? Get(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var state) ? Snapshot(state) : null;

    public bool TryCancel(Guid jobId) {
        if (!_jobs.TryGetValue(jobId, out var state)) return false;
        if (state.State is "complete" or "failed" or "cancelled") return false;
        try {
            state.Cts.Cancel();
            return true;
        } catch (ObjectDisposedException) {
            return false;
        }
    }

    private static BatchJobDto Snapshot(JobState s) => new(
        JobId: s.JobId,
        JobType: s.JobType,
        State: s.State,
        Done: s.Done,
        Total: s.Total,
        StartedUtc: s.StartedUtc,
        FinishedUtc: s.FinishedUtc,
        ErrorMessage: s.ErrorMessage);

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
}
