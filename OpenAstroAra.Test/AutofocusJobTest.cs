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
using OpenAstroAra.Server.Endpoints;
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

        // The endpoint's own work body (EquipmentEndpoints.AutofocusJobWork), not a copy.
        private static Func<Action<int>, CancellationToken, Task> Work(
                IAutofocusExecutor autofocus, int totalProbes = 1, AutofocusRunTracker? tracker = null) =>
            EquipmentEndpoints.AutofocusJobWork(autofocus, tracker ?? new AutofocusRunTracker(), totalProbes);

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
        public async Task Structured_sweep_progress_ticks_the_job_per_probe() {
            // The sweep reports Progress/MaxProgress per probe; the job body maps
            // those onto done/total so a polling client sees 3/9, not 0→1.
            var jobs = new InMemoryBatchJobService(null);
            var executor = new Mock<IAutofocusExecutor>();
            var probesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.Setup(e => e.RunAutofocusAsync(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IProgress<ApplicationStatus> p, CancellationToken _) => {
                    for (var i = 1; i <= 9; i++) {
                        p.Report(new ApplicationStatus {
                            Progress = i,
                            MaxProgress = 9,
                            ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue,
                        });
                    }
                    probesSeen.TrySetResult();
                    return true;
                });
            var job = jobs.Enqueue("autofocus", 9, Work(executor.Object, totalProbes: 9));
            await probesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("complete"));
            Assert.That(final.Total, Is.EqualTo(9));
            Assert.That(final.Done, Is.EqualTo(9));
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
        public async Task Failed_sweep_carries_the_run_records_reason() {
            var tracker = new AutofocusRunTracker();
            using var runCts = new CancellationTokenSource();
            tracker.Begin("classic", 10_000, 9, null, null, runCts);
            tracker.Fail("only 2 of 9 probes had measurable stars", restoredPosition: 10_000);
            var jobs = new InMemoryBatchJobService(null);
            var job = jobs.Enqueue("autofocus", 1, Work(Executor(result: false).Object, tracker: tracker));
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("failed"));
            Assert.That(final.ErrorMessage, Is.EqualTo("Autofocus failed: only 2 of 9 probes had measurable stars"));
        }

        [Test]
        public async Task Sweep_cancelled_from_the_pane_ends_the_job_cancelled_not_failed() {
            // POST /api/v1/autofocus/cancel stops the sweep through the run record without cancelling
            // the job's own token; the job must still land as cancelled.
            var tracker = new AutofocusRunTracker();
            using var runCts = new CancellationTokenSource();
            tracker.Begin("classic", 10_000, 9, null, null, runCts);
            tracker.Cancel(restoredPosition: 10_000);
            var jobs = new InMemoryBatchJobService(null);
            var job = jobs.Enqueue("autofocus", 1, Work(Executor(result: false).Object, tracker: tracker));
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("cancelled"));
        }

        [Test]
        public async Task Tick_guard_never_regresses_or_exceeds_total() {
            // The service-level invariant that closes both review races: a delayed
            // report (smaller value after the settle) is ignored, and a sweep whose
            // live probe count outgrew the enqueue-time total clamps at total —
            // done can never end up > total on a terminal job.
            var jobs = new InMemoryBatchJobService(null);
            var job = jobs.Enqueue("autofocus", 9, async (tick, ct) => {
                tick(21);  // live sweep grew (Steps changed while queued) → clamps to 9
                tick(3);   // delayed straggler → ignored (monotone)
                await Task.CompletedTask;
            });
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("complete"));
            Assert.That(final.Total, Is.EqualTo(9));
            Assert.That(final.Done, Is.EqualTo(9));
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
