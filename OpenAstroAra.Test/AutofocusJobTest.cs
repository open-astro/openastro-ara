#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59 — the POST /equipment/focuser/autofocus job body: a sweep runs as a §65.5
    /// background job, a failed sweep surfaces as a Failed job with the reason, and the
    /// single-job-per-type policy makes a second request join the running sweep.
    /// </summary>
    [TestFixture]
    public class AutofocusJobTest {

        // The exact work body the endpoint enqueues (kept in lockstep — the endpoint lambda
        // itself is a thin Results.Accepted wrapper smoke-tested by CI's runtime job).
        private static Func<Action<int>, CancellationToken, Task> Work(IAutofocusExecutor autofocus) =>
            async (tick, ct) => {
                var ok = await autofocus.RunAutofocusAsync(new Progress<ApplicationStatus>(), ct);
                if (!ok) {
                    throw new InvalidOperationException(
                        "Autofocus sweep failed — see the daemon log (probe quality, curve fit, or focuser fault).");
                }
                tick(1);
            };

        private static Mock<IAutofocusExecutor> Executor(bool result, TimeSpan? delay = null) {
            var executor = new Mock<IAutofocusExecutor>();
            executor.Setup(e => e.RunAutofocusAsync(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IProgress<ApplicationStatus> _, CancellationToken ct) => {
                    if (delay is { } d) await Task.Delay(d, ct);
                    return result;
                });
            return executor;
        }

        private static async Task<BatchJobDto> WaitForTerminalAsync(InMemoryBatchJobService jobs, Guid id) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var job = jobs.GetJob(id);
                if (job is not null && job.State is "complete" or "failed" or "cancelled") return job;
                await Task.Delay(20);
            }
            return jobs.GetJob(id)!;
        }

        [Test]
        public async Task Successful_sweep_completes_the_job() {
            var jobs = new InMemoryBatchJobService(null);
            var job = jobs.Enqueue("autofocus", 1, Work(Executor(result: true).Object));
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("complete"));
            Assert.That(final.Done, Is.EqualTo(1));
        }

        [Test]
        public async Task Failed_sweep_fails_the_job_with_the_reason() {
            var jobs = new InMemoryBatchJobService(null);
            var job = jobs.Enqueue("autofocus", 1, Work(Executor(result: false).Object));
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("failed"));
            Assert.That(final.ErrorMessage, Does.Contain("Autofocus sweep failed"));
        }

        [Test]
        public async Task Second_request_while_running_joins_the_same_job() {
            var jobs = new InMemoryBatchJobService(null);
            var executor = Executor(result: true, delay: TimeSpan.FromSeconds(2));
            var first = jobs.Enqueue("autofocus", 1, Work(executor.Object));
            var second = jobs.Enqueue("autofocus", 1, Work(executor.Object));
            Assert.That(second.JobId, Is.EqualTo(first.JobId), "single-job-per-type: a duplicate POST joins the running sweep");
            jobs.TryCancel(first.JobId); // don't leave the delayed sweep running past the test
            await WaitForTerminalAsync(jobs, first.JobId);
        }
    }
}
