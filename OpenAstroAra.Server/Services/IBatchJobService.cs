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
    BatchJobDto Enqueue(string jobType, int totalSteps, Func<Action<int>, CancellationToken, Task> work);
    BatchJobDto? GetJob(Guid jobId);
    bool TryCancel(Guid jobId);
}