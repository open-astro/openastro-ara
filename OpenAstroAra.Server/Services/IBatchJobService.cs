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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §65.5 in-memory batch-job tracker. Backs the /api/v1/jobs/{id}
/// status endpoints + the various enqueue operations that return a
/// job_id (currently only session restretch). Jobs are ephemeral —
/// state resets on daemon restart; users have to re-enqueue.
///
/// One job per JobType at a time (rate-limited per §65.5). Second
/// enqueue while one is running returns the running job's id rather
/// than starting a new one — caller decides whether that's good
/// enough or to wait + retry.
/// </summary>
public interface IBatchJobService {
    /// <summary>Enqueue a job of <paramref name="jobType"/>. One live job per type (§65.5): a second
    /// enqueue while one is queued/running returns that job instead of starting another. With an
    /// <paramref name="identity"/> (#1149: what the job is for, e.g. the centering target), a live
    /// job of the same type but a different identity is a <see cref="BatchJobConflictException"/>
    /// rather than a silent join; null identities always join.</summary>
    BatchJobDto Enqueue(string jobType, int totalSteps, Func<Action<int>, CancellationToken, Task> work, string? identity = null);
    BatchJobDto? GetJob(Guid jobId);
    bool TryCancel(Guid jobId);
}

/// <summary>#1149 — a live job of the same type exists for a different identity (e.g. a centering
/// job for other coordinates); the caller decides whether to report it (409) or cancel it first.</summary>
public sealed class BatchJobConflictException : InvalidOperationException {
    public BatchJobConflictException(Guid runningJobId, string? runningIdentity, string requestedIdentity)
        : base($"a '{runningIdentity}' job is already running ({runningJobId}); '{requestedIdentity}' was requested") {
        RunningJobId = runningJobId;
        RunningIdentity = runningIdentity;
        RequestedIdentity = requestedIdentity;
    }

    public BatchJobConflictException() { }

    public BatchJobConflictException(string message) : base(message) { }

    public BatchJobConflictException(string message, Exception innerException) : base(message, innerException) { }

    public Guid RunningJobId { get; }
    public string? RunningIdentity { get; }
    public string? RequestedIdentity { get; }
}
